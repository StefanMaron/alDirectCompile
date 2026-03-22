# AL Standalone Test Runner (alDirectCompile)

## What This Is

A PoC that executes Business Central AL unit tests **without a BC server, database, or license**. Target: 500 tests in <15 seconds (vs 5-6 min today on a BC server).

## 4-Stage Pipeline

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

## Quick Start

```bash
# Simple spike tests (BaseApp + TestApp from source directories)
dotnet run --project AlRunner -- BaseApp TestApp

# Real BC .app packages with symbol references
dotnet run --project AlRunner -- \
  path/to/TestApp.app \
  path/to/SourceApp.app \
  --packages path/to/system/symbols/ \
  --packages path/to/other/dependencies/ \

# Debug: dump generated or rewritten C#
dotnet run --project AlRunner -- --dump-csharp <inputs...>
dotnet run --project AlRunner -- --dump-rewritten <inputs...>
```

## Project Structure

```
AlRunner/
├── Program.cs              — CLI, AlTranspiler, RoslynCompiler, Executor (top-level statements)
├── RoslynRewriter.cs       — CSharpSyntaxRewriter: all BC→mock transformations
├── Runtime/
│   ├── AlScope.cs          — Base scope class, AlDialog (Message/Error), AlCompat (type helpers)
│   ├── MockRecordHandle.cs — In-memory record store (replaces NavRecordHandle)
│   ├── MockCodeunitHandle.cs — Cross-codeunit dispatch via reflection
│   └── MockInterfaceHandle.cs — Interface handle stub
├── AlRunner.csproj         — References AL compiler + BC service tier DLLs
BaseApp/                    — Spike test: Table 50100 + Codeunit 50100
TestApp/                    — Spike test: Test Codeunit 50200
artifacts/                  — BC platform artifacts (not checked in, downloaded via BcContainerHelper)
```

## Key Architecture

### AlTranspiler (in Program.cs)
- Uses `Microsoft.Dynamics.Nav.CodeAnalysis` (BC compiler) public APIs
- `SyntaxTree.ParseObjectText()` for parsing AL, `Compilation.Emit()` for code generation
- `CompilationGenerationOptions.All` enables C# output
- Multi-app mode: each .app transpiled as separate AL compilation, others as symbol references (avoids AL0275 ambiguous reference errors)
- App identity extracted from `app.json` / `NavxManifest.xml` for correct InternalsVisibleTo resolution
- Symbol references resolved via `ReferenceLoaderFactory.CreateReferenceLoader()`

### RoslynRewriter (RoslynRewriter.cs)
A `CSharpSyntaxRewriter` with these transformation categories:

**Type replacements** (VisitIdentifierName):
- `INavRecordHandle` / `NavRecordHandle` → `MockRecordHandle`
- `NavCodeunitHandle` → `MockCodeunitHandle`
- `NavInterfaceHandle` → `MockInterfaceHandle`
- `NavVariant` → `object`
- `NavTextConstant` → `NavText` (avoids NavEnvironment initialization)
- `NavEventScope` → `object` (event scope type used for static fields)

**Constructor rewrites** (VisitObjectCreationExpression):
- `new NavRecordHandle(this, tableId, false, SecurityFiltering.X)` → `new MockRecordHandle(tableId)`
- `new NavCodeunitHandle(this, codeunitId)` → `MockCodeunitHandle.Create(codeunitId)`
- `new NavInterfaceHandle(this)` → `new MockInterfaceHandle()`
- `new NavRecordRef(this, ...)` → `new NavRecordRef(null!, ...)`
- `new NavTextConstant(langIds, strings, ...)` → `new NavText(strings[0])`

**Method/invocation rewrites** (VisitInvocationExpression):
- `NavDialog.ALMessage(session, guid, fmt, args)` → `AlDialog.Message(fmt, args)`
- `NavDialog.ALError(...)` → `AlDialog.Error(...)`
- `ALCompiler.ObjectToDecimal(x)` → `AlCompat.ObjectToDecimal(x)`
- `ALCompiler.ObjectToBoolean(x)` → `AlCompat.ObjectToBoolean(x)`
- `ALCompiler.ToVariant(this, x)` → `AlCompat.ToVariant(x)`
- `ALCompiler.NavValueToVariant(this, x)` → `AlCompat.ToVariant(x)`
- `ALCompiler.ToInterface(this, cu)` → `cu`
- `ALCompiler.ObjectToExactINavRecordHandle(x)` → `(MockRecordHandle)x`
- `ALCompiler.NavIndirectValueToDecimal(x)` → `AlCompat.ObjectToDecimal(x)`
- `ALCompiler.NavIndirectValueToINavRecordHandle(x)` → `(MockRecordHandle)x`
- `NavFormatEvaluateHelper.Format(session, value)` → `AlCompat.Format(value)`
- `ALSystemErrorHandling.ALClearLastError()` → `AlScope.LastErrorText = ""`
- `ALSystemErrorHandling.ALGetLastErrorTextFunc(...)` → `AlScope.LastErrorText`
- `StmtHit(N)` → removed (debug coverage)
- `CStmtHit(N)` → `true` (conditional coverage)
- `NavRuntimeHelpers.CompilationError(...)` → `throw new InvalidOperationException(...)`

**Member access rewrites** (VisitMemberAccessExpression):
- `base.Parent.xxx` → `_parent.xxx` (scope→codeunit access)
- `xxx.Target` → `xxx` (strip all .Target accessor on handles)
- `ALSystemErrorHandling.ALGetLastErrorText` → `AlScope.LastErrorText`
- `value.ALIsBoolean` (and 21 other ALIs* props) → `AlCompat.ALIsBoolean(value)`

**Class-level transformations** (VisitClassDeclaration):
- Base class: `NavCodeunit` / `NavTestCodeunit` / `NavRecord` / `NavFormExtension` / `NavRecordExtension` / `NavEventScope` / `NavUpgradeCodeunit` → removed
- Base class: `NavMethodScope<T>` / `NavTriggerMethodScope<T>` / `NavEventMethodScope<T>` → `AlScope`
- Scope classes get `private T _parent;` field added
- Scope constructors: `base(...)` initializer removed, `βparent` parameter kept, `_parent = βparent` assignment added
- Removed members: `__Construct`, `OnInvoke(int, object[])`, `OnRun(params)`, `GetMethodScopeFlags`, `IsInterfaceOfType`, `IsInterfaceMethod`, `ObjectName`, `IsCompiledForOnPremise`, `IsSingleInstance`, `Rec`/`xRec`, `RawScopeId`, `αscopeId`, `ParentObject`, `CurrPage`, `IndirectPermissionList`, `EventScope`, `MethodId`, `OnMetadataLoaded`, `EvaluateCaptionClass`, `EnsureGlobalVariablesInitialized`, `RegisterDynamicCaptionExpression`
- Override keyword removed from methods in classes whose BC base class was removed

**Statements removed** (VisitExpressionStatement):
- `StmtHit(N);`
- `ALGetTable(...)`, `ALClose()`, `RunEvent()` calls → replaced with empty statement
- `true;` (leftover from CStmtHit replacement)

**Additional rewrites** (added to fix NavEnvironment triggers):
- `ALCompiler.ToNavValue(x)` → `AlCompat.ToNavValue(x)` — our version returns `NavValue` by constructing the correct concrete subtype (NavInteger, NavDecimal, NavText, NavGuid, etc.) without going through NavValueFormatter/NavSession
- `ALCompiler.ObjectToExactNavValue<T>(x)` → `(T)(object)x` — direct cast
- `NCLEnumMetadata.Create(N)` → `NCLOptionMetadata.Default` — avoids `NavGlobal.MetadataProvider` → `NavEnvironment` chain

**NOT rewritten** (intentionally left as real BC calls):
- `ALSystemString.ALStrSubstNo(...)` — BC string formatting, works without NavSession
- `NavOption.Create(metadata, value)` — option creation using metadata from NCLOptionMetadata.Default

### RoslynCompiler (in Program.cs)
- Compiles rewritten C# in-memory against:
  - .NET runtime assemblies
  - All `Microsoft.Dynamics.Nav.*.dll` from BC service tier (wildcard discovery)
  - `AlRunner.Runtime` assembly
- No files written to disk

### Executor (in Program.cs)
- Auto-detects test codeunits (AL source contains `Subtype = Test`)
- Finds test scope classes: pattern `Test*_Scope_*` (excluding `OnRun_Scope`)
- Per test: reset MockRecordHandle tables, create parent codeunit via `GetUninitializedObject`, call `InitializeComponent`, create scope via constructor, invoke `OnRun()`
- Reports `[PASS]`/`[FAIL]` with stack traces

### Runtime Classes (AlRunner/Runtime/)

**AlScope** — base class for all scope classes (replaces NavMethodScope<T>):
- `OnRun()` / `Run()` entry point
- `StmtHit(n)` / `CStmtHit(n)` debug coverage stubs
- `AssertError(Action)` — AL's `asserterror` keyword implementation
- `LastErrorText` static property — for `GetLastErrorText()` support

**AlDialog** — replaces NavDialog:
- `Message(format, args)` — console output with AL `%1`→`{0}` format conversion
- `Error(format, args)` — throws Exception with formatted message

**AlCompat** — replaces various ALCompiler/NavFormatEvaluateHelper methods:
- `ToNavValue(object?) → NavValue` (creates correct NavValue subtype: NavInteger, NavDecimal, NavText, NavGuid, NavBoolean, NavBigInteger; handles Decimal18)
- `ObjectToDecimal(object?)`, `ObjectToBoolean(object?)`
- `ToVariant(object?)`, `Format(object?)`, `Format(object?, int, int)`
- 22 `ALIs*` type-check methods (e.g., `ALIsBoolean`, `ALIsDecimal`, `ALIsText`)

**MockRecordHandle** — in-memory record store:
- Per-table storage: `Dictionary<int, List<Dictionary<int, NavValue>>>`
- Supports: ALInit, ALInsert, ALModify, ALGet, ALFind, ALNext, ALDelete, ALDeleteAll, ALCount, ALFindSet, ALFindFirst, ALFindLast, ALIsEmpty, ALSetCurrentKey, ALSetAscending, ALFieldNo, ALReset, Clear
- Uses real BC `NavValue` types for field storage (Decimal18, NavText, NavCode, etc.)
- `ResetAll()` clears between tests
- **Not yet implemented**: SetRange/SetFilter filtering, CalcFields, TransferFields

**MockCodeunitHandle** — cross-codeunit dispatch:
- `Create(codeunitId)` factory method
- `Invoke(memberId, args)` — finds codeunit type by name `Codeunit{id}`, finds method by scope class name containing memberId, invokes via reflection
- Lazy instance creation with `InitializeComponent` call
- `ConvertArg()` handles int→Decimal18, object→NavVariant conversions

## Dependencies / Environment

- **AL Compiler**: `microsoft.dynamics.businesscentral.development.tools.linux` .NET tool (provides `Microsoft.Dynamics.Nav.CodeAnalysis.dll`)
- **BC Service Tier DLLs**: From BC artifacts at `artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service/` (provides NavValue, NavText, Decimal18, ALCompiler, etc.)
- **System symbols**: From `artifacts/onprem/27.5.46862.0/platform/ModernDev/pfiles/microsoft dynamics nav/270/al development environment/System.app`
- **Linux support**: `Kernel32Shim.EnsureRegistered()` provides `LCIDToLocaleName` P/Invoke via `NativeLibrary.SetDllImportResolver`
- **Runtime**: .NET 8

### Downloading BC Artifacts

```powershell
# Via BcContainerHelper PowerShell module
$artifactUrl = Get-BCArtifactUrl -type onprem -version 27.5 -country w1
Download-Artifacts -artifactUrl $artifactUrl -basePath ./artifacts
```

## NavEnvironment Pitfall

`Microsoft.Dynamics.Nav.Runtime.NavEnvironment` throws `TypeInitializationException` on Linux because `WindowsIdentity.GetCurrent()` fails. Known triggers and their fixes:
- ~~`NavTextConstant` constructor~~ → **fixed**: rewrite to `NavText`
- ~~`ALSystemErrorHandling` methods~~ → **fixed**: rewrite to `AlScope.LastErrorText`
- ~~`ALCompiler.ToNavValue()`~~ → **fixed**: rewrite to `AlCompat.ToNavValue()` which constructs NavValue subtypes directly
- ~~`NCLEnumMetadata.Create(N)`~~ → **fixed**: rewrite to `NCLOptionMetadata.Default` (avoids NavGlobal.MetadataProvider)

Any new BC type usage that chains into NavEnvironment will crash. The chain is: `NavEnvironment.Instance` → `NavGlobal.SystemTenant` → `NavGlobal.MetadataProvider`. When adding rewriter rules, always test that the rewritten code doesn't trigger NavEnvironment at runtime.

## Current Status

**Working**:
- Spike tests: 3/3 pass (BaseApp + TestApp from source dirs)
- Real Microsoft tests: **8/9 pass** (Recommended Apps Tests) — end-to-end from .al source → transpile → compile → execute
- Multi-app transpilation from .app packages
- NavEnvironment issue RESOLVED: All four triggers rewritten (NavTextConstant, ALSystemErrorHandling, ALCompiler.ToNavValue, NCLEnumMetadata.Create)

**End-to-end approach**: Combined directory at `/tmp/recomapps_combined/` with:
- Stubbed source app (Codeunit 4750/4751, Table 4750, Enum) — DotNet-free stubs of Recommended Apps Impl
- Stubbed Assert codeunit (130000) — minimal implementation with AreEqual/AreNotEqual/ExpectedError
- Test codeunit (139527) — real Microsoft test code, unmodified
- All compiled as single AL compilation against System.app symbols only

**Multi-app approach** also works (3 separate compilations):
```bash
dotnet run --project AlRunner -- \
  /tmp/recomapps_stubbed \
  "artifacts/.../RecommendedApps/Test/Microsoft_Recommended Apps Tests.app" \
  /tmp/assert_stub \
  --packages /tmp/al_packages
```
- Group 1: Stubbed source dir → 3 objects
- Group 2: Real Microsoft test .app → 1 object
- Group 3: Assert stub dir → 1 object
- Package dir (`/tmp/al_packages`): System.app, Application.app, Tests-TestLibraries.app, Library Assert.app, Library Variable Storage.app, System Application.app, System Application Test Library.app, Recommended Apps.app

**1 failing test is fundamentally out of scope**:
- TestRefreshImage: NavMedia field type not supported (needs media/image handling infrastructure)

**Rewriter coverage expanding**: ReviewGLEntries source app (29 AL files including Pages, PageExtensions, TableExtensions, Codeunits, event subscribers) reduced from 91 → 30 Roslyn errors. Remaining errors are mostly Page/TableExtension-specific (NavForm constructors, Page.Rec, Record extension methods).

**Not implemented**:
- SetRange/SetFilter record filtering (data-level filtering)
- CalcFields, TransferFields
- Event subscriptions (event scopes transpile but subscribers are no-ops)
- Report/Page/XMLPort execution
- NavMedia field type support
- Ready2Run .app DLL loading (for pre-compiled dependencies)
- Page/TableExtension runtime (Rec, CurrPage, SourceTable access patterns)

## Conventions

- **Fail-fast**: Unhandled features must throw `NotSupportedException`, never silently produce wrong results. A `[PASS]` must mean `PASS`.
- All core logic is in `Program.cs` (top-level statements style) — AlTranspiler, RoslynCompiler, Executor are nested static classes.
- RoslynRewriter is a separate file because it's large (~900 lines).
- Runtime mock classes are in `AlRunner/Runtime/` namespace.
- BC types (NavValue, NavText, Decimal18, etc.) are used directly where needed — we reference real BC DLLs.

## Notifications

Send progress/blocker notifications to: `https://ntfy.sh/zkbwzWH02Jwe3d8w`

```bash
curl -d "message here" https://ntfy.sh/zkbwzWH02Jwe3d8w
```
