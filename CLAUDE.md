# ALRunner — AL Test Runner for Business Central

## Vision

Execute Business Central AL unit tests on Linux in minutes, not the 45+ minutes
a full pipeline takes today.

## Two Approaches (Active)

### Approach 1: Headless Service Tier (PRIMARY — nearly working)
Run the real BC service tier on Linux with Docker SQL Server. Patch only the
Linux-specific blockers via a .NET startup hook. Tests run in the real BC runtime
with 100% fidelity.

**Status (2026-03-24):** Full CI pipeline GREEN on GitHub Actions for sample
extensions. BC v27.5 boots, compiles extensions, publishes apps, runs 5 AL tests
via OData API. BCApps System Application (1264 files) compiles in ~6s locally.

Server-side compilation resolves all .NET types (Patch #15 — merged type-forward
assemblies). AL0452 (type not found) errors: **236 → 0**. The remaining 97 errors
are AL0132/AL0122/AL0133 (missing members on stub types in merged assemblies).
These are fixable by improving the merge tool to copy complete member metadata.

**See `docs/ci-pipeline-progress.md` for detailed session progress.**
**See `docs/performance-strategy.md` for optimization plans.**

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
- `docker-compose.yml` — Docker orchestration (BC + SQL Server 2022)
- `scripts/setup-sql.sh` — CRONUS database restore
- `scripts/entrypoint.sh` — Container entrypoint (applies patches, clears apps, starts BC)
- `docs/headless-service-tier.md` — Architecture, decisions, progress
- `docs/headless-decision-history.md` — Why this approach was chosen
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
  ├── refasm/             — .NET 8 reference assemblies (not in git, see below)
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
tools/
  └── MergeNetstandard/  — Merge tool for type-forward assemblies (Patch #15)
docker-compose.yml     — Docker orchestration
scripts/
  ├── setup-sql.sh     — CRONUS database restore
  └── entrypoint.sh    — BC container entrypoint
BaseApp/               — Spike test source
TestApp/               — Spike test source
artifacts/             — BC platform artifacts (not checked in)
docs/
  ├── headless-service-tier.md
  ├── headless-decision-history.md
  ├── ci-pipeline-progress.md
  ├── performance-strategy.md
  ├── runtime-analysis.md
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
