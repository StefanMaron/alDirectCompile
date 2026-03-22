# ALRunner — AL Standalone Test Runner

## Vision

Execute Business Central AL unit tests **without a BC server, database, or license**.
North star: run the 40,000+ base application tests on Linux in minutes, not the 45+ minutes a full pipeline takes today.

## Architecture

### Why Roslyn Rewriting (Not Assembly Shimming)

Investigation of the BC service tier DLLs (see `docs/runtime-analysis.md`) found that
the runtime is deeply entangled with `NavSession`/`NavEnvironment`. Every BC runtime type
chains back to `NavSession → NavTenant → NavDatabase → SQL Server`. The storage boundary
(`DataProvider`) is `internal abstract` — cannot be implemented externally. `NavEnvironment`
crashes on Linux via `WindowsIdentity.GetCurrent()`.

**Result:** We cannot reference real `Nav.Ncl.dll` types and just swap the storage backend.
Instead, we rewrite the transpiled C# to use our own mock types that provide equivalent
behavior without the infrastructure dependencies.

### 4-Stage Pipeline

```
AL Source (.al files or .app packages)
    ↓  AlTranspiler     — BC compiler public API: Compilation.Emit()
Generated C# Code
    ↓  RoslynRewriter    — CSharpSyntaxRewriter: BC types → mock types
Rewritten C# Code
    ↓  RoslynCompiler    — Roslyn in-memory compilation
.NET Assembly
    ↓  Executor          — test discovery + invocation via reflection
Test Results
```

### What IS Reused From Microsoft

- `Nav.Types.dll`: All NavValue types (NavText, NavCode, Decimal18, NavInteger, NavBoolean, NavGuid, etc.) — pure value types, no infrastructure deps
- `Nav.Language.dll`: `ALSystemString.ALStrSubstNo()` — BC string formatting, works without NavSession
- `Nav.Common.dll`: Culture utilities
- `Nav.Types.dll`: NavOption, NCLOptionMetadata, NavType enum, DataError enum

### What Is Replaced (Nav.Ncl.dll types)

These types are in the transpiled C# but cannot be used from the real assembly:

| BC Type | Replaced With | Why |
|---|---|---|
| `NavRecordHandle`/`INavRecordHandle` | `MockRecordHandle` | Requires NavSession + SQL via DataProvider |
| `NavCodeunitHandle` | `MockCodeunitHandle` | Requires ITreeObject → NavSession |
| `NavInterfaceHandle` | `MockInterfaceHandle` | Requires ITreeObject → NavSession |
| `NavCodeunit`/`NavTestCodeunit` | Removed (base class stripped) | 200+ session dependencies |
| `NavMethodScope<T>` | `AlScope` | NavSession-dependent scope management |
| `NavDialog` | `AlDialog` | Session-dependent dialog system |
| `ALCompiler` static methods | `AlCompat` | Some methods chain to NavEnvironment |
| `NavTextConstant` | `NavText` | Constructor triggers NavEnvironment |
| `NavVariant` | `object` | Simple type alias |
| `ALSystemErrorHandling` | `AlScope.LastErrorText` | NavCurrentThread.Session dependency |
| `NCLEnumMetadata.Create()` | `NCLOptionMetadata.Default` | NavGlobal.MetadataProvider dependency |

### Future: Shim Assembly

Once rewriter patterns stabilize, they can be extracted into a shim assembly
(`AlRunner.NavShim.dll`) with matching namespace/type names. The transpiled C#
would compile against the shim instead of real Nav.Ncl.dll, eliminating Roslyn
rewriting entirely. Each rewriter rule maps 1:1 to a shim method/type.

## Quick Start

```bash
# Spike tests (BaseApp + TestApp from source directories)
dotnet run --project AlRunner -- BaseApp TestApp

# Real BC .app packages with symbol references
dotnet run --project AlRunner -- \
  path/to/TestApp.app \
  path/to/SourceApp.app \
  --packages path/to/system/symbols/ \
  --packages path/to/other/dependencies/

# Debug: dump generated or rewritten C#
dotnet run --project AlRunner -- --dump-csharp <inputs...>
dotnet run --project AlRunner -- --dump-rewritten <inputs...>
```

## Project Structure

```
AlRunner/
├── Program.cs              — CLI, AlTranspiler, RoslynCompiler, Executor
├── RoslynRewriter.cs       — CSharpSyntaxRewriter: all BC→mock transformations
├── Runtime/
│   ├── AlScope.cs          — Base scope class, AlDialog, AlCompat
│   ├── MockRecordHandle.cs — In-memory record store
│   ├── MockCodeunitHandle.cs — Cross-codeunit dispatch via reflection
│   └── MockInterfaceHandle.cs — Interface handle stub
├── AlRunner.csproj         — References AL compiler + BC service tier DLLs
BaseApp/                    — Spike test: Table 50100 + Codeunit 50100
TestApp/                    — Spike test: Test Codeunit 50200
artifacts/                  — BC platform artifacts (not checked in)
docs/
├── runtime-analysis.md     — BC service tier DLL investigation
├── gap-analysis-real-ms-tests.md
└── feature-compatibility-matrix.md
```

## Key Components

### AlTranspiler (Program.cs)
- Uses `Microsoft.Dynamics.Nav.CodeAnalysis` (BC compiler) public APIs
- `SyntaxTree.ParseObjectText()` for parsing AL, `Compilation.Emit()` for C# generation
- `CompilationGenerationOptions.All` enables C# output
- Multi-app mode: each .app transpiled as separate compilation, others as symbol references
- App identity from `app.json` / `NavxManifest.xml` for InternalsVisibleTo resolution
- Symbol references via `ReferenceLoaderFactory.CreateReferenceLoader()`

### RoslynRewriter (RoslynRewriter.cs)
A `CSharpSyntaxRewriter` (~1000 lines) that transforms transpiled C# for standalone execution.
Transformation categories:

- **Type replacements**: NavRecordHandle→MockRecordHandle, NavCodeunitHandle→MockCodeunitHandle, etc.
- **Constructor rewrites**: Strip ITreeObject params, simplify to mock constructors
- **Method rewrites**: ALCompiler→AlCompat, NavDialog→AlDialog, strip session params
- **Class transforms**: Remove BC base classes, replace scope bases with AlScope, add _parent field
- **Member removal**: Strip BC infrastructure members (__Construct, OnInvoke, ObjectName, etc.)
- **Statement removal**: StmtHit (debug coverage), ALGetTable, ALClose, RunEvent

See the rewriter source for the complete rule set.

### Runtime Mocks (AlRunner/Runtime/)

**AlScope** — Base class for scope classes (replaces NavMethodScope<T>):
- `AssertError(Action)` — AL's `asserterror` keyword
- `LastErrorText` static property for error handling

**MockRecordHandle** — In-memory record store (replaces NavRecordHandle/NavRecord):
- Per-table storage: `Dictionary<int, List<Dictionary<int, NavValue>>>`
- CRUD: ALInit, ALInsert, ALModify, ALGet, ALFind/ALFindSet/ALFindFirst/ALFindLast, ALNext, ALDelete/ALDeleteAll
- Uses real BC NavValue types for field storage
- **Not yet implemented**: SetRange/SetFilter filtering, CalcFields, TransferFields

**MockCodeunitHandle** — Cross-codeunit dispatch (replaces NavCodeunitHandle):
- Reflection-based: finds Codeunit{id} type, invokes scope methods
- Lazy instance creation with InitializeComponent

**AlCompat** — Type conversion (replaces ALCompiler static methods):
- ToNavValue, ObjectToDecimal, ObjectToBoolean, ToVariant, Format
- 22 ALIs* type-check methods

**AlDialog** — Dialog replacement:
- Message/Error with AL %1→{0} format conversion

### RoslynCompiler (Program.cs)
Compiles rewritten C# in-memory against:
- .NET runtime assemblies
- All `Microsoft.Dynamics.Nav.*.dll` from BC service tier
- `AlRunner.Runtime` assembly

### Executor (Program.cs)
- Auto-detects test codeunits (AL source contains `Subtype = Test`)
- Scope class pattern: `Test*_Scope_*` (excluding `OnRun_Scope`)
- Per test: reset tables, create codeunit via GetUninitializedObject, call InitializeComponent, create scope, invoke OnRun()
- Reports [PASS]/[FAIL] with stack traces

## Dependencies / Environment

- **AL Compiler**: `microsoft.dynamics.businesscentral.development.tools.linux` .NET tool
- **BC Service Tier DLLs**: From BC artifacts at `artifacts/onprem/27.5.46862.0/platform/ServiceTier/...`
- **System symbols**: From `artifacts/.../ModernDev/.../System.app`
- **Linux support**: `Kernel32Shim.EnsureRegistered()` for LCIDToLocaleName P/Invoke
- **Runtime**: .NET 8

### Downloading BC Artifacts

```powershell
$artifactUrl = Get-BCArtifactUrl -type onprem -version 27.5 -country w1
Download-Artifacts -artifactUrl $artifactUrl -basePath ./artifacts
```

## NavEnvironment Pitfall

`NavEnvironment` throws `TypeInitializationException` on Linux (`WindowsIdentity.GetCurrent()`).
Chain: `NavEnvironment.Instance` → `NavGlobal.SystemTenant` → `NavGlobal.MetadataProvider`.

Known triggers (all fixed via rewriter):
- `NavTextConstant` ctor → rewrite to `NavText`
- `ALSystemErrorHandling` → rewrite to `AlScope.LastErrorText`
- `ALCompiler.ToNavValue()` → rewrite to `AlCompat.ToNavValue()`
- `NCLEnumMetadata.Create()` → rewrite to `NCLOptionMetadata.Default`

When adding rewriter rules, always test that rewritten code doesn't trigger NavEnvironment.

## Current Status

**Working**:
- Spike tests: 3/3 pass
- Real Microsoft tests: 8/9 pass (Recommended Apps Tests)
- Multi-app transpilation from .app packages
- NavEnvironment workarounds complete

**Not implemented**:
- SetRange/SetFilter record filtering
- CalcFields, TransferFields
- Event subscriptions (transpile but no-op)
- Report/Page/XMLPort execution
- CRONUS data fixtures
- HandlerFunctions support

## Design Principles

- **Fail-fast**: Unhandled features throw `NotSupportedException`. A `[PASS]` must mean PASS.
- **Reuse real BC types** where they work standalone (NavValue, NavText, Decimal18, etc.)
- **Mock only what touches infrastructure** (NavSession, NavEnvironment, SQL)
- **Granular commits** with descriptive messages
- **Track progress quantitatively**: test pass rates, blocker counts

## Roadmap

1. **100 tests**: Expand to System Application Test suite (regex, barcode, cryptography tests)
2. **500 tests**: Add SetRange/SetFilter, CRONUS data fixtures, HandlerFunctions
3. **5,000 tests**: CalcFields, event subscriptions, cross-app dispatch
4. **40,000 tests**: Full base application test coverage

## Notifications

```bash
curl -d "message here" https://ntfy.sh/zkbwzWH02Jwe3d8w
```
