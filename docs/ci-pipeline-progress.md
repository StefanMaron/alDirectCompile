# CI Pipeline Progress — BC Headless on GitHub Actions

## Session: 2026-03-23

### What Was Achieved

**1. BC Service Tier Runs on GitHub Actions (GREEN PIPELINE)**

The headless BC service tier now starts, compiles extensions, publishes apps, and
executes AL tests on ubuntu-latest GitHub Actions runners. Full end-to-end proof:

```
5 tests passed on CI:
  ✓ Sample Data Tests PPC::TestGenerateSampleData
  ✓ Sample Data Tests PPC::TestClearAllData
  ✓ Sample Data Tests PPC::TestProcessLargeDataSet
  ✓ Sample Data Tests PPC::TestSampleDataValidation
  ✓ Sample Data Tests PPC::TestSampleLineCalculation
```

Working workflow: `PipelinePerformanceComparison/.github/workflows/linux-full-pipeline.yml`

**2. Root Causes Found and Fixed**

| Bug | Root Cause | Fix |
|-----|-----------|-----|
| FileNotFoundException on CI | `find \| head -1` picked net9.0 SqlClient DLL instead of net8.0 | Explicit net8.0 verification in CI workflow |
| No entrypoint logs visible | bash stdout is pipe-buffered when PID 1 has no TTY | `exec 1>&2` at top of entrypoint.sh |
| AL compiler picks wrong files | `aldirectcompile/` clone in project dir contains .al files | `rm -rf aldirectcompile` before `AL compile` |
| Test runner API 404 | Custom API pages served on port 7052, not OData port 7048 | Fixed URL to port 7052 |
| Extension publish ID conflict | TestRunner.app and sample ext both use IDs 50002-50004 | Don't publish separate TestRunner |
| Watson crash handler | `WatsonReporting.GetRegistryValue` → NullRef (no Windows registry) | Patch #13: JMP hook SendReport to no-op |

**3. BCApps System Application Compiles on Linux**

```
Component                   | Files | Time
System Application          | 1264  | ~6s
System Application Test Lib | 169   | ~5s
System Application Test     | 224   | ~10s
TOTAL                       | 1657  | ~21s
```

BCApps tag `releases/27.5/StrictMode` matches our v27.5 platform artifacts exactly.

---

## Session: 2026-03-24

### What Was Achieved

**4. Watson Crash Prevention (FULLY FIXED)**

Watson reporting (`WatsonReporting.GetRegistryValue`) caused process-terminating
NullRef crashes on the GC finalizer thread. Root cause chain:

```
UnobservedTaskException → NavEnvironment handler → WriteWatsonLog
→ SendReport → GetWatsonPath → GetRegistryValue → NullRef (no Windows registry)
→ finalizer thread crash → process exit
```

Multi-layer fix:
- `TaskScheduler.UnobservedTaskException` handler with `SetObserved()` (first defense)
- **Timer-based cleanup** every 1s that removes BC's NavEnvironment Watson handler via
  reflection on `typeof(TaskScheduler).GetField("UnobservedTaskException")` (aggressive cleanup)
- **Tiered compilation disabled** (`DOTNET_TieredCompilation=0`) to prevent JMP hooks
  from being overwritten by Tier 1 recompilation
- JMP hooks on `SendReport`, `GetWatsonPath`, `GetRegistryValue` (defense-in-depth)

**5. Patch #14: Binary-Patched CodeAnalysis.dll (IsTypeForwardingCircular)**

**Root cause discovered:** BC's server-side AL compiler uses Mono.Cecil to load .NET
assemblies for DotNet type validation. On .NET 8, `netstandard.dll` is a facade with
type-forwarding (Dictionary → System.Runtime → System.Private.CoreLib). Cecil's
`CecilDotNetTypeLoader.IsTypeForwardingCircular` crashes with NullRef when following
these chains.

**Fix:** Binary IL patch — replace `IsTypeForwardingCircular` method body with
`ldc.i4.0; ret` (return false = not circular). This allows the forwarding chain to
be followed. Method body at file offset `0x1DDC28` in `Microsoft.Dynamics.Nav.CodeAnalysis.dll`.

**Why not JMP hook:** The assembly is loaded into a different AssemblyLoadContext by
BC's runtime. JMP hooks applied to the eagerly-loaded instance don't affect the
runtime-loaded instance. Binary IL patching modifies the DLL file directly.

**6. Patched Mono.Cecil.dll (CheckFileName)**

**Problem:** When the type-forwarding chain is followed, Cecil tries to load assemblies
from Add-Ins. Some paths in the assembly candidate list are empty strings. Cecil's
`Mixin.CheckFileName` throws `ArgumentNullOrEmptyException` for empty paths, which is
NOT caught by `GetAssemblyNameFromPath`'s catch blocks (it only catches `IOException`,
`BadImageFormatException`, etc.).

**Fix:** Cecil-rewrite `CheckFileName` to throw `BadImageFormatException` for
null/empty paths instead of `ArgumentNullOrEmptyException`. `BadImageFormatException`
IS caught by the existing handler, so empty paths are gracefully skipped.

**7. .NET 8 Reference Assemblies in Add-Ins**

BC's server-side compiler probes Add-Ins for .NET assemblies needed by the type-
forwarding chain. On Windows, the types are found in the .NET Framework. On Linux,
we need them in Add-Ins.

**Key discovery:** .NET 8 runtime DLLs are mostly **ReadyToRun (R2R) native** DLLs
(89 out of 168). Cecil crashes when trying to read their metadata. Only 79 are
managed and readable by Cecil.

**Fix:** Copy .NET 8 **reference assemblies** (163 files, 4MB total) from the SDK
pack (`Microsoft.NETCore.App.Ref/8.0.*/ref/net8.0/`) to Add-Ins. Reference assemblies
are always managed, have full type signatures, and are small.

**8. Pre-Installed Apps Cleared from Database**

The dev endpoint refuses to publish System Application because "it would replace the
existing AppSource app which is a dependency of other apps". Microsoft's own BCApps
pipeline handles this by uninstalling all apps first.

**Fix:** In `entrypoint.sh`, clear all installed/published app tables via SQL before
BC starts:
```sql
DELETE FROM [NAV App Installed App];
DELETE FROM [NAV App Dependencies];
DELETE FROM [Published Application];
-- etc.
```

This gives BC a clean slate with no dependency conflicts.

**9. Add-Ins Case-Sensitivity Fix**

BC expects the directory name `Add-Ins` (capital I) but the artifacts create it as
`Add-ins` (lowercase i). On Linux (case-sensitive filesystem), BC's probing fails
silently.

**Fix:** Rename in entrypoint: `mv Add-ins Add-Ins`

**10. MockTest.dll Located in BC Artifacts**

The Test Library references `assembly("MockTest")` for `MockAzureKeyVaultSecretProvider`.
This DLL is NOT in the BCApps repo — it's shipped with BC at:
```
artifacts/onprem/.../platform/Test Assemblies/Mock Assemblies/MockTest.dll
```

**11. BCApps Pipeline Analysis (Microsoft's Approach)**

Decompiled the entire publish chain and analyzed the BCApps CI/CD workflows:

| Finding | Detail |
|---------|--------|
| MS uses Publish-NAVApp, NOT dev endpoint | Management cmdlet path doesn't recompile |
| Dev endpoint ALWAYS recompiles | `PublishAndInstallExtension → NavAppPackageCompiler.Recompile()` |
| Management API uses WCF/WebSocket | Port 7045, NetHttpBinding, requires Windows auth |
| `IsRuntimePackage` flag skips compilation | But only for packages with no source code |
| MS uninstalls ALL apps first | Then publishes from scratch with `-skipVerification -scope Global` |

### Current State: Server-Side Compilation Runs Without Internal Errors

The combination of all fixes eliminates all crashes and internal errors:
- ✅ No more `IsTypeForwardingCircular` NullRef
- ✅ No more `CheckFileName` empty path exception
- ✅ No more `InvalidProgramException` from bad IL
- ✅ No more Watson crash on finalizer thread
- ✅ No more dependency conflict rejections

The server-side compiler now runs and produces **normal AL compilation errors**:
`AL0452: The type 'X' could not be found in assembly 'netstandard'`

### The ONE Remaining Blocker

**236 .NET types cannot be resolved through the type-forwarding chain.**

The forwarding chain: `netstandard → System.Collections → defines List<T>`. Our
reference assemblies in Add-Ins have the types. But the .NET runtime directory
(`/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.25/`) ALSO contains assemblies
with the same names — these are native/R2R and unreadable by Cecil.

**The problem:** When Cecil follows a type-forward from `netstandard` to
`System.Collections`, it finds TWO candidates:
1. Our reference assembly in Add-Ins (managed, readable, has the type) ✅
2. The runtime's R2R version in the .NET path (native, unreadable) ❌

Cecil (or BC's assembly resolver) picks the wrong one. The .NET runtime path is
in the probing list and may take priority over Add-Ins for certain resolution paths.

**Solution (not yet implemented):**
- **Option A**: Remove/rename the native .NET DLLs in the runtime directory so
  Cecil only finds the reference assemblies in Add-Ins
- **Option B**: Patch BC's `AssemblyLocatorBase.GetPathToCompatibleAssemblyFromCandidates`
  to prefer Add-Ins over the runtime path
- **Option C**: Replace the runtime netstandard.dll with a version that forwards
  to assembly names that ONLY exist in Add-Ins (custom forwarding targets)

---

## Architecture (Current)

```
GitHub Actions Runner (ubuntu-latest, 7GB RAM)
├── Pre-downloads BC artifacts (curl + unzip on host)
├── Builds Docker image (alDirectCompile/StartupHook/Dockerfile)
│   └── Contains: StartupHook.dll + 6 stubs + libwin32_stubs.so + scripts
│   └──           + patched CodeAnalysis.dll + patched Mono.Cecil.dll
│   └──           + 163 .NET 8 reference assemblies
├── docker compose up -d
│   ├── sql (SQL Server 2022, healthcheck)
│   └── bc (entrypoint.sh)
│       ├── Copies service tier to /bc/service/
│       ├── Applies binary-patched DLLs
│       ├── Renames Add-ins → Add-Ins
│       ├── Copies .NET reference assemblies to Add-Ins
│       ├── Clears pre-installed apps from SQL
│       ├── Restores CRONUS, creates user, imports license
│       └── Starts: DOTNET_STARTUP_HOOKS=... dotnet Microsoft.Dynamics.Nav.Server.dll
├── Workflow: AL compile → produces .app
├── Workflow: curl POST .app to dev endpoint (publish)
└── Workflow: curl to port 7052 (custom API) → run tests → read logEntries
```

## All Patches (14 total)

| # | Target DLL | Method | What it does |
|---|-----------|--------|-------------|
| 1 | Nav.Language | `OnAppDomainAssemblyResolve`, `ResolveSatelliteAssembly` | No-op (prevent satellite assembly stack overflow) |
| 2 | Nav.Ncl | `NavEnvironment..cctor` | Replace static ctor (avoids WindowsIdentity) |
| 3 | kernel32.dll | All P/Invoke | `libwin32_stubs.so` C shared library |
| 4 | Nav.Types | `NavEventLogEntryWriter.WriteEntry` | No-op (EventLog unsupported) |
| 5 | Nav.Ncl + Nav.Types | `NavOpenTelemetryLogger`, Geneva | No-op + stub DLL |
| 6 | System.Drawing | Font enumeration | Stub DLL + `EnableUnixSupport` |
| 7 | Nav.Types | Encryption provider | Passthrough (no DPAPI) |
| 8 | PerformanceCounter | - | Stub DLL |
| 9 | Nav.Ncl | Topology | `IsServiceRunningInLocalEnvironment=false` |
| 10 | Principal.Windows | WindowsIdentity, SecurityIdentifier | Framework-level stub |
| 11 | HttpSys | `UseHttpSys()` | Redirect to Kestrel |
| 12 | SqlClient | - | Replace with Unix build |
| 13 | Nav.Watson | `SendReport`, `GetRegistryValue`, `GetWatsonPath` | JMP hooks to no-op + TaskScheduler handler cleanup |
| 14 | CodeAnalysis | `IsTypeForwardingCircular` | **Binary IL patch** → return false |
| 14b | Mono.Cecil | `Mixin.CheckFileName` | **Cecil rewrite** → throw BadImageFormatException for empty paths |

## Binary Patch Details

### CodeAnalysis.dll (`IsTypeForwardingCircular`)
- **File:** `Microsoft.Dynamics.Nav.CodeAnalysis.dll` (10.7MB)
- **Method RVA:** 0x001DFA28
- **File offset:** 0x001DDC28
- **Original header:** Fat (0x3013), MaxStack=3, CodeSize=115, 4 locals, 1 exception handler
- **Patched header:** Tiny (0x0A), CodeSize=2
- **Patched IL:** `ldc.i4.0 (0x16); ret (0x2A)` — return false
- **Zero-out:** 177 bytes (fat header + IL + exception handler section)

### Mono.Cecil.dll (`CheckFileName`)
- **File:** `Mono.Cecil.dll` (355KB)
- **Method RVA:** deduced by Cecil tool
- **Fix:** Cecil assembly rewrite (not binary patch) — small DLL, Cecil can rewrite safely
- **New body:** `if (string.IsNullOrEmpty(fileName)) throw new BadImageFormatException(); return;`

## Key Technical Decisions

1. **Binary IL patches over JMP hooks for CodeAnalysis/Cecil** — BC loads these DLLs
   into a different AssemblyLoadContext. JMP hooks applied to eagerly-loaded instances
   don't affect the runtime-loaded instances. Binary patches modify the file itself.

2. **Reference assemblies over runtime DLLs** — 89 of 168 .NET 8 runtime DLLs are
   ReadyToRun (native), which crashes Cecil's PE/metadata reader. Reference assemblies
   (163 files, 4MB) are always managed and have full type signatures.

3. **Pre-clear all installed apps** — The dev endpoint refuses to replace AppSource
   dependencies. Clearing all published/installed app tables before BC starts gives a
   clean slate. BC boots instantly with no extensions to compile.

4. **Tiered compilation disabled** — `DOTNET_TieredCompilation=0` prevents Tier 1
   recompilation from overwriting JMP hooks on Watson reporting methods.

## Next Steps (Priority Order)

1. **Fix the assembly resolution priority** — Make Cecil prefer reference assemblies
   in Add-Ins over native DLLs in the .NET runtime path. This is the ONLY remaining
   blocker for System Application server-side compilation.

2. **Publish System Application + Test Library + Tests**
   - Once type resolution works, publish System Application via dev endpoint
   - Then publish Test Library and System Application Test

3. **Execute System Application tests and collect timing numbers**

4. **Update PipelinePerformanceComparison workflow** with the full fix chain

5. **Reduce ~6 min Win32Exception startup delay** (secondary goal)
