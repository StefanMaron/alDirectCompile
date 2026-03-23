# ALRunner — AL Test Runner for Business Central

## Vision

Execute Business Central AL unit tests on Linux in minutes, not the 45+ minutes
a full pipeline takes today.

## Two Approaches (Active)

### Approach 1: Headless Service Tier (PRIMARY — nearly working)
Run the real BC service tier on Linux with Docker SQL Server. Patch only the
Linux-specific blockers via a .NET startup hook. Tests run in the real BC runtime
with 100% fidelity.

**Status (2026-03-23):** BC v27.5 boots on Linux, SQL connected, all extensions
compiled, 7 Kestrel API hosts created. ONE remaining blocker (SPN registration).

**See `docs/headless-service-tier.md` for full details.**

```
Docker SQL Server 2022 (CRONUS database)
    ↑ TCP
DynamicsNavServer.Main() on Linux
    ↑ DOTNET_STARTUP_HOOKS
StartupHook.dll (12 patches for Linux compat)
    + 6 stub DLL subprojects + libwin32_stubs.so
```

Key files:
- `StartupHook/StartupHook.cs` — All patches
- `StartupHook/Dockerfile` — Docker container definition
- `StartupHook/kernel32_stubs.c` — C shared library for P/Invoke stubs
- `StartupHook/GenevaStub/` — OpenTelemetry.Exporter.Geneva stub
- `StartupHook/DrawingStub/` — System.Drawing.Common stub
- `StartupHook/PerfCounterStub/` — PerformanceCounter stub
- `StartupHook/WindowsPrincipalStub/` — System.Security.Principal.Windows stub
- `StartupHook/HttpSysStub/` — Microsoft.AspNetCore.Server.HttpSys stub (redirects to Kestrel)
- `StartupHook/AclStub/` — (unused — ACL bypass done via topology proxy)
- `docker-compose.yml` — Docker orchestration (BC + SQL Server 2022)
- `scripts/setup-sql.sh` — CRONUS database restore
- `docs/headless-service-tier.md` — Architecture, decisions, progress
- `docs/headless-decision-history.md` — Why this approach was chosen

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
  ├── GenevaStub/         — Geneva ETW exporter stub
  ├── DrawingStub/        — System.Drawing.Common stub
  ├── PerfCounterStub/    — PerformanceCounter stub
  ├── WindowsPrincipalStub/ — WindowsIdentity/SecurityIdentifier stub
  ├── HttpSysStub/        — HttpSys→Kestrel redirect stub
  └── AclStub/            — (unused)
AlRunner/              — Standalone transpiler (Approach 2)
  ├── Program.cs
  ├── RoslynRewriter.cs
  └── Runtime/
docker-compose.yml     — Docker orchestration
scripts/
  └── setup-sql.sh     — CRONUS database restore
BaseApp/               — Spike test source
TestApp/               — Spike test source
artifacts/             — BC platform artifacts (not checked in)
docs/
  ├── headless-service-tier.md
  ├── headless-decision-history.md
  ├── runtime-analysis.md
  └── feature-compatibility-matrix.md
```

## Dependencies / Environment

- **BC Artifacts**: v27.5.46862.0 at `artifacts/onprem/27.5.46862.0/`
- **AL Compiler**: `microsoft.dynamics.businesscentral.development.tools.linux`
- **Runtime**: .NET 8 + ASP.NET Core 8.0
- **Decompiler**: `ilspycmd` (for investigating BC DLLs)
- **Docker**: For SQL Server 2022 container + BC service tier

### Downloading BC Artifacts

```powershell
$artifactUrl = Get-BCArtifactUrl -type onprem -version 27.5 -country w1
Download-Artifacts -artifactUrl $artifactUrl -basePath ./artifacts
```

## Design Principles

- **100% test fidelity**: Test results must match production BC exactly
- **Fail-fast**: Unhandled features throw `NotSupportedException`
- **Minimal patches**: Only patch what crashes on Linux, keep everything else real
- **Granular commits** with descriptive messages

## Notifications

```bash
curl -d "message here" https://ntfy.sh/zkbwzWH02Jwe3d8w
```
