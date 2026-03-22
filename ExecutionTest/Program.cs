using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Dynamics.Nav.BusinessApplication;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

Console.WriteLine("=== Execution Test: Generated AL-to-C# Codeunit (Linux) ===\n");

// ---------------------------------------------------------------------------
// APPROACH: DllImportResolver + Direct Reflection
//
// Two static initialisers crash on Linux:
//   1. WindowsLanguageHelper (Nav.Types) → P/Invokes kernel32.dll!LCIDToLocaleName
//   2. NavEnvironment (Nav.Ncl) → calls WindowsIdentity.GetCurrent()
//
// We use NativeLibrary.SetDllImportResolver to intercept the kernel32 P/Invoke
// and then bypass the full BC runtime by invoking OnRun() on scope objects directly.
// ---------------------------------------------------------------------------

Console.WriteLine("--- Phase 0a: Register DllImportResolver for kernel32.dll ---");

// Register the resolver on ALL BC assemblies that might P/Invoke kernel32
var registeredAssemblies = new HashSet<string>();
var bcAssemblies = new[]
{
    typeof(Decimal18).Assembly,        // Nav.Types (may be separate or merged)
    typeof(NavCodeunit).Assembly,      // Nav.Ncl / Nav.Runtime
};

// Also try to find the Types assembly explicitly
var typesAsmPath = Path.Combine(
    "../artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service",
    "Microsoft.Dynamics.Nav.Types.dll");
var fullTypesPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, typesAsmPath));

// Check loaded assemblies for Types
foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
{
    if (asm.GetName().Name?.Contains("Nav.Types") == true ||
        asm.GetName().Name?.Contains("Nav.Ncl") == true ||
        asm.GetName().Name?.Contains("Nav.Runtime") == true ||
        asm.GetName().Name?.Contains("Nav.Core") == true ||
        asm.GetName().Name?.Contains("Nav.Common") == true)
    {
        bcAssemblies = bcAssemblies.Append(asm).ToArray();
    }
}

foreach (var asm in bcAssemblies)
{
    var name = asm.GetName().Name ?? "?";
    if (registeredAssemblies.Contains(name)) continue;
    try
    {
        NativeLibrary.SetDllImportResolver(asm, Kernel32Shim.Resolver);
        Console.WriteLine($"[OK] DllImportResolver registered for {name}");
        registeredAssemblies.Add(name);
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine($"[INFO] Resolver already set for {name}");
        registeredAssemblies.Add(name);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Could not register resolver for {name}: {ex.Message}");
    }
}

Console.WriteLine("\n--- Phase 0b: Pre-trigger static constructors ---");

var windowsLangHelper = typeof(Decimal18).Assembly.GetType("Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper");
if (windowsLangHelper != null)
{
    try
    {
        RuntimeHelpers.RunClassConstructor(windowsLangHelper.TypeHandle);
        Console.WriteLine("[OK] WindowsLanguageHelper static ctor ran successfully");
    }
    catch (TypeInitializationException ex)
    {
        Console.WriteLine($"[WARN] WindowsLanguageHelper cctor failed: {ex.InnerException?.Message ?? ex.Message}");
    }
}

var navEnvType = typeof(NavCodeunit).Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavEnvironment");
if (navEnvType != null)
{
    try
    {
        RuntimeHelpers.RunClassConstructor(navEnvType.TypeHandle);
        Console.WriteLine("[OK] NavEnvironment static ctor ran successfully");
    }
    catch (TypeInitializationException ex)
    {
        Console.WriteLine($"[EXPECTED] NavEnvironment cctor failed: {ex.InnerException?.Message ?? ex.Message}");
        Console.WriteLine("  (Expected on Linux - WindowsIdentity.GetCurrent() is Windows-only)");
    }
}

// ---------------------------------------------------------------------------
// Phase 1: Try normal construction (likely fails due to session dependency)
// ---------------------------------------------------------------------------
Console.WriteLine("\n--- Phase 1: Try constructing Codeunit50100 (normal path) ---");
Codeunit50100? codeunit = null;
try
{
    var root = new FakeRootObject();
    codeunit = new Codeunit50100(root);
    Console.WriteLine("[OK] Constructed Codeunit50100");
}
catch (Exception ex)
{
    var inner = ex;
    while (inner.InnerException != null) inner = inner.InnerException;
    Console.WriteLine($"[FAIL] Normal construction: {inner.GetType().Name}: {inner.Message}");
    Console.WriteLine("  (Expected - requires NavSession infrastructure)");
}

// ---------------------------------------------------------------------------
// Phase 2: Direct scope invocation for ApplyDiscount(100, 10)
// ---------------------------------------------------------------------------
Console.WriteLine("\n--- Phase 2: Direct scope invocation for ApplyDiscount(100, 10) ---");
try
{
    // ByRef<T> needs getter/setter delegates - the default ctor leaves them null
    Decimal18 priceStorage = new Decimal18(100);
    var price = new ByRef<Decimal18>(
        () => priceStorage,
        v => priceStorage = v);
    var pct = new Decimal18(10);
    Console.WriteLine($"  Before: price={price.Value}, pct={pct}");

    ScopeInvoker.InvokeOnRun(
        typeof(Codeunit50100),
        "ApplyDiscount_Scope__743102751",
        new Dictionary<string, object>
        {
            ["unitPrice"] = price,
            ["pct"] = pct
        });

    Console.WriteLine($"  After:  price={price.Value}");
    Console.WriteLine($"[OK] ApplyDiscount => {price.Value}");
}
catch (Exception ex)
{
    var inner = ex.InnerException ?? ex;
    Console.WriteLine($"[FAIL] ApplyDiscount: {inner.GetType().Name}: {inner.Message}");
    Console.WriteLine($"  Stack: {inner.StackTrace}");
}

// ---------------------------------------------------------------------------
// Phase 3: Direct scope invocation for Greet("World")
// ---------------------------------------------------------------------------
Console.WriteLine("\n--- Phase 3: Direct scope invocation for Greet(\"World\") ---");
try
{
    var result = ScopeInvoker.InvokeOnRun(
        typeof(Codeunit50100),
        "Greet_Scope_376357172",
        new Dictionary<string, object>
        {
            ["name"] = new NavText("World").ModifyLength(0)
        },
        returnFieldName: "\u03b3retVal",
        returnFieldInit: NavText.Default(0));

    Console.WriteLine($"  Result: {result}");
    Console.WriteLine($"[OK] Greet => {result}");
}
catch (Exception ex)
{
    var inner = ex.InnerException ?? ex;
    Console.WriteLine($"[FAIL] Greet: {inner.GetType().Name}: {inner.Message}");
    Console.WriteLine($"  Stack: {inner.StackTrace}");
}

Console.WriteLine("\n=== Execution Test Complete ===");

// ===========================================================================
// Kernel32 native shim
// ===========================================================================
public static class Kernel32Shim
{
    private static IntPtr _handle = IntPtr.Zero;

    public static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, "kernel32.dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(libraryName, "kernel32", StringComparison.OrdinalIgnoreCase))
        {
            return GetOrCreate();
        }
        return IntPtr.Zero;
    }

    private static IntPtr GetOrCreate()
    {
        if (_handle != IntPtr.Zero) return _handle;

        var shimDir = Path.Combine(Path.GetTempPath(), "bc-kernel32-shim");
        Directory.CreateDirectory(shimDir);

        var cFile = Path.Combine(shimDir, "kernel32_shim.c");
        var soFile = Path.Combine(shimDir, "libkernel32.so");

        if (!File.Exists(soFile))
        {
            // P/Invoke: [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            // .NET marshals CharSet.Unicode as UTF-16LE on all platforms.
            File.WriteAllText(cFile, SHIM_SOURCE);
            Console.WriteLine($"  Compiling kernel32 shim...");

            var psi = new System.Diagnostics.ProcessStartInfo("gcc",
                $"-shared -fPIC -o \"{soFile}\" \"{cFile}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10000);
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException($"gcc failed: {err}");
            }
            Console.WriteLine("  [OK] Compiled kernel32 shim");
        }

        _handle = NativeLibrary.Load(soFile);
        Console.WriteLine($"  [OK] Loaded kernel32 shim (0x{_handle:X})");
        return _handle;
    }

    private const string SHIM_SOURCE = @"
#include <stdint.h>
#include <string.h>

typedef uint16_t WCHAR;

static void u16copy(WCHAR* dst, const char* src, int max) {
    int i;
    for (i = 0; src[i] && i < max - 1; i++) dst[i] = (WCHAR)src[i];
    if (i < max) dst[i] = 0;
}

int LCIDToLocaleName(uint32_t lcid, WCHAR* buf, int bufSize, uint32_t flags) {
    const char* name = 0;
    switch (lcid) {
        case 1033: name = ""en-US""; break;
        case 1031: name = ""de-DE""; break;
        case 1036: name = ""fr-FR""; break;
        case 1034: name = ""es-ES""; break;
        case 1040: name = ""it-IT""; break;
        case 1043: name = ""nl-NL""; break;
        case 1044: name = ""nb-NO""; break;
        case 1045: name = ""pl-PL""; break;
        case 1046: name = ""pt-BR""; break;
        case 1049: name = ""ru-RU""; break;
        case 1053: name = ""sv-SE""; break;
        case 2052: name = ""zh-CN""; break;
        case 2057: name = ""en-GB""; break;
        case 1028: name = ""zh-TW""; break;
        case 1029: name = ""cs-CZ""; break;
        case 1030: name = ""da-DK""; break;
        case 1032: name = ""el-GR""; break;
        case 1035: name = ""fi-FI""; break;
        case 1037: name = ""he-IL""; break;
        case 1038: name = ""hu-HU""; break;
        case 1041: name = ""ja-JP""; break;
        case 1042: name = ""ko-KR""; break;
        case 1048: name = ""ro-RO""; break;
        case 1055: name = ""tr-TR""; break;
        case 1058: name = ""uk-UA""; break;
        case 1060: name = ""sl-SI""; break;
        case 1061: name = ""et-EE""; break;
        case 1062: name = ""lv-LV""; break;
        case 1063: name = ""lt-LT""; break;
        case 0: case 127: name = """"; break;
        default: return 0;
    }
    int len = strlen(name);
    if (!buf || bufSize == 0) return len + 1;
    u16copy(buf, name, bufSize);
    return len + 1;
}

uint32_t GetLastError(void) { return 0; }
void SetLastError(uint32_t e) { }
";
}

// ===========================================================================
// Direct scope invoker - bypasses BC runtime entirely
// ===========================================================================
public static class ScopeInvoker
{
    public static object? InvokeOnRun(
        Type codeunitType,
        string scopeTypeName,
        Dictionary<string, object> fields,
        string? returnFieldName = null,
        object? returnFieldInit = null)
    {
        var scopeType = codeunitType.GetNestedType(scopeTypeName,
            BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Scope type '{scopeTypeName}' not found");

        // Create uninitialised - no constructor chain fires
        var scope = RuntimeHelpers.GetUninitializedObject(scopeType);

        // Set fields
        foreach (var (name, value) in fields)
        {
            var field = scopeType.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Field '{name}' not found on {scopeType.Name}");
            field.SetValue(scope, value);
        }

        // Initialise return value field if specified
        if (returnFieldName != null && returnFieldInit != null)
        {
            var retField = scopeType.GetField(returnFieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            retField?.SetValue(scope, returnFieldInit);
        }

        // Call OnRun()
        var onRun = scopeType.GetMethod("OnRun",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"OnRun() not found on {scopeType.Name}");

        onRun.Invoke(scope, null);

        // Read return value
        if (returnFieldName != null)
        {
            var retField = scopeType.GetField(returnFieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return retField?.GetValue(scope);
        }
        return null;
    }
}

// ===========================================================================
// Minimal ITreeObject
// ===========================================================================
public class FakeRootObject : ITreeObject
{
    private readonly TreeHandler _tree;
    public TreeHandler Tree => _tree;

    public FakeRootObject()
    {
        _tree = TreeHandler.CreateTreeRoot(this);
    }
}
