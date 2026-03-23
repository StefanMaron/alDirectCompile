using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

/// <summary>
/// .NET Startup Hook that patches the BC service tier to run on Linux.
///
/// Patch #1: CustomTranslationResolver (Nav.Language.dll)
///   Stack overflow from recursive satellite assembly resolution when WindowsIdentity
///   throws PlatformNotSupportedException. Fix: no-op OnAppDomainAssemblyResolve and
///   ResolveSatelliteAssembly.
///
/// Patch #2: NavEnvironment (Nav.Ncl.dll)
///   Static field initializer calls WindowsIdentity.GetCurrent() which throws on Linux.
///   Fix: Replace the entire .cctor with one that initializes fields without WindowsIdentity.
///   Also hook ServiceAccount/ServiceAccountName properties that dereference the null field.
///
/// Patch #3: kernel32.dll P/Invoke interception (all assemblies)
///   Provides stub implementations of kernel32 functions (JobObject, EventLog, etc.)
///   via a compiled C shared library + NativeLibrary.ResolvingUnmanagedDll.
///
/// Patch #4: EventLogWriter (Nav.Types.dll)
///   System.Diagnostics.EventLog throws PlatformNotSupportedException on Linux.
///   Fix: No-op NavEventLogEntryWriter.WriteEntry so event log writes are silently dropped.
///
/// Patch #5: ETW/OpenTelemetry (Nav.Ncl.dll + Nav.Types.dll)
///   Geneva ETW exporter and EtwTelemetryLog require Windows ETW subsystem.
///   Fix: No-op NavOpenTelemetryLogger constructor, pre-set TraceWriter to no-op proxy.
///
/// JMP hooks work ONLY on BC methods (JIT-compiled). BCL methods are ReadyToRun pre-compiled
/// and cannot be patched this way.
/// </summary>
internal class StartupHook
{
    private static bool _patchedLanguage;
    private static bool _patchedNcl;
    private static bool _patchedTypes;
    private static Type? _navEnvironmentType;
    private static Assembly? _navNclAssembly;
    private static IntPtr _kernel32StubHandle;
    private static object? _noopEncryptionProvider;
    private static bool _encryptionBypassed;
    private static bool _encryptionApplying;
    private static object? _originalTopology;

    public static void Initialize()
    {
        // Patch #6: Must be set before ANY System.Drawing type is accessed
        AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);


        Console.WriteLine("[StartupHook] Initializing Linux compatibility patches...");

        // Patch #3: Load kernel32 stubs for P/Invoke interception
        LoadKernel32Stubs();


        // Replace DLLs with stubs or cross-platform versions (unsigned, can copy directly)
        ReplaceWithStub("OpenTelemetry.Exporter.Geneva.dll", "Geneva ETW exporter");
        ReplaceWithStub("Microsoft.Data.SqlClient.dll", "cross-platform SqlClient");

        // Patch #6: System.Drawing requires strong name bypass — use assembly resolver
        // Register managed assembly resolver (once, for all stubs below)
        AssemblyLoadContext.Default.Resolving += ResolveStubAssembly;
        SetupStubWithResolver("System.Drawing.Common");
        SetupStubWithResolver("System.Diagnostics.PerformanceCounter");
        SetupStubWithResolver("System.Security.Principal.Windows");

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        TryEagerPatch();

        Console.WriteLine("[StartupHook] Initialization complete.");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        string? name = args.LoadedAssembly.GetName().Name;

        if (!_patchedLanguage && name == "Microsoft.Dynamics.Nav.Language")
        {
            Console.WriteLine("[StartupHook] Nav.Language.dll loaded — patching");
            PatchCustomTranslationResolver(args.LoadedAssembly);
        }

        if (!_patchedNcl && name == "Microsoft.Dynamics.Nav.Ncl")
        {
            Console.WriteLine("[StartupHook] Nav.Ncl.dll loaded — patching");
            PatchNavEnvironment(args.LoadedAssembly);
        }

        // Re-apply encryption bypass after Main() overrides it.
        // Guard against recursion (DispatchProxy.Create triggers assembly loads).
        if (!_encryptionBypassed && !_encryptionApplying && name == "Microsoft.Dynamics.Nav.Core")
        {
            _encryptionApplying = true;
            try { ReapplyEncryptionBypass(); }
            finally { _encryptionApplying = false; }
        }
        if (name == "Microsoft.Dynamics.Nav.Core")
        {
            ReapplyTopologyProxy();
        }

        if (!_patchedTypes && name == "Microsoft.Dynamics.Nav.Types")
        {
            Console.WriteLine("[StartupHook] Nav.Types.dll loaded — patching");
            PatchNavTypes(args.LoadedAssembly);
        }

    }

    private static void TryEagerPatch()
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        TryEagerLoadAndPatch(baseDir, "Microsoft.Dynamics.Nav.Language.dll",
            "Nav.Language.dll", PatchCustomTranslationResolver, () => _patchedLanguage);
        // DON'T eagerly load Nav.Ncl.dll and Nav.Types.dll — let the runtime load them
        // naturally. Eager LoadFrom creates a separate instance that the runtime doesn't use,
        // which is why JMP hooks on many methods fail (they patch the wrong instance).
        // The AssemblyLoad event handler will catch them when the runtime loads them.
    }

    private static void TryEagerLoadAndPatch(string baseDir, string fileName,
        string displayName, Action<Assembly> patchAction, Func<bool> isPatched)
    {
        if (isPatched()) return;

        string path = System.IO.Path.Combine(baseDir, fileName);
        if (!System.IO.File.Exists(path))
        {
            Console.WriteLine($"[StartupHook] {displayName} not found at base dir — will patch on load");
            return;
        }

        try
        {
            Assembly asm = Assembly.LoadFrom(path);
            Console.WriteLine($"[StartupHook] Eagerly loaded {displayName}");
            patchAction(asm);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Eager load {displayName} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #3: kernel32.dll stub for P/Invoke interception
    // ========================================================================

    /// <summary>
    /// Load a compiled C stub library that provides no-op implementations of Windows DLL
    /// functions (kernel32, user32, advapi32, etc.). Register via ResolvingUnmanagedDll so ALL
    /// assemblies get the stubs when they P/Invoke Windows DLLs on Linux.
    /// </summary>
    private static void LoadKernel32Stubs()
    {
        try
        {
            var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
            if (hookDir == null) return;

            var stubPath = Path.Combine(hookDir, "libwin32_stubs.so");
            if (!File.Exists(stubPath))
            {
                Console.WriteLine($"[StartupHook] libwin32_stubs.so not found at {hookDir}");
                Console.WriteLine("[StartupHook] Build with: dotnet publish -c Release -o bin/Release/net8.0/publish");
                return;
            }

            _kernel32StubHandle = NativeLibrary.Load(stubPath);
            Console.WriteLine("[StartupHook] Loaded Win32 stubs (kernel32/user32/advapi32/...)");

            // Intercept kernel32.dll resolution for ALL assemblies
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveWin32Stubs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Kernel32 stub load failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Windows DLLs that we provide stub implementations for
    private static readonly string[] _stubbedLibraries = new[]
    {
        "kernel32", "kernel32.dll",
        "user32", "user32.dll",
        "Wintrust", "Wintrust.dll", "wintrust", "wintrust.dll",
        "nclcsrts", "nclcsrts.dll",
        "dhcpcsvc", "dhcpcsvc.dll",
        "Netapi32", "Netapi32.dll", "netapi32", "netapi32.dll",
        "ntdsapi", "ntdsapi.dll",
        "rpcrt4", "rpcrt4.dll",
        "advapi32", "advapi32.dll",
        "httpapi", "httpapi.dll",
        "gdiplus", "libgdiplus", "libgdiplus.so", "libgdiplus.so.0",
    };

    private static IntPtr ResolveWin32Stubs(Assembly assembly, string libraryName)
    {
        if (_kernel32StubHandle != IntPtr.Zero)
        {
            foreach (var name in _stubbedLibraries)
            {
                if (libraryName == name)
                    return _kernel32StubHandle;
            }
        }
        return IntPtr.Zero;
    }

    // ========================================================================
    // Patch #1: CustomTranslationResolver — breaks satellite assembly recursion
    // ========================================================================

    private static void PatchCustomTranslationResolver(Assembly navLanguage)
    {
        if (_patchedLanguage) return;

        try
        {
            Type? resolverType = navLanguage.GetType("Microsoft.Dynamics.Nav.Common.CustomTranslationResolver");
            if (resolverType == null)
            {
                Console.WriteLine("[StartupHook] CustomTranslationResolver type not found");
                return;
            }

            var onResolve = resolverType.GetMethod("OnAppDomainAssemblyResolve",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (onResolve != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_OnAppDomainAssemblyResolve),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(onResolve, replacement, "OnAppDomainAssemblyResolve");
            }

            var resolveSat = resolverType.GetMethod("ResolveSatelliteAssembly",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (resolveSat != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_ResolveSatelliteAssembly),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(resolveSat, replacement, "ResolveSatelliteAssembly");
            }

            _patchedLanguage = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #1 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #2: NavEnvironment — skip WindowsIdentity in static constructor
    // ========================================================================

    private static void PatchNavEnvironment(Assembly navNcl)
    {
        if (_patchedNcl) return;

        try
        {
            Type? envType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
            if (envType == null)
            {
                Console.WriteLine("[StartupHook] NavEnvironment type not found");
                return;
            }

            _navEnvironmentType = envType;
            _navNclAssembly = navNcl;

            // Hook the .cctor — replaces the static constructor entirely
            var cctor = envType.TypeInitializer;
            if (cctor != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_NavEnvironmentCctor),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(cctor, replacement, "NavEnvironment..cctor");
            }
            else
            {
                Console.WriteLine("[StartupHook] NavEnvironment has no .cctor — nothing to patch");
            }

            // Hook ServiceAccount property (returns SecurityIdentifier from serviceAccount.User)
            var saProp = envType.GetProperty("ServiceAccount", BindingFlags.Public | BindingFlags.Static);
            if (saProp?.GetMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_GetServiceAccount),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(saProp.GetMethod, replacement, "NavEnvironment.get_ServiceAccount");
            }

            // Hook ServiceAccountName property (returns serviceAccount.Name)
            var sanProp = envType.GetProperty("ServiceAccountName", BindingFlags.Public | BindingFlags.Static);
            if (sanProp?.GetMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_GetServiceAccountName),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(sanProp.GetMethod, replacement, "NavEnvironment.get_ServiceAccountName");
            }

            // Patch #3 (kernel32.dll) is handled globally via NativeLibrary resolver

            // --- Patch #13: VerifyTestExecutionEnabled — allow test execution on onprem/production ---
            var testRunnerType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution");
            if (testRunnerType != null)
            {
                // Search all methods including inherited
                MethodInfo? verifyMethod = null;
                foreach (var m in testRunnerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.FlattenHierarchy))
                {
                    if (m.Name == "VerifyTestExecutionEnabled")
                    {
                        verifyMethod = m;
                        break;
                    }
                }
                if (verifyMethod != null)
                {
                    var replacement = typeof(StartupHook).GetMethod(nameof(Replacement_ReturnVoid),
                        BindingFlags.Public | BindingFlags.Static)!;
                    ApplyJmpHook(verifyMethod, replacement, "NavTestRunnerCodeUnit.VerifyTestExecutionEnabled");
                }
                else
                {
                    var methods = testRunnerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var m in methods) names.Add(m.Name);
                    Console.WriteLine($"[StartupHook] VerifyTestExecutionEnabled not found in {testRunnerType.FullName}. Methods: {string.Join(", ", names)}");
                }
            }
            else
                Console.WriteLine("[StartupHook] NavTestRunnerCodeUnit not found");

            // --- Patch #6: EmitServerStartupTraceEvents — contains System.Drawing font enum ---
            var emitMethod = envType.GetMethod("EmitServerStartupTraceEvents",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (emitMethod != null)
            {
                var replacement = typeof(StartupHook).GetMethod(
                    emitMethod.IsStatic ? nameof(Replacement_NoOp_2Args) : nameof(Replacement_NoOp_3Args),
                    BindingFlags.Public | BindingFlags.Static)!;
                ApplyJmpHook(emitMethod, replacement, "NavEnvironment.EmitServerStartupTraceEvents");
            }

            // --- Patch #9: Replace Topology with one that returns IsServiceRunningInLocalEnvironment=false ---
            // This makes NavDirectorySecurity skip Windows ACL APIs (returns null instead).
            Type? topoIfaceType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.IServiceTopology");
            if (topoIfaceType != null)
            {
                // Create a proxy that returns false for IsServiceRunningInLocalEnvironment
                // and delegates everything else to the original topology
                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(topoIfaceType, typeof(LinuxTopologyProxy));
                var linuxTopology = createProxy.Invoke(null, null);

                // Store original topology for delegation, then replace
                var topoProp = envType.GetProperty("Topology", BindingFlags.Public | BindingFlags.Static);
                if (topoProp != null)
                {
                    _originalTopology = topoProp.GetValue(null);
                    topoProp.SetValue(null, linuxTopology);
                    Console.WriteLine("[StartupHook] Replaced Topology with Linux proxy (ACL bypass)");
                }
            }

            _patchedNcl = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #2/#3 failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[StartupHook]   {ex.StackTrace}");
        }
    }

    // ========================================================================
    // Patch #4: NavTypes — no-op EventLog writer
    // ========================================================================

    private static void PatchNavTypes(Assembly navTypes)
    {
        if (_patchedTypes) return;

        try
        {
            // Replace the EventLogWriter's IEventLogEntryWriter with a no-op proxy.
            // JMP hooks don't reliably intercept here (JIT inlining), so we replace
            // the writer instance via the public settable property instead.
            Type? eventLogWriterType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.EventLogWriter");
            Type? ifaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.IEventLogEntryWriter");

            if (eventLogWriterType != null && ifaceType != null)
            {
                // Create a no-op proxy implementing IEventLogEntryWriter
                // Use genericParameterCount overload to avoid AmbiguousMatchException
                var createMethod = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(ifaceType, typeof(NoOpDispatchProxy));
                var noopWriter = createMethod.Invoke(null, null);

                // Replace the static field
                var field = eventLogWriterType.GetField("eventLogEntryWriter",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(null, noopWriter);
                    Console.WriteLine("[StartupHook] Replaced EventLogWriter with no-op proxy");
                }
                else
                {
                    // Try the public property setter as fallback
                    var prop = eventLogWriterType.GetProperty("EventLogEntryWriter",
                        BindingFlags.Public | BindingFlags.Static);
                    prop?.SetValue(null, noopWriter);
                    Console.WriteLine("[StartupHook] Replaced EventLogWriter via property setter");
                }
            }
            else
            {
                Console.WriteLine("[StartupHook] EventLogWriter or IEventLogEntryWriter not found");
            }

            // --- Patch #5b: Replace NavDiagnostics.TraceWriter with no-op ---
            // EtwTelemetryLog uses Windows ETW. Replace before NavEnvironment..ctor runs.
            Type? navDiagType = navTypes.GetType("Microsoft.Dynamics.Nav.Diagnostic.NavDiagnostics");
            Type? telemetryIfaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Diagnostics.Telemetry.ITelemetryLogWriter");

            if (navDiagType != null && telemetryIfaceType != null)
            {
                var createTelemetry = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(telemetryIfaceType, typeof(NoOpDispatchProxy));
                var noopTelemetry = createTelemetry.Invoke(null, null);

                var traceWriterField = navDiagType.GetField("traceWriter",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (traceWriterField != null)
                {
                    traceWriterField.SetValue(null, noopTelemetry);
                    Console.WriteLine("[StartupHook] Pre-set NavDiagnostics.TraceWriter to no-op");
                }
            }

            // --- Patch #7: Encryption provider bypass for plain text SQL password ---
            Type? factoryType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.DefaultServerInstanceRsaEncryptionProviderFactory");
            Type? encIfaceType = navTypes.GetType("Microsoft.Dynamics.Nav.Types.ISystemEncryptionProvider");

            if (factoryType != null && encIfaceType != null)
            {
                // Create a pass-through encryption proxy
                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(encIfaceType, typeof(PassthroughEncryptionProxy));
                var noopEncryption = createProxy.Invoke(null, null);

                // Set the factory delegate to return our proxy
                var prop = factoryType.GetProperty("GetDefaultEncryptionProvider",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop != null)
                {
                    // Create Func<ISystemEncryptionProvider> delegate
                    var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                    var capturedProxy = noopEncryption;
                    // Use DynamicMethod to create the delegate
                    var dm = new DynamicMethod("GetNoOpEncryption", encIfaceType, Type.EmptyTypes,
                        typeof(StartupHook).Module, skipVisibility: true);
                    // We can't close over capturedProxy in IL, so store it in a static field
                    _noopEncryptionProvider = noopEncryption;
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                        BindingFlags.Static | BindingFlags.NonPublic)!);
                    il.Emit(OpCodes.Ret);
                    var funcDelegate = dm.CreateDelegate(funcType);

                    prop.SetValue(null, funcDelegate);
                    Console.WriteLine("[StartupHook] Set encryption provider to pass-through (plain text passwords)");
                }
            }

            _patchedTypes = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Patch #4/5/7 failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// DispatchProxy subclass that no-ops all method calls.
    /// Used to create runtime implementations of BC interfaces without compile-time references.
    /// </summary>
    /// <summary>
    /// No-op proxy, but for IEventLogEntryWriter, log to Console instead of Windows Event Log.
    /// </summary>
    public class NoOpDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            // Redirect EventLog writes to Console so we can see errors
            if (targetMethod?.Name == "WriteEntry" && args?.Length >= 2)
            {
                var message = args[1]?.ToString();
                if (message != null && message.Length > 10)
                    Console.Error.WriteLine($"[BC-EventLog] {message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Encryption proxy that passes text through unchanged.
    /// IsKeyPresent returns false, making ProtectedDatabasePassword treat values as plain text.
    /// </summary>
    /// <summary>
    /// Topology proxy that returns false for IsServiceRunningInLocalEnvironment
    /// (bypasses ACL APIs) and delegates everything else to the original topology.
    /// </summary>
    public class LinuxTopologyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_IsServiceRunningInLocalEnvironment")
                return false; // Must be false to skip ACL APIs on Linux
            // Delegate to original topology
            if (_originalTopology != null && targetMethod != null)
            {
                try { return targetMethod.Invoke(_originalTopology, args); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }
            if (targetMethod?.ReturnType == typeof(bool)) return false;
            if (targetMethod?.ReturnType == typeof(string)) return "";
            return null;
        }
    }

    public class PassthroughEncryptionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod?.Name switch
            {
                "Encrypt" or "Decrypt" => args?[0], // pass-through
                "get_IsKeyPresent" or "get_IsKeyCreated" => true,
                "get_PublicKey" => "<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
                _ => null,
            };
            if (targetMethod?.Name == "Decrypt" || targetMethod?.Name == "Encrypt")
            {
                _encryptionBypassed = true;
                Console.WriteLine($"[StartupHook] Encryption.{targetMethod.Name}() called — bypass working");
            }
            return result;
        }
    }

    // ========================================================================
    // Patch #5c: Replace Geneva DLL with no-op stub
    // ========================================================================

    /// <summary>
    /// Replace a DLL in the service directory with a no-op stub from our publish directory.
    /// The original is backed up to .orig.
    /// </summary>
    private static void ReplaceWithStub(string dllName, string description)
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        string targetDll = Path.Combine(baseDir, dllName);
        if (!File.Exists(targetDll)) return;

        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (hookDir == null) return;
        string stubDll = Path.Combine(hookDir, dllName);
        if (!File.Exists(stubDll))
        {
            Console.WriteLine($"[StartupHook] Stub for {dllName} not found — skipping");
            return;
        }

        try
        {
            string backup = targetDll + ".orig";
            if (!File.Exists(backup))
                File.Copy(targetDll, backup, overwrite: false);

            File.Copy(stubDll, targetDll, overwrite: true);
            Console.WriteLine($"[StartupHook] Replaced {dllName} with stub ({description})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] {dllName} replacement failed: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #7b: Re-apply encryption bypass (Main() overrides our initial setting)
    // ========================================================================

    private static void ReapplyEncryptionBypass()
    {
        try
        {
            // Find Nav.Types and Nav.Core assemblies
            Assembly? navTypesAsm = null;
            Assembly? navCoreAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Microsoft.Dynamics.Nav.Types") navTypesAsm = asm;
                if (asm.GetName().Name == "Microsoft.Dynamics.Nav.Core") navCoreAsm = asm;
            }
            if (navTypesAsm == null) return;

            Type? encIfaceType = navTypesAsm.GetType("Microsoft.Dynamics.Nav.Types.ISystemEncryptionProvider");
            if (encIfaceType == null) return;

            var createProxy = typeof(DispatchProxy)
                .GetMethod("Create", 2, Type.EmptyTypes)!
                .MakeGenericMethod(encIfaceType, typeof(PassthroughEncryptionProxy));
            _noopEncryptionProvider = createProxy.Invoke(null, null);

            // Strategy 1: Set the factory delegate
            Type? factoryType = navTypesAsm.GetType("Microsoft.Dynamics.Nav.Types.DefaultServerInstanceRsaEncryptionProviderFactory");
            if (factoryType != null)
            {
                var prop = factoryType.GetProperty("GetDefaultEncryptionProvider",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop != null)
                {
                    var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                    var dm = new DynamicMethod("GetNoOpEncryption2", encIfaceType, Type.EmptyTypes,
                        typeof(StartupHook).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                        BindingFlags.Static | BindingFlags.NonPublic)!);
                    il.Emit(OpCodes.Ret);
                    prop.SetValue(null, dm.CreateDelegate(funcType));
                }
            }

            // Strategy 2: Replace BOTH the instance field AND the public Factory delegate
            // on ServerInstanceRsaEncryptionProvider. Main() uses Factory which defaults to
            // () => Instance. We replace both so all code paths return our proxy.
            if (navCoreAsm != null)
            {
                Type? rsaProvType = navCoreAsm.GetType("Microsoft.Dynamics.Nav.Core.ServerInstanceRsaEncryptionProvider");
                if (rsaProvType != null)
                {
                    // Replace the private 'instance' field (used by Instance getter)
                    var instanceField = rsaProvType.GetField("instance",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (instanceField != null)
                        instanceField.SetValue(null, _noopEncryptionProvider);

                    // Replace the public 'Factory' delegate field
                    var factoryField = rsaProvType.GetField("Factory",
                        BindingFlags.Static | BindingFlags.Public);
                    if (factoryField != null)
                    {
                        var funcType = typeof(Func<>).MakeGenericType(encIfaceType);
                        var dm = new DynamicMethod("GetProxy", encIfaceType, Type.EmptyTypes,
                            typeof(StartupHook).Module, skipVisibility: true);
                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldsfld, typeof(StartupHook).GetField(nameof(_noopEncryptionProvider),
                            BindingFlags.Static | BindingFlags.NonPublic)!);
                        il.Emit(OpCodes.Ret);
                        factoryField.SetValue(null, dm.CreateDelegate(funcType));
                    }

                    Console.WriteLine("[StartupHook] Replaced encryption Instance + Factory");
                }
            }

            Console.WriteLine("[StartupHook] Re-applied encryption bypass (after Nav.Core load)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Encryption re-apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReapplyTopologyProxy()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Microsoft.Dynamics.Nav.Ncl") continue;

                Type? envType = asm.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
                Type? topoIfaceType = asm.GetType("Microsoft.Dynamics.Nav.Runtime.IServiceTopology");
                if (envType == null || topoIfaceType == null) break;

                var topoProp = envType.GetProperty("Topology", BindingFlags.Public | BindingFlags.Static);
                if (topoProp == null) break;

                _originalTopology = topoProp.GetValue(null);

                var createProxy = typeof(DispatchProxy)
                    .GetMethod("Create", 2, Type.EmptyTypes)!
                    .MakeGenericMethod(topoIfaceType, typeof(LinuxTopologyProxy));
                topoProp.SetValue(null, createProxy.Invoke(null, null));

                Console.WriteLine("[StartupHook] Re-applied Linux topology proxy (after Nav.Core load)");
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Topology re-apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Patch #6/#8: Stub DLLs with strong-name bypass via assembly resolver
    // ========================================================================

    private static readonly System.Collections.Generic.Dictionary<string, byte[]> _stubBytesMap = new();

    /// <summary>
    /// Move a signed DLL aside and register an assembly resolver that provides our
    /// unsigned stub via Assembly.Load(byte[]) — bypasses strong-name identity checks.
    /// </summary>
    private static void SetupStubWithResolver(string assemblyName)
    {
        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) return;

        string dllName = assemblyName + ".dll";
        string targetDll = Path.Combine(baseDir, dllName);
        string backup = targetDll + ".orig";

        // If original already removed (container restart), just load stub bytes
        bool originalExists = File.Exists(targetDll);
        bool alreadyMoved = !originalExists && File.Exists(backup);

        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (hookDir == null) return;
        string stubDll = Path.Combine(hookDir, dllName);
        if (!File.Exists(stubDll))
        {
            Console.WriteLine($"[StartupHook] Stub for {assemblyName} not found — skipping");
            return;
        }

        _stubBytesMap[assemblyName] = File.ReadAllBytes(stubDll);

        if (alreadyMoved)
        {
            Console.WriteLine($"[StartupHook] {assemblyName} stub ready (already moved, via resolver)");
            return;
        }

        if (!originalExists) return;

        // Move original aside so default resolution fails → our resolver provides the stub
        try
        {
            if (!File.Exists(backup))
                File.Copy(targetDll, backup, overwrite: false);
            File.Delete(targetDll);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] Could not move {dllName}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[StartupHook] {assemblyName} stub ready (via resolver)");
    }

    private static Assembly? ResolveStubAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != null && _stubBytesMap.TryGetValue(name.Name, out var bytes))
        {
            Console.WriteLine($"[StartupHook] Providing {name.Name} stub via resolver");
            return Assembly.Load(bytes);
        }
        return null;
    }

    // ========================================================================
    // JMP Hook Infrastructure
    // ========================================================================

    /// <summary>
    /// Force JIT compilation of both methods, then overwrite the original's native code
    /// entry with an absolute JMP to the replacement. Works on JIT-compiled BC methods only.
    /// </summary>
    private static void ApplyJmpHook(MethodBase original, MethodInfo replacement, string name)
    {
        RuntimeHelpers.PrepareMethod(original.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

        IntPtr origFp = original.MethodHandle.GetFunctionPointer();
        IntPtr replFp = replacement.MethodHandle.GetFunctionPointer();

        // Patch the precode entry point
        WriteJmp(origFp, replFp, name);

        // On .NET Core, GetFunctionPointer() returns the precode stub, not the compiled code.
        // The precode is: mov r10, pMethodDesc (10 bytes) + jmp [rip+disp32] (6 bytes)
        // We need to ALSO patch the compiled code that the precode jumps to,
        // because JIT-compiled callers may use direct calls past the precode.
        try
        {
            byte[] precodeBytes = new byte[16];
            Marshal.Copy(origFp, precodeBytes, 0, 16);

            // Check if we just overwrote a FixupPrecode (our JMP is now there)
            // Read from backup: the original precode would have been:
            // 49 BA [8-byte MethodDesc] FF 25 [4-byte disp32]
            // Since we already patched it, read from the MethodDesc to find compiled code
            IntPtr methodDesc = original.MethodHandle.Value;
            // On .NET 8 x64, the compiled code pointer is at MethodDesc + 0x08 (CodeData pointer)
            // This is runtime-internal but works for .NET 8 on Linux x64
            IntPtr codeDataPtr = Marshal.ReadIntPtr(methodDesc, 8);
            if (codeDataPtr != IntPtr.Zero && codeDataPtr != origFp)
            {
                WriteJmp(codeDataPtr, replFp, name + " (code)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook]   (compiled code patch skipped: {ex.Message})");
        }
    }

    private static void WriteJmp(IntPtr target, IntPtr destination, string name)
    {
        // x86-64 absolute indirect jump: FF 25 00 00 00 00 [8-byte address]
        byte[] jmp = new byte[14];
        jmp[0] = 0xFF;
        jmp[1] = 0x25;
        BitConverter.GetBytes(destination.ToInt64()).CopyTo(jmp, 6);

        long pageSize = 4096;
        long addr = target.ToInt64();
        long pageStart = addr & ~(pageSize - 1);
        nuint regionSize = (nuint)((addr - pageStart) + jmp.Length + pageSize);

        int ret = mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC);
        if (ret != 0)
        {
            Console.WriteLine($"[StartupHook] mprotect failed for {name}: errno={Marshal.GetLastWin32Error()}");
            return;
        }

        Marshal.Copy(jmp, 0, target, jmp.Length);
        Console.WriteLine($"[StartupHook] Patched {name} at 0x{target:X} -> 0x{destination:X}");
    }

    // ========================================================================
    // Static field initialization helpers
    // ========================================================================

    /// <summary>
    /// Set a static field value, handling both regular and readonly (initonly) fields.
    /// For readonly fields, uses DynamicMethod IL emit to bypass the initonly restriction.
    /// </summary>
    private static void SetStaticField(Type type, string fieldName, object? value)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var field = type.GetField(fieldName, flags);
        if (field == null)
        {
            Console.WriteLine($"[StartupHook]   Field {fieldName} not found");
            return;
        }

        try
        {
            // Try direct SetValue first (works for non-readonly fields)
            field.SetValue(null, value);
        }
        catch (FieldAccessException)
        {
            // Readonly (initonly) field — use DynamicMethod to bypass
            SetReadonlyStaticField(field, value);
        }
        Console.WriteLine($"[StartupHook]   Set {fieldName} = {value ?? "null"}");
    }

    /// <summary>
    /// Use DynamicMethod IL emission to set a static readonly field.
    /// DynamicMethod with skipVisibility bypasses initonly checks.
    /// </summary>
    private static void SetReadonlyStaticField(FieldInfo field, object? value)
    {
        var dm = new DynamicMethod(
            $"SetStatic_{field.Name}",
            typeof(void),
            new[] { typeof(object) },
            field.DeclaringType!.Module,
            skipVisibility: true);

        var il = dm.GetILGenerator();

        if (value == null)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            if (field.FieldType.IsValueType)
                il.Emit(OpCodes.Unbox_Any, field.FieldType);
        }

        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);

        var setter = (Action<object?>)dm.CreateDelegate(typeof(Action<object?>));
        setter(value);
    }

    /// <summary>
    /// Try to construct a BC type field via parameterless constructor.
    /// If the type can't be constructed, sets the field to null.
    /// </summary>
    private static void TryInitField(Type type, string fieldName)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var field = type.GetField(fieldName, flags);
        if (field == null) return;

        try
        {
            var instance = Activator.CreateInstance(field.FieldType);
            SetStaticField(type, fieldName, instance);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook]   Cannot init {fieldName} ({field.FieldType.Name}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    // Replacement methods
    // ========================================================================

    // --- Patch #1: CustomTranslationResolver replacements ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Assembly? Replacement_OnAppDomainAssemblyResolve(object self, object? sender, ResolveEventArgs args)
    {
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_ResolveSatelliteAssembly(object self, string name)
    {
    }

    // --- Patch #5: Telemetry replacements ---

    /// <summary>
    /// Replaces NavOpenTelemetryLogger constructor. No-op — ETW/Geneva telemetry
    /// is not available on Linux.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NavOpenTelemetryLoggerCtor(object self, int traceLevel, object? contextColumns, string? logFileFolder)
    {
        Console.WriteLine("[StartupHook] NavOpenTelemetryLogger..ctor skipped (no ETW on Linux)");
    }


    // --- Generic no-op replacements ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_ObjectArg(object? arg) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_2Args(object? a, object? b) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NoOp_3Args(object? self, object? a, object? b) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Replacement_ReturnNull() { return null; }

    /// <summary>No-op replacement for void methods with one parameter (e.g., VerifyTestExecutionEnabled).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_ReturnVoid(object? arg) { }

    // --- Patch #2: NavEnvironment replacements ---

    /// <summary>
    /// Replaces NavEnvironment..cctor(). Initializes all static fields except serviceAccount
    /// (which would call WindowsIdentity.GetCurrent() and crash on Linux).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Replacement_NavEnvironmentCctor()
    {
        Console.WriteLine("[StartupHook] Running NavEnvironment..cctor replacement");
        var type = _navEnvironmentType!;

        try
        {
            // Critical fields that must be non-null
            SetStaticField(type, "lockObject", new object());
            SetStaticField(type, "instanceId", Guid.NewGuid());
            SetStaticField(type, "serviceInstanceName", string.Empty);

            // serviceAccount: set to a WindowsIdentity from our stub so the original
            // getters (ServiceAccount => serviceAccount.User, ServiceAccountName => serviceAccount.Name)
            // work even when JMP hooks are bypassed by R2R/tiered compilation.
            SetStaticField(type, "serviceAccount", System.Security.Principal.WindowsIdentity.GetCurrent());

            // Try to construct BC-typed fields (non-critical if they fail)
            TryInitField(type, "compactLohGate");
            TryInitField(type, "TerminatedSessionsMetric");

            // HashSet<ConnectionType> fields — try empty sets
            TryInitField(type, "defaultAwaitedShutdownConnectionTypesList");
            TryInitField(type, "defaultRestartNotificationConnectionTypesList");

            Console.WriteLine("[StartupHook] NavEnvironment..cctor replacement completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StartupHook] NavEnvironment..cctor replacement error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[StartupHook]   {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Replaces: static SecurityIdentifier ServiceAccount => serviceAccount.User
    /// Returns null — no Windows security identity on Linux.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Replacement_GetServiceAccount()
    {
        // Return a SecurityIdentifier for LocalSystem (S-1-5-18)
        return new System.Security.Principal.SecurityIdentifier("S-1-5-18");
    }

    /// <summary>
    /// Replaces: static string ServiceAccountName => serviceAccount.Name
    /// Returns a fake service account name.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Replacement_GetServiceAccountName()
    {
        return "SYSTEM";
    }

    // ========================================================================
    // P/Invoke
    // ========================================================================

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);

    private const int PROT_READ = 1;
    private const int PROT_WRITE = 2;
    private const int PROT_EXEC = 4;
}
