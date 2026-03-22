# AL Standalone Test Runner — Spike Findings

**Date:** 2026-03-21
**Status:** PoC COMPLETE
**Environment:** Arch Linux, .NET 8/10, no BC server, no database, no license

---

## Executive Summary

A standalone AL test runner that transpiles AL source to C#, compiles it in-memory with Roslyn, and executes tests — all on Linux with no BC server, database, or license.

```
AL Source Code (BaseApp/ + TestApp/)
    ↓  Compilation.Emit()              [BC compiler public API]
Generated C# Code
    ↓  RoslynRewriter                  [CSharpSyntaxRewriter → mock types]
Rewritten C# Code
    ↓  RoslynCompiler                  [in-memory compilation]
.NET Assembly
    ↓  Executor                        [test discovery + invocation]
Test Results: 2/2 PASS ✓
```

### Running It

```bash
dotnet run --project AlRunner -- BaseApp TestApp
```

### Test Results

| Test | Input | Expected | Actual | Status |
|------|-------|----------|--------|--------|
| TestApplyDiscount | price=100, pct=10 | 90 | 90 | PASS |
| TestDoublePrice | price=50 | 100 | 100 | PASS |

---

## Architecture (4-Stage Pipeline)

### 1. AlTranspiler
AL source to C# via BC compiler's `Compilation.Emit()` public API. Uses `SyntaxTree.ParseObjectText()` for parsing and `CompilationGenerationOptions.All` to enable code generation.

### 2. RoslynRewriter
A `CSharpSyntaxRewriter` that transforms BC runtime types into mock types. Replaces the fragile regex-based approach used in earlier iterations. Handles:
- Type replacements (NavCodeunit → AlScope, NavRecordHandle → MockRecordHandle, etc.)
- Constructor chain bypass
- Method dispatch rewiring

### 3. RoslynCompiler
In-memory Roslyn compilation against BC service tier DLLs + AlRunner.Runtime. No files written to disk.

### 4. Executor
Discovers test codeunits (SubType = Test), resets tables per test, invokes `OnRun()` on each test method, reports pass/fail.

---

## What Works

- Full pipeline: AL source → C# transpilation → Roslyn in-memory compilation → execution
- In-memory record store (MockRecordHandle) with Init, Insert, Modify, Get, field read/write by ID
- Cross-codeunit dispatch (MockCodeunitHandle) routes `Invoke(memberId, args)` to generated methods
- Test auto-detection and execution with pass/fail reporting, table reset per test
- Simple codeunit execution (hello.al, calc.al, greet.al)
- Runs on Linux with kernel32 P/Invoke shim (`NativeLibrary.SetDllImportResolver`)

---

## Key Files

| File | Role |
|------|------|
| `AlRunner/Program.cs` | Main CLI + transpiler + compiler + executor |
| `AlRunner/RoslynRewriter.cs` | Roslyn CSharpSyntaxRewriter (replaced regex-based rewriting) |
| `AlRunner/Runtime/AlScope.cs` | Base scope, AlDialog, AlCompat |
| `AlRunner/Runtime/MockRecordHandle.cs` | In-memory record store |
| `AlRunner/Runtime/MockCodeunitHandle.cs` | Cross-codeunit dispatch |
| `BaseApp/` | Table 50100 + Codeunit 50100 (ApplyDiscount, DoublePrice) |
| `TestApp/` | Test Codeunit 50200 (TestApplyDiscount, TestDoublePrice) |

---

## Key Technical Decisions

**Roslyn SyntaxRewriter over regex:** The initial regex-based C# rewriting was fragile and broke on edge cases. Switching to a proper `CSharpSyntaxRewriter` gives AST-level precision for type and method replacements.

**Path C (real DLLs + session bypass):** Rather than building a custom code generator or mocking the entire BC runtime, we reference real BC DLLs and bypass the NavSession chain using `RuntimeHelpers.GetUninitializedObject()`.

**Linux compatibility:** A compiled C shim provides `kernel32.dll!LCIDToLocaleName` via `NativeLibrary.SetDllImportResolver`. The `NavEnvironment` `TypeInitializationException` from `WindowsIdentity.GetCurrent()` is harmless and ignored.

---

## Known Limitations

- No SETRANGE/SETFILTER filtering
- No CALCFIELDS, TRANSFERFIELDS
- No Report/Page/XMLPort execution
- No event subscriptions
- Tests must be compiled from AL source directories (not from .app files yet)
- Dependency resolution from .app files not yet implemented

---

## Next Steps

1. Support loading dependencies from .app files (extract AL source + symbol references)
2. Version compatibility strategy for multiple BC versions
3. Add SETRANGE/SETFILTER filtering to MockRecordHandle
4. Add Assert codeunit mock
5. Test with real-world app test suites
