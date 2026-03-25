# ALRunner — AL Test Runner for Business Central

## Vision

Execute Business Central AL unit tests on Linux in minutes, not the 45+ minutes
a full pipeline takes today.

## Container Infrastructure → MsDyn365Bc.On.Linux

The Docker container infrastructure (Dockerfile, docker-compose, entrypoint,
stubs, startup hook) has been extracted to a standalone repository:

**https://github.com/StefanMaron/MsDyn365Bc.On.Linux**

That repo provides the general-purpose BC-on-Linux container. This repo
(alDirectCompile) focuses on the test execution layer on top of it.

## Current Focus: Test Execution via MS Test Framework

The remaining challenge is getting the test runner API to work reliably so
tests can be executed the same way BcContainerHelper's `Run-TestsInBcContainer`
does on Windows. Specifically:

- **Page 130455 (Command Line Test Tool)** — the standard automation entry point
  used by BcContainerHelper. Needs to be registered as a web service and called
  via SOAP/OData.
- **Codeunit 130451 (Test Runner - Isol. Disabled)** — the test runner with
  disabled isolation, used by BCApps CI with `renewClientContextBetweenTests`.
- **Our TestRunner Extension (codeunit 50003/50004)** — custom API wrapper that
  uses the MS Test Framework internally. Works locally but fails in CI due to
  dependency chain issues with server-side compilation.

### What Works Locally (67/173 = 39% pass rate on BCApps System App Tests)

- BC v27.5 boots, compiles, publishes, runs tests
- MS Test Framework (130451) with disabled isolation
- Demo data initialization (codeunits 2, 5193, 5691)
- Per-method result tracking via AL Test Suite tables
- 11/20 pass rate (55%) for tests that actually execute (rest timeout/hang)

### Blocking Issues

1. **Hanging tests** — ~5 codeunits hang indefinitely (client callback/UI).
   10-min timeout + BC restart handles this but adds overhead.
2. **TestRunner Extension server-side compilation** — the custom API extension
   fails to publish via dev endpoint in CI because its dependency (MS Test
   Runner) is a source .app that needs server-side compilation, which requires
   patched CodeAnalysis.dll.
3. **patched CodeAnalysis.dll not in CI** — the binary patcher tool exists at
   `/tmp/PatchNetstandard/` locally but isn't in the repo. Need to either check
   in the tool or the patched DLLs.

## Two Approaches

### Approach 1: Headless Service Tier (PRIMARY)
Run the real BC service tier on Linux with Docker SQL Server. Patch only the
Linux-specific blockers via a .NET startup hook. Tests run in the real BC runtime
with 100% fidelity.

**Status (2026-03-25):** Full CI pipeline GREEN on GitHub Actions — compiles
System Application from BCApps source, publishes 9 apps, initializes demo data,
runs 174 test codeunits. Test execution infrastructure works but test runner
API needs fixing (see above).

```
Docker SQL Server 2022 (CRONUS database)
    ↑ TCP
DynamicsNavServer.Main() on Linux
    ↑ DOTNET_STARTUP_HOOKS
StartupHook.dll (15 patches for Linux compat)
    + 6 stub DLL subprojects + libwin32_stubs.so
    + 2 binary-patched DLLs (CodeAnalysis, Mono.Cecil)
    + 4 merged type-forward assemblies (netstandard, OpenXml, Drawing, Core)
    + 163 .NET 8 reference assemblies in Add-Ins
```

Key files:
- `StartupHook/StartupHook.cs` — All 15 patches (JMP hooks + assembly event handlers)
- `StartupHook/Dockerfile` — Docker container definition
- `StartupHook/kernel32_stubs.c` — C shared library for P/Invoke stubs
- `StartupHook/patched/` — Binary-patched DLLs + merged assemblies — not in git
- `StartupHook/refasm/` — .NET 8 reference assemblies for Add-Ins — not in git
- `StartupHook/GenevaStub/` — OpenTelemetry.Exporter.Geneva stub
- `StartupHook/DrawingStub/` — System.Drawing.Common stub
- `StartupHook/PerfCounterStub/` — PerformanceCounter stub
- `StartupHook/WindowsPrincipalStub/` — System.Security.Principal.Windows stub
- `StartupHook/HttpSysStub/` — Microsoft.AspNetCore.Server.HttpSys stub (redirects to Kestrel)
- `StartupHook/AclStub/` — (unused — ACL bypass done via topology proxy)
- `tools/MergeNetstandard/` — Tool to merge type-forward assemblies (Patch #15)
- `TestRunnerExtension/` — Custom test runner API (wraps MS Test Framework 130451)
- `docker-compose.yml` — Docker orchestration (BC + SQL Server 2022)
- `scripts/entrypoint.sh` — Container entrypoint (applies patches, starts BC)
- `scripts/run-system-tests.sh` — Runs BCApps System Application test suite
- `docs/ci-pipeline-progress.md` — Detailed session-by-session progress

### Approach 2: Standalone Transpiler (ALRunner — paused)
AL → C# → Roslyn rewrite → compile → run. Works for simple tests (8/9 pass)
but has fidelity concerns for complex tests (events, triggers, filters).
Kept as fallback and proof-of-concept.

Key files:
- `AlRunner/Program.cs` — CLI, transpiler, compiler, executor
- `AlRunner/RoslynRewriter.cs` — BC→mock type transformations
- `AlRunner/Runtime/` — Mock implementations

## Project Structure

```
StartupHook/           — Headless service tier patches (Approach 1)
  ├── StartupHook.cs
  ├── StartupHook.csproj
  ├── Dockerfile
  ├── kernel32_stubs.c
  ├── patched/            — Binary-patched DLLs + merged assemblies (not in git)
  ├── refasm/             — .NET 8 reference assemblies (not in git)
  ├── GenevaStub/         — Geneva ETW exporter stub
  ├── DrawingStub/        — System.Drawing.Common stub
  ├── PerfCounterStub/    — PerformanceCounter stub
  ├── WindowsPrincipalStub/ — WindowsIdentity/SecurityIdentifier stub
  ├── HttpSysStub/        — HttpSys→Kestrel redirect stub
  └── AclStub/            — (unused)
TestRunnerExtension/   — Custom test runner API
  ├── app.json           — Depends on MS Test Runner (130451)
  ├── src/TestSuiteRunner.Codeunit.al  — Wraps AL Test Suite infrastructure
  ├── src/RunnerTable.al  — API page for test execution
  └── TestRunner.app      — Compiled extension
AlRunner/              — Standalone transpiler (Approach 2)
  ├── Program.cs
  ├── RoslynRewriter.cs
  └── Runtime/
tools/
  └── MergeNetstandard/  — Merge tool for type-forward assemblies (Patch #15)
docker-compose.yml     — Docker orchestration
scripts/
  ├── entrypoint.sh    — BC container entrypoint
  ├── download-artifacts.sh — BC artifact downloader with version resolution
  └── run-system-tests.sh  — BCApps test suite runner
docs/
  ├── headless-service-tier.md
  ├── headless-decision-history.md
  ├── ci-pipeline-progress.md
  ├── performance-strategy.md
  ├── copilot-integration-pitch.md
  └── feature-compatibility-matrix.md
```

### Binary-patched DLLs (not checked in)

Two BC DLLs require binary IL patches for Linux compatibility. These are generated
by external tools and placed in `StartupHook/patched/`:

1. **Microsoft.Dynamics.Nav.CodeAnalysis.dll** — `IsTypeForwardingCircular` patched
   to return false (prevents NullRef when following netstandard type-forwarding chains)
2. **Mono.Cecil.dll** — `CheckFileName` patched to throw `BadImageFormatException`
   instead of `ArgumentNullOrEmptyException` for empty paths (caught by existing handlers)

Generate with the Cecil patcher tool at `/tmp/PatchNetstandard/` (or rebuild from
the IL byte offsets documented in `docs/ci-pipeline-progress.md`).

### Merged type-forward assemblies (not checked in)

Four assemblies have their type-forwards resolved into direct type definitions using
`tools/MergeNetstandard/`. Output goes to `StartupHook/patched/`:

1. **netstandard-merged.dll** — 2604 type-forwards from 80 assemblies
2. **DocumentFormat.OpenXml-merged.dll** — 91 type-forwards
3. **System.Drawing-merged.dll** — 172 type-forwards
4. **System.Core-merged.dll** — 248 type-forwards

Generate: `cd tools/MergeNetstandard && dotnet run`

### .NET 8 Reference Assemblies (not checked in)

163 reference assemblies from the .NET 8 SDK (`/usr/share/dotnet/packs/
Microsoft.NETCore.App.Ref/8.0.*/ref/net8.0/`) are copied to `StartupHook/refasm/`
and included in the Docker image. These provide type definitions for Cecil's
type-forwarding resolution without the R2R/native format that crashes Cecil.

## Dependencies / Environment

- **BC Artifacts**: v27.5.46862.0 at `artifacts/onprem/27.5.46862.0/`
- **AL Compiler**: `microsoft.dynamics.businesscentral.development.tools.linux`
- **Runtime**: .NET 8 + ASP.NET Core 8.0
- **Decompiler**: `ilspycmd` (for investigating BC DLLs)
- **Docker**: For SQL Server 2022 container + BC service tier
- **MockTest.dll**: From `artifacts/onprem/.../platform/Test Assemblies/Mock Assemblies/`
- **Container repo**: https://github.com/StefanMaron/MsDyn365Bc.On.Linux

### Downloading BC Artifacts

```powershell
$artifactUrl = Get-BCArtifactUrl -type onprem -version 27.5 -country w1
Download-Artifacts -artifactUrl $artifactUrl -basePath ./artifacts
```

## Design Principles

- **100% test fidelity**: Test results must match production BC exactly
- **Fail-fast**: Unhandled features throw `NotSupportedException`
- **Minimal patches**: Only patch what crashes on Linux, keep everything else real
- **Binary IL patches**: Only when JMP hooks fail (tiered compilation, assembly context)
- **Merged assemblies**: When BC's type-forwarding resolution fails silently
- **Granular commits** with descriptive messages

## Notifications

```bash
curl -d "message here" https://ntfy.sh/zkbwzWH02Jwe3d8w
```
