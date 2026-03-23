# Headless BC Service Tier on Linux

## Goal

Run the real BC service tier (v27.5) on Linux with Docker SQL Server for automated
pipeline testing. 100% test fidelity — real events, real filters, real triggers.

## Architecture

```
Docker SQL Server (CRONUS database with schema + license)
    ↑ TCP connection
DynamicsNavServer.Main() (real BC service tier on Linux, .NET 8)
    ↑ DOTNET_STARTUP_HOOKS
StartupHook.dll (JMP patches for Linux compatibility, no external deps)
```

We run the REAL service tier. Microsoft's code handles initialization, metadata loading,
sessions, test execution. We only patch the parts that crash on Linux.

## Service Tier Feature Decisions (Pipeline Scope)

### KEEP
| Feature | Notes |
|---|---|
| SQL connection (NavDatabase) | Docker SQL |
| Metadata loading (NCLMetadata) | Table/field/codeunit definitions from SQL |
| NavEnvironment initialization | Runtime bootstrap |
| NavSession creation | Runtime context for AL execution |
| App publishing (NavAppPublisher) | Load .app packages into runtime |
| Test execution (NavTestCodeunit) | Test discovery, invocation, isolation |
| Event subscription system | Event binding/dispatch |
| NavCodeunit dispatch (ITreeObject) | Codeunit.Run, cross-object calls |
| Licensing (from DB) | CRONUS ships with license data |
| Dev endpoint | For publishing extensions (needs Http.sys → Kestrel) |
| Management API | For running tests — requires auth disabled |

### KILL
| Feature | Patch Strategy |
|---|---|
| OData V4, SOAP, Client Service, MCP, Debugger | No-op endpoints |
| ALL authentication (Windows, AAD, OAuth) | Accept everything |
| Permissions / Entitlements | Bypass — all access allowed |
| Data encryption + transport encryption | Disabled |
| Reporting (RDLC) + side services subprocess | No-op |
| Task Scheduler + Job Queue | No-op |
| Performance counters, ETW, OpenTelemetry | No-op |
| Health monitoring, heartbeat, deadlock analyzer | No-op |
| Change tracking, XRM/Dataverse, full-text search, Azure Blob | No-op |

## Implementation Progress

### Patch #1: CustomTranslationResolver — DONE ✅

**Problem:** Stack overflow from recursive satellite assembly resolution when
WindowsIdentity.GetCurrent() throws PlatformNotSupportedException on Linux.

**Fix:** JMP hook on `OnAppDomainAssemblyResolve` and `ResolveSatelliteAssembly`
in `Microsoft.Dynamics.Nav.Language.dll` (note: NOT Nav.Common.dll despite the
`Microsoft.Dynamics.Nav.Common` namespace).

**Result:** Stack overflow eliminated. Clean TypeInitializationException now.

### Patch #2: NavEnvironment..cctor() — DONE ✅

**Problem:** Static field initializer calls `WindowsIdentity.GetCurrent()`.
**Fix:** JMP hook replaces entire .cctor with one that initializes fields without
WindowsIdentity. Uses DynamicMethod IL emit for readonly fields. Also hooks
ServiceAccount/ServiceAccountName property getters.

### Patch #3: Win32 P/Invoke stubs — DONE ✅

**Problem:** kernel32.dll, user32.dll, advapi32.dll, etc. don't exist on Linux.
**Fix:** Compiled C stub library (`libwin32_stubs.so`) providing no-op implementations
of JobObject, OEM/ANSI encoding, EventLog, performance counters, AD/DHCP functions.
Loaded via `NativeLibrary.ResolvingUnmanagedDll` to intercept ALL assemblies.
Note: JMP hooks don't work for some methods (likely R2R or JIT inlining).

### Patch #4: EventLogWriter — DONE ✅

**Problem:** `System.Diagnostics.EventLog` throws PlatformNotSupportedException.
**Fix:** DispatchProxy replaces the `IEventLogEntryWriter` implementation with a no-op.

### Patch #5: Geneva ETW exporter — DONE ✅

**Problem:** `OpenTelemetry.Exporter.Geneva` requires Windows ETW subsystem.
**Fix:** Replaced the DLL with a stub assembly providing the same public API with
no-op implementations (GenevaStub/ subproject).

### Patch #6: System.Drawing.Common — DONE ✅

**Problem:** BC-bundled System.Drawing 8.0 unconditionally throws on non-Windows.
Used by NavReportFontEnumeration for font logging.
**Fix:** Original DLL renamed to .orig; stub loaded via AssemblyLoadContext.Resolving
to bypass strong-name identity check (DrawingStub/ subproject).

### **MILESTONE: Service tier boots on Linux! ASP.NET Core host starts.**

### Next steps:
- Config file (minimal XML pointing to Docker SQL)
- SQL connection (Docker SQL must be running with BC schema)
- Http.sys → Kestrel (already using Kestrel via ASP.NET Core)
- Authentication bypass
- Test execution pipeline

## Technical Details

### JMP Hook Mechanism
Works ONLY on BC methods (JIT-compiled). BCL methods are ReadyToRun pre-compiled
and cannot be patched this way.

```csharp
RuntimeHelpers.PrepareMethod(method.MethodHandle);     // Force JIT
IntPtr fp = method.MethodHandle.GetFunctionPointer();   // Native code address
// Write x86-64 absolute JMP: FF 25 00 00 00 00 [8-byte addr]
mprotect(page, size, PROT_READ | PROT_WRITE | PROT_EXEC);
Marshal.Copy(jmpBytes, 0, fp, 14);
```

### Build & Test
```bash
cd StartupHook && dotnet publish -c Release -o bin/Release/net8.0/publish

SERVICE_DIR="artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service"
HOOK="StartupHook/bin/Release/net8.0/publish/StartupHook.dll"
cd "$SERVICE_DIR" && DOTNET_STARTUP_HOOKS="$HOOK" dotnet Microsoft.Dynamics.Nav.Server.dll 2>&1 | head -80
```

### Key Files
- `StartupHook/StartupHook.cs` — All patches
- `StartupHook/StartupHook.csproj` — net8.0, AllowUnsafeBlocks, no external deps
- `docs/headless-decision-history.md` — Why this approach was chosen over alternatives
- `docs/runtime-analysis.md` — BC runtime DLL analysis
- Service tier: `artifacts/onprem/27.5.46862.0/platform/ServiceTier/.../Service/`

### Constraints
- No Harmony/MonoMod (native helper rejected by kernel security)
- Raw JMP hooks only — works on BC methods, NOT on BCL methods
- Decompile with `ilspycmd` when investigating blockers
- .NET 8 + ASP.NET Core 8.0 required

## Success Criteria

Service tier starts on Linux, connects to Docker SQL, loads metadata, publishes
.app packages, and runs AL tests with results identical to production BC.
