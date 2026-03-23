# ALRunner — AL Test Runner for Business Central

## Vision

Execute Business Central AL unit tests on Linux in minutes, not the 45+ minutes
a full pipeline takes today.

## Two Approaches (Active)

### Approach 1: Headless Service Tier (PRIMARY — in progress)
Run the real BC service tier on Linux with Docker SQL Server. Patch only the
Linux-specific blockers via a .NET startup hook. Tests run in the real BC runtime
with 100% fidelity.

**See `docs/headless-service-tier.md` for full details.**

```
Docker SQL Server (CRONUS database)
    ↑ TCP
DynamicsNavServer.Main() on Linux
    ↑ DOTNET_STARTUP_HOOKS
StartupHook.dll (JMP patches for Linux compat)
```

Key files:
- `StartupHook/StartupHook.cs` — All patches
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
AlRunner/              — Standalone transpiler (Approach 2)
  ├── Program.cs
  ├── RoslynRewriter.cs
  └── Runtime/
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
- **Docker**: For SQL Server container

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
