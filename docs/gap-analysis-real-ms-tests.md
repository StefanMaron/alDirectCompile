# Gap Analysis: Running Real Microsoft Test Apps Through AlRunner

**Date:** 2026-03-21
**Test Subject:** Bank Deposits Tests (3 AL files, 73 test methods)
**App Path:** `artifacts/onprem/27.5.46862.0/platform/Applications/BankDeposits/Test/Microsoft__Exclude_Bank Deposits Tests.app`

## Summary

| Stage | Status | Detail |
|-------|--------|--------|
| **1. AL Transpilation** | BLOCKED without refs, PASS with refs | Needs `Compilation.AddReferences()` + `WithReferenceLoader()` |
| **2. C# Rewriting** | NOT TESTED (many known gaps) | See RoslynRewriter gaps below |
| **3. Roslyn Compilation** | NOT TESTED | Many missing mock types expected |
| **4. Runtime Execution** | NOT TESTED | Massive mock surface area needed |

## Test App Profile

- **Codeunits:** 3 (Bank Deposit Posting Tests, UT Page Bank Deposit, UT Report Bank Deposit)
- **Test methods:** 73 total (24 + 29 + 20)
- **Handler functions:** 20 (confirm, modal page, message, report request page)
- **Event subscribers:** 6
- **Generated C# lines:** 10,427 (3,728 + 3,866 + 2,835)

## Stage 1: AL Transpilation

### Without symbol references (current AlRunner)
**Result: CRASH** - `AggregateException` during `Compilation.Emit()`

The AL compiler cannot resolve external types (Records, Codeunits, Enums, Pages, Reports from dependent apps). Three distinct errors:
- `NavTypeKind.None` - Cannot determine type of variables referencing external Record/Codeunit types
- `BoundKind.BadExpression` - Cannot emit calls to methods on unresolved types

The `continueBuildOnError: true` option does not prevent the crash; the errors happen in the method-level emitter which throws `AggregateException`.

### With symbol references (proven approach)
**Result: SUCCESS** - All 3 codeunits transpiled to C# (700KB total)

Required API changes to `AlTranspiler.TranspileMulti()`:
```csharp
// Build package cache from all .app directories
var refLoader = ReferenceLoaderFactory.CreateReferenceLoader(packageCachePaths);

// Add dependency specifications from manifest
var deps = new List<SymbolReferenceSpecification>();
deps.Add(SymbolReferenceSpecification.PlatformReference(new Version("27.0.0.0")));
deps.Add(SymbolReferenceSpecification.ApplicationReference(new Version("27.5.0.0")));
// + explicit app dependencies from NavxManifest.xml

compilation = compilation.WithReferenceLoader(refLoader).AddReferences(deps.ToArray());
```

**Key APIs discovered:**
- `ReferenceLoaderFactory.CreateReferenceLoader(IEnumerable<string> packageCachePaths)` - takes directories containing .app files
- `Compilation.AddReferences(SymbolReferenceSpecification[])` - adds dependency references
- `Compilation.WithReferenceLoader(ISymbolReferenceLoader)` - sets the loader for resolving references
- `SymbolReferenceSpecification.PlatformReference(version)` / `.ApplicationReference(version)` - built-in helpers
- `NavAppPackageReader.Create(stream, leaveOpen, path)` - official API to read .app packages

**Remaining transpilation issue:** `[AL1022]` warning: System platform package not found. Despite this, all 3 codeunits emit successfully. Likely needs the System.app from ModernDev directory.

**24 warnings:** All `[AL0603]` - implicit enum-to-option conversions (benign).

## Stage 2: RoslynRewriter Gaps

### Attributes not yet removed
| Attribute | Occurrences | Action needed |
|-----------|-------------|---------------|
| `[NavHandler(...)]` | 20 | Remove |
| `[NavEvent(...)]` | 6 | Remove |
| `[NavTestPermissions(...)]` | ~3 | Remove |

### Base classes not yet handled
| Pattern | Occurrences | Action needed |
|---------|-------------|---------------|
| `NavEventMethodScope<X>` | 6 | Rewrite to `: AlScope` |

### Types not yet rewritten
| Type | Occurrences | Current handling | Action needed |
|------|-------------|------------------|---------------|
| `NavTestPageHandle` | 154 | None | New MockTestPageHandle |
| `NavReportHandle` | 8 | None | New MockReportHandle |
| `NavOption` / `NavOption.Create()` | 152 | None (BC runtime type) | Keep (exists in Nav.Types.dll) or mock |
| `NavTextConstant` | 27 | None (BC runtime type) | Keep (exists in Nav.Types.dll) |
| `NavCode` / `NavCode.Default()` | 6 | None (BC runtime type) | Keep (exists in Nav.Types.dll) |
| `NavDate` / `NavDate.Create()` | 2 | None (BC runtime type) | Keep (exists in Nav.Types.dll) |
| `NavArray` | present | None | Keep (BC runtime type) |
| `NavOptionMetadata` | present | None | Keep (BC runtime type) |
| `NavRecordToVariant` | present | None | Keep (BC runtime type) |
| `NavValueToVariant` | present | None | Keep (BC runtime type) |
| `NavNCLInvalidNumberOfArgumentsException` | present | None | Keep (BC runtime type) |

### Method call patterns not yet rewritten
| Pattern | Occurrences | Action needed |
|---------|-------------|---------------|
| `NavForm.Run(...)` | 8 | Mock or rewrite |
| `NavCodeunit.RunCodeunit(...)` | 2 | Mock or rewrite |
| `NavFormatEvaluateHelper.Format(...)` | 24 | Mock or rewrite |
| `NavRecordHandle.Factory2(...)` | 4 | Mock or rewrite |
| `NavRuntimeHelpers.CompilationError(...)` | 97 | Already partially handled |

## Stage 3: MockRecordHandle Method Gaps

The current MockRecordHandle implements 16 methods. The generated C# calls 47 unique `.Target.*` methods. **31 methods are missing:**

### Record methods (on MockRecordHandle) - 27 missing
| Method | Type | Difficulty |
|--------|------|-----------|
| `ALFindFirst` | Query | Medium - shorthand for Find('-') |
| `ALFindLast` | Query | Medium - shorthand for Find('+') |
| `ALFindSet` | Query | Medium - returns multiple records |
| `ALLast` | Query | Low - move to last record |
| `ALCalcFields` | Calculation | High - computed fields |
| `ALCalcSums` | Calculation | High - aggregate computation |
| `ALFieldCaption` | Metadata | Medium - field name lookup |
| `ALFieldNo` | Metadata | Medium - field number lookup |
| `ALTableCaption` | Metadata | Low - table name |
| `ALSetFilter` | Filtering | Medium - expression-based filter |
| `ALSetRecFilter` | Filtering | Medium - set filter from primary key |
| `ALSetAutoCalcFields` | Filtering | Low - no-op acceptable |
| `ALSetLoadFields` | Filtering | Low - no-op acceptable |
| `ALFilter` | Filtering | Medium - get current filter string |
| `ALValidateSafe` | Data | High - trigger field validation |
| `ALTestFieldSafe` | Data | Medium - assert field value |
| `ALTestFieldNavValueSafe` | Data | Medium - assert field nav value |
| `ALGoToRecord` | Navigation | Medium - position to specific record |
| `ALRename` | Data | Medium - rename primary key |
| `ALAddLink` | Links | Low - stub |
| `ALHasLinks` | Links | Low - stub returning false |
| `ALClose` | Lifecycle | Low - no-op |
| `ALOpenView` | UI | Medium - relates to page |
| `ALOpenEdit` | UI | Medium - relates to page |
| `ALOpenNew` | UI | Medium - relates to page |
| `ALSaveAsXml` | Report | High - report execution |
| `ALTrap` | Testing | Medium - test page trap |

### TestPage methods (on MockTestPageHandle - does not exist yet) - 4 missing
| Method | Type |
|--------|------|
| `GetField` | Page field access |
| `GetPart` | Subpage access |
| `GetAction` | Page action access |
| `GetBuiltInAction` | Built-in action invocation |
| `GetDataItem` | Report data item access |

### Codeunit methods on Target - already handled
| Method | Status |
|--------|--------|
| `Invoke` | Handled via MockCodeunitHandle |

## Stage 4: New Mock Types Needed

### MockTestPageHandle
- 154 occurrences across all 3 test files
- Must support: `GetField`, `GetPart`, `GetAction`, `GetBuiltInAction`, `ALClose`, `ALTrap`, `ALOpenEdit`, `ALOpenNew`
- This is the biggest gap - test page interaction is core to BC test methodology

### MockReportHandle
- 8 occurrences
- Must support: `ALSaveAsXml`, `GetDataItem`, `ALTrap`

### EventScope / Handler Infrastructure
- Tests use `[HandlerFunctions('...')]` pattern - each test declares which handler functions handle modal dialogs, messages, confirms
- The generated C# has `NavHandler` attributes and `NavEvent` attributes on handler methods
- Need infrastructure to route modal dialog/confirm/page invocations to handler functions during test execution

## Categories of Work (Priority Order)

### P0: Enable transpilation of any real-world AL app
- **Add symbol reference support to AlRunner** - Use `Compilation.AddReferences()` + `WithReferenceLoader()` + `ReferenceLoaderFactory.CreateReferenceLoader()`
- **Auto-detect dependencies** from `NavxManifest.xml` inside the .app package
- **Scan artifact directories** for .app packages to build package cache
- Estimated effort: Small (API exists, just needs wiring)

### P1: RoslynRewriter completeness
- **Remove 3 more attribute types**: `NavHandler`, `NavEvent`, `NavTestPermissions`
- **Handle `NavEventMethodScope<X>`** base class -> AlScope
- **Rewrite `NavTestPageHandle`** -> MockTestPageHandle
- **Rewrite `NavReportHandle`** -> MockReportHandle
- Estimated effort: Small-Medium (pattern matching, similar to existing rewrites)

### P2: MockRecordHandle method completeness
- **27 missing methods** on MockRecordHandle
- Most are stubs or simple (ALFindFirst = Find("-"), ALClose = no-op, ALFieldCaption = return "Field N")
- High-complexity: ALValidateSafe (trigger validation), ALCalcFields, ALCalcSums
- Estimated effort: Medium (many methods, but most are straightforward)

### P3: New mock types
- **MockTestPageHandle** - field access, action invocation, subpage navigation
- **MockReportHandle** - report execution stub
- **Handler routing infrastructure** - connect `[HandlerFunctions]` to handler methods during test execution
- Estimated effort: Large (new subsystem, test-specific infrastructure)

### P4: Keep BC runtime types working
- Types like `NavOption`, `NavTextConstant`, `NavCode`, `NavDate`, `Decimal18` come from `Nav.Types.dll`
- These are already referenced in compilation but need the runtime DLLs loaded
- Current AlRunner already loads these from ServiceTier
- Main risk: some types need initialization (WindowsLanguageHelper etc.)
- Estimated effort: Small (already mostly working)

## Appendix: Files Involved

### Test app
- `artifacts/onprem/27.5.46862.0/platform/Applications/BankDeposits/Test/Microsoft__Exclude_Bank Deposits Tests.app`
  - `src/src/BankDepositPostingTests.Codeunit.al` (24 tests)
  - `src/src/UTPageBankDeposit.Codeunit.al` (29 tests)
  - `src/src/UTReportBankDeposit.Codeunit.al` (20 tests)

### Dependencies (from NavxManifest.xml)
- `_Exclude_Bank Deposits` (7a129d06-5fd6-4fb6-b82b-0bf539c779d0)
- `Tests-TestLibraries` (5d86850b-0d76-4eca-bd7b-951ad998e997)
- `Library Variable Storage` (5095f467-0a01-4b99-99d1-9ff1237d286f)
- Platform >= 27.0.0.0
- Application >= 27.5.0.0

### Generated C#
- `/tmp/bank_deposits_csharp/Bank_Deposit_Posting_Tests.cs` (252,552 chars, 3,728 lines)
- `/tmp/bank_deposits_csharp/UT_Page_Bank_Deposit.cs` (246,110 chars, 3,866 lines)
- `/tmp/bank_deposits_csharp/UT_Report_Bank_Deposit.cs` (203,046 chars, 2,835 lines)

### Key AlRunner files
- `AlRunner/Program.cs` - Main pipeline, AlTranspiler, RoslynCompiler, AppPackageReader, Executor
- `AlRunner/RoslynRewriter.cs` - C# syntax rewriter (571 lines)
- `AlRunner/Runtime/MockRecordHandle.cs` - Record handle mock (16 methods)
- `AlRunner/Runtime/MockCodeunitHandle.cs` - Codeunit handle mock
- `AlRunner/Runtime/AlScope.cs` - Scope base class
