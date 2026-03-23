# Headless BC Service Tier on Linux

## Goal

Run the real BC service tier (v27.5) on Linux with Docker SQL Server for automated
pipeline testing. 100% test fidelity — real events, real filters, real triggers.

## Architecture

```
Docker SQL Server 2022 (CRONUS database with schema + license)
    ↑ TCP connection
DynamicsNavServer.Main() (real BC service tier on Linux, .NET 8)
    ↑ DOTNET_STARTUP_HOOKS
StartupHook.dll (JMP patches for Linux compatibility)
    + 6 stub DLL subprojects (GenevaStub, DrawingStub, PerfCounterStub,
      WindowsPrincipalStub, HttpSysStub, AclStub)
    + libwin32_stubs.so (C shared library for P/Invoke)
    + Framework-level DLL replacements in /usr/share/dotnet/shared/
```

We run the REAL service tier. Microsoft's code handles initialization, metadata loading,
sessions, test execution. We only patch the parts that crash on Linux.

## Current Status (as of 2026-03-23)

**The BC v27.5 service tier runs on Linux in Docker.** Key milestones reached:
- 12 startup patches applied via DOTNET_STARTUP_HOOKS
- SQL connected to CRONUS database (Docker SQL Server 2022)
- All BC extensions compiled (Base Application, System Application, ~30 extensions)
- 7 Kestrel API hosts created (ports 18001-18007)
- **ONE remaining blocker**: SPN registration NullRef (see Patch #12 below)

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

**Problem:** kernel32.dll, user32.dll, advapi32.dll, rpcrt4.dll, httpapi.dll, etc. don't exist on Linux.
**Fix:** Compiled C stub library (`libwin32_stubs.so`) providing no-op implementations
of JobObject, OEM/ANSI encoding, EventLog, performance counters, AD/DHCP functions.
Loaded via `NativeLibrary.ResolvingUnmanagedDll` to intercept ALL assemblies.

### Patch #4: EventLogWriter — DONE ✅

**Problem:** `System.Diagnostics.EventLog` throws PlatformNotSupportedException.
**Fix:** DispatchProxy replaces the `IEventLogEntryWriter` implementation with a no-op.

### Patch #5: Geneva ETW exporter — DONE ✅

**Problem:** `OpenTelemetry.Exporter.Geneva` requires Windows ETW subsystem.
**Fix:** Replaced the DLL with a stub assembly providing the same public API with
no-op implementations (StartupHook/GenevaStub/ subproject).

### Patch #6: System.Drawing.Common — DONE ✅

**Problem:** BC-bundled System.Drawing 8.0 unconditionally throws on non-Windows.
Used by NavReportFontEnumeration for font logging.
**Fix:** Original DLL renamed to .orig; stub loaded via AssemblyLoadContext.Resolving
to bypass strong-name identity check (StartupHook/DrawingStub/ subproject).

### **MILESTONE: Service tier boots on Linux! ASP.NET Core host starts.**

### Patch #7: Encryption provider — DONE ✅

**Problem:** BC encrypts database passwords using DPAPI (Windows-only). The service
tier needs to decrypt `ProtectedDatabasePassword` from config.
**Fix:** Three-part solution:
1. `PassthroughEncryptionProxy` that returns input unchanged (plain text passwords)
2. Matching RSA public key inserted into DB so `ServerInstanceRsaEncryptionProvider`
   validation passes
3. `ServerInstanceRsaEncryptionProvider.Instance` field replaced at runtime

### Patch #8: PerformanceCounter — DONE ✅

**Problem:** `System.Diagnostics.PerformanceCounter` is Windows-only.
**Fix:** Stub DLL replacement (StartupHook/PerfCounterStub/ subproject).

### Patch #9: Topology proxy — DONE ✅

**Problem:** BC calls Windows ACL APIs to check HTTP URL reservations.
**Fix:** `LinuxTopologyProxy` with `IsServiceRunningInLocalEnvironment=false`
to skip ACL checks entirely.

### Patch #10: WindowsPrincipal — DONE ✅

**Problem:** `System.Security.Principal.Windows` types (WindowsIdentity,
SecurityIdentifier, etc.) used throughout BC for authentication.
**Fix:** Framework-level stub replacing `System.Security.Principal.Windows.dll`
in the shared framework directory (`/usr/share/dotnet/shared/...`) with dummy
implementations of WindowsIdentity, SecurityIdentifier, WindowsPrincipal, etc.
(StartupHook/WindowsPrincipalStub/ subproject).

### Patch #11: HttpSys → Kestrel — DONE ✅

**Problem:** `Microsoft.AspNetCore.Server.HttpSys` is Windows-only. BC uses
`UseHttpSys()` for all its API endpoints.
**Fix:** Stub replacing `Microsoft.AspNetCore.Server.HttpSys.dll` where
`UseHttpSys()` redirects to `UseKestrel()` (StartupHook/HttpSysStub/ subproject).
Framework DLL replaced in `/usr/share/dotnet/shared/Microsoft.AspNetCore.App/`.

### Patch #12: Microsoft.Data.SqlClient — DONE ✅

**Problem:** BC-bundled SqlClient has Windows registry dependencies that cause
NullRef on Linux.
**Fix:** Replaced with cross-platform Unix build from NuGet.

### **MILESTONE: SQL connected, all extensions compiled, 7 Kestrel hosts up.**

### Remaining Blocker: SPN Registration NullRef

**Problem:** `ServicePrincipalHelper.SpnRegister` calls `NavEnvironment.get_ServiceAccount()`
which returns `SecurityIdentifier("S-1-5-18")` from our WindowsPrincipal stub. SpnRegister
then uses this for Active Directory SPN registration — which is Windows-only and crashes.

**Fix options:**
1. Make SpnRegister a no-op via nclcsrts.dll stub (already have `NCL_SpnRegister` returning 0)
2. Make the SecurityIdentifier stub more complete so the code path succeeds harmlessly

## Technical Findings

### JMP Hook Mechanism
```csharp
RuntimeHelpers.PrepareMethod(method.MethodHandle);     // Force JIT
IntPtr fp = method.MethodHandle.GetFunctionPointer();   // Native code address
// Write x86-64 absolute JMP: FF 25 00 00 00 00 [8-byte addr]
mprotect(page, size, PROT_READ | PROT_WRITE | PROT_EXEC);
Marshal.Copy(jmpBytes, 0, fp, 14);
```

### JMP Hook Limitations

**Works for:**
- Static constructors (.cctor)
- Static property getters in Nav.Ncl.dll
- Methods in Nav.Language.dll

**Does NOT work for:**
- Regular static/instance methods (verified: code IS written at correct address,
  but JIT uses different dispatch path — likely R2R or tiered compilation)

**Workarounds for non-hookable methods:**
- DLL replacement (stub assemblies)
- DispatchProxy (interface-based interception)
- AssemblyLoadContext.Resolving (load-time assembly substitution)
- Framework-level DLL override in Docker (`/usr/share/dotnet/shared/...`)

### Configuration (CustomSettings.config)

Key settings changed for Linux/Docker:
```xml
DatabaseServer=sql              <!-- Docker network hostname -->
DatabaseName=CRONUS
DatabaseUserName=bctest
ProtectedDatabasePassword=Test1234   <!-- plain text, encryption bypassed -->
ClientServicesCredentialType=NavUserPassword
DeveloperServicesEnabled=true
ReportingServiceIsSideService=false
TrustSQLServerCertificate=true
<!-- All ports changed to 17xxx range -->
ManagementServicesPort=17045
ClientServicesPort=17046
SoapServicesPort=17047
ODataServicesPort=17048
DeveloperServicesPort=17049
SnapshotDebuggerServicesPort=17085
```

Additional in `runtimeconfig.json`:
```json
"System.Drawing.EnableUnixSupport": true
```
(Note: this flag does not actually work for .NET 8 — hence the DrawingStub)

## Infrastructure

### Docker Setup
- `StartupHook/Dockerfile` — BC service tier container
- `docker-compose.yml` — Orchestrates BC + SQL Server 2022
- `scripts/setup-sql.sh` — CRONUS database restore

### Stub Subprojects (all under StartupHook/)
| Project | Replaces | Method |
|---|---|---|
| GenevaStub/ | OpenTelemetry.Exporter.Geneva | DLL replacement in service dir |
| DrawingStub/ | System.Drawing.Common | AssemblyLoadContext.Resolving |
| PerfCounterStub/ | System.Diagnostics.PerformanceCounter | DLL replacement |
| WindowsPrincipalStub/ | System.Security.Principal.Windows | Framework dir replacement |
| HttpSysStub/ | Microsoft.AspNetCore.Server.HttpSys | Framework dir replacement |
| AclStub/ | (unused — ACL bypass done via topology proxy) | — |

### Build & Run
```bash
# Build and start everything
docker compose up --build

# Or manually:
cd StartupHook && dotnet publish -c Release -o bin/Release/net8.0/publish
SERVICE_DIR="artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service"
HOOK="StartupHook/bin/Release/net8.0/publish/StartupHook.dll"
cd "$SERVICE_DIR" && DOTNET_STARTUP_HOOKS="$HOOK" dotnet Microsoft.Dynamics.Nav.Server.dll
```

### Key Files
- `StartupHook/StartupHook.cs` — All patches
- `StartupHook/StartupHook.csproj` — net8.0, AllowUnsafeBlocks
- `StartupHook/Dockerfile` — Docker container definition
- `StartupHook/kernel32_stubs.c` — C source for libwin32_stubs.so
- `docker-compose.yml` — Docker orchestration
- `scripts/setup-sql.sh` — SQL Server setup
- `docs/headless-decision-history.md` — Why this approach was chosen
- `docs/runtime-analysis.md` — BC runtime DLL analysis

### Constraints
- No Harmony/MonoMod (native helper rejected by kernel security)
- Raw JMP hooks only — works on BC methods, NOT on BCL methods
- Decompile with `ilspycmd` when investigating blockers
- .NET 8 + ASP.NET Core 8.0 required
- Framework DLLs must be replaced in `/usr/share/dotnet/shared/` (not service dir)

## Future Plans

### Self-Contained Docker Image
- Specify BC artifact URL at container startup
- Container pulls artifacts, prepares everything, starts BC automatically
- Use default BC ports internally (7045-7049, 7085, 7086), docker-compose maps to host ports
- Support sandbox artifacts (primary target), not just onprem

### Full Pipeline Vision
```
docker run -e ARTIFACT_URL=... bc-test-runner
  → Pull BC artifacts
  → Restore CRONUS database
  → Start BC service tier
  → Publish test .app
  → Execute tests
  → Report results (JSON/JUnit)
```

### Version Support
- v27.5 (current — nearly working)
- v28 (next target after v27.5 blocker resolved)

## Success Criteria

Service tier starts on Linux, connects to Docker SQL, loads metadata, publishes
.app packages, and runs AL tests with results identical to production BC.
