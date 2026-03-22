# AL/BC Language Feature Compatibility Matrix

**Last updated:** 2026-03-21
**Scope:** AlRunner standalone test runner (spike stage)
**Reference test app:** Bank Deposits Tests (73 methods, 10K+ lines of generated C#)

## Summary

| Status | Count | Description |
|--------|-------|-------------|
| **Mocked** | 18 | Working mock implementation provided |
| **Removed** | 14 | Stripped by RoslynRewriter (safe for unit tests) |
| **Passed through** | 9 | Uses real BC runtime type from Nav.Types.dll / Nav.Runtime.dll |
| **Stub** | 5 | Exists but does nothing meaningful |
| **Not handled** | 27 | Will cause compilation or runtime error |
| **Total** | 73 | |

---

## 1. Type System

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| Decimal18 | `Decimal18` (struct), arithmetic operators | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavCode | `new NavCode(20, "ITEM1")` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavText | `new NavText(100, "...")`, `NavText.Default(0)` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavBoolean | `NavBoolean.Default` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavInteger | `NavInteger.Default` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavDecimal | `NavDecimal.Default` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavValue | Base type for field values | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavOption | `NavOption.Create(metadata, ordinal)`, `CreateInstance()` | Passed through | Loaded from Nav.Types.dll; ~217 occurrences in real tests | Compilation error |
| NavTextConstant | `new NavTextConstant(int[], string[], ...)` | Passed through | Loaded from Nav.Types.dll; used for error/label text | Compilation error |
| ByRef\<T\> | `ByRef<bool>`, `ByRef<Decimal18>` | Not handled | BC runtime type for VAR parameters; ~46 occurrences | Compilation error; needed for handler functions and cross-codeunit VAR params |
| NavArray | `new NavArray<INavRecordHandle>(factory, N)` | Not handled | BC runtime type for array variables; ~16 occurrences | Compilation error; used for multi-record locals |
| NavType (enum) | `NavType.Decimal`, `NavType.Code`, etc. | Passed through | Loaded from Nav.Types.dll; used in field access calls | Compilation error |
| DataError (enum) | `DataError.ThrowError` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavDate | `NavDate.Create(...)` | Passed through | Loaded from Nav.Types.dll | Compilation error |
| NavRecordToVariant | Wraps record as variant | Not handled | BC runtime type; present in real tests | Compilation error |
| NavValueToVariant | Wraps NavValue as variant | Not handled | BC runtime type; present in real tests | Compilation error |
| FormResult (enum) | `(FormResult)1` | Not handled | Used in `GetBuiltInAction()` calls for test pages | Compilation error |

## 2. Record Operations

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| Init | `rec.ALInit()` | Mocked | Clears field dictionary | None |
| Insert | `rec.ALInsert(DataError, bool)` | Mocked | Adds row to in-memory table store | None |
| Modify | `rec.ALModify(DataError, bool)` | Mocked | Updates row by PK (field 1) match | None |
| Delete | `rec.ALDelete(DataError, bool)` | Mocked | Removes row by PK match | None |
| DeleteAll | `rec.ALDeleteAll(...)` | Mocked | Clears table | None |
| Get | `rec.ALGet(DataError, keyValues...)` | Mocked | Finds row by PK | None |
| Find | `rec.ALFind(DataError, searchMethod)` | Mocked | Positions cursor at first row | None |
| Next | `rec.ALNext()` | Mocked | Advances cursor, returns 0 at end | None |
| SetRange | `rec.ALSetRange(fieldNo, type, from, to)` / `ALSetRangeSafe(...)` | Stub | Accepted but does not filter | Tests pass but operate on unfiltered data; wrong results possible |
| Reset | `rec.ALReset()` | Mocked | Clears fields and cursor | None |
| Count | `rec.ALCount()` | Mocked | Returns row count for table | None |
| IsEmpty | `rec.ALIsEmpty()` | Mocked | Returns whether table has rows | None |
| SetFieldValueSafe | `rec.SetFieldValueSafe(fieldNo, type, value)` | Mocked | Stores value by field number | None |
| GetFieldValueSafe | `rec.GetFieldValueSafe(fieldNo, type)` | Mocked | Returns value with type-appropriate default | None |
| GetFieldRefSafe | `rec.GetFieldRefSafe(fieldNo, type)` | Mocked | Alias for GetFieldValueSafe (for formatting) | None |
| FindFirst | `rec.ALFindFirst(DataError)` | Not handled | ~40 occurrences in real tests | Runtime exception (method not found) |
| FindLast | `rec.ALFindLast(DataError)` | Not handled | Present in real tests | Runtime exception |
| FindSet | `rec.ALFindSet(DataError)` | Not handled | ~40 occurrences total | Runtime exception; needed for record iteration loops |
| Last | `rec.ALLast(DataError)` | Not handled | Present in real tests | Runtime exception |
| SetFilter | `rec.ALSetFilter(fieldNo, type, filter, args...)` | Not handled | ~13 occurrences; expression-based filtering | Runtime exception |
| SetRecFilter | `rec.ALSetRecFilter()` | Not handled | Sets filter from primary key | Runtime exception |
| Validate | `rec.ALValidateSafe(fieldNo, type, value)` | Not handled | ~41 occurrences; triggers field validation logic | Runtime exception; critical for tests that verify validation behavior |
| TestField | `rec.ALTestFieldSafe(fieldNo, type, value)` / `ALTestFieldNavValueSafe(...)` | Not handled | ~20 occurrences; asserts field equals expected value | Runtime exception; tests that verify field values will fail |
| CalcFields | `rec.ALCalcFields(fieldNos...)` | Not handled | ~8 occurrences; computed/flowfield calculation | Runtime exception |
| CalcSums | `rec.ALCalcSums(fieldNos...)` | Not handled | Present in real tests | Runtime exception |
| FieldCaption | `rec.ALFieldCaption(fieldNo)` | Not handled | ~7 occurrences; returns field display name | Runtime exception |
| FieldNo | `rec.ALFieldNo(name)` | Not handled | Returns field number by name | Runtime exception |
| TableCaption | `rec.ALTableCaption()` | Not handled | Returns table display name | Runtime exception |
| TransferFields | `rec.ALTransferFields(sourceRec)` | Not handled | Copies matching fields between records | Runtime exception |
| Copy | `rec.ALCopy(sourceRec)` | Not handled | Copies record including filters | Runtime exception |
| Rename | `rec.ALRename(keyValues...)` | Not handled | Changes primary key | Runtime exception |
| SetLoadFields | `rec.ALSetLoadFields(fieldNos...)` | Not handled | Performance hint - load specific fields only | Runtime exception; could be stubbed as no-op |
| SetAutoCalcFields | `rec.ALSetAutoCalcFields(fieldNos...)` | Not handled | Auto-calculate flowfields on read | Runtime exception; could be stubbed as no-op |
| GoToRecord | `rec.ALGoToRecord(targetRec)` | Not handled | Positions to specific record | Runtime exception |
| Close | `rec.ALClose()` | Not handled | Record lifecycle cleanup | Runtime exception; safe to stub as no-op |
| AddLink | `rec.ALAddLink(url)` | Not handled | Record link management | Runtime exception; safe to stub as no-op |
| HasLinks | `rec.ALHasLinks()` | Not handled | Check if record has links | Runtime exception; safe to stub returning false |
| NavRecordHandle.Factory2 | `new NavRecordHandle.Factory2(this, tableId, false, SecurityFiltering)` | Not handled | Array factory for record variables; 6 occurrences | Compilation error |

## 3. Codeunit Operations

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| Cross-codeunit Invoke | `handle.Target.Invoke(memberId, args[])` | Mocked | MockCodeunitHandle finds method via reflection on scope class names | None for compiled codeunits |
| Codeunit constructor | `new NavCodeunitHandle(this, codeunitId)` | Mocked | Rewritten to `MockCodeunitHandle.Create(codeunitId)` | None |
| NavCodeunit.RunCodeunit | `NavCodeunit.RunCodeunit(DataError, codeunitId, rec)` | Not handled | 2 occurrences; runs a codeunit with a record parameter | Runtime exception; used for posting routines |
| Handle.Clear() | `handle.Clear()` | Not handled | Called in OnClear; resets codeunit handle state | Runtime exception on every test codeunit teardown |
| base.Parent.Method() | `base.Parent.MethodName(args)` | Not handled | ~767 occurrences; scope calling back to parent codeunit methods | Compilation error; `base.Parent` does not exist after removing NavMethodScope base |
| InitializeComponent | `this.InitializeComponent()` | Not handled | Called from constructor; initializes codeunit handles | Works if constructor is not removed; but constructor removal may break it |

## 4. Test Framework

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| [NavTest] attribute | `[NavTest("Name", TestMethodNo=N, TestPermissions=X)]` | Removed | Stripped by RoslynRewriter | None; test discovery uses other means |
| TestPermissions | `NavTestPermissions.Restrictive` etc. in NavTest attribute | Removed | Stripped with attribute | Tests always run without permission checks |
| Assert codeunit | `assert.Target.Invoke(memberId, args)` | Not handled | Assert (CU 130000) is an external library codeunit | Runtime exception; no assertion checking. Tests cannot verify expected values |
| [HandlerFunctions] | Not directly in C#; encoded in NavTest attribute | Not handled | No handler routing infrastructure exists | Modal dialogs, confirms, messages not routed to handler methods; runtime exception |
| ConfirmHandler | `[NavHandler(NavHandlerType.Confirm)]` + method with `ByRef<bool>` | Not handled | Handler method exists but never called | Confirm dialogs not handled; test hangs or crashes |
| MessageHandler | `[NavHandler(NavHandlerType.Message)]` | Not handled | Handler method exists but never called | Message calls go to console instead of handler |
| ModalPageHandler | `[NavHandler(NavHandlerType.ModalPage)]` with NavTestPageHandle param | Not handled | Handler method exists but never called | Modal page operations not intercepted |
| PageHandler | `[NavHandler(NavHandlerType.Page)]` | Not handled | Present in real tests | Page operations not intercepted |
| RequestPageHandler | `[NavHandler(NavHandlerType.RequestPage)]` with NavReportHandle param | Not handled | Present in real tests (report filtering) | Report request pages not intercepted |
| HyperlinkHandler | `[NavHandler(NavHandlerType.Hyperlink)]` | Not handled | Not seen in Bank Deposits but exists in BC test framework | Hyperlink operations not intercepted |

## 5. Events

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| [NavEvent] attribute | `[NavEvent(NavEventType.Integration, false, false)]` | Not handled | 6 occurrences; needs to be removed by rewriter | Compilation error (unknown attribute) |
| NavEventMethodScope\<T\> | Scope class for event subscriber methods | Not handled | Needs rewriting to `: AlScope`; 6 occurrences | Compilation error (base class not found) |
| NavEventScope | `public static NavEventScope eventScope` field | Not handled | BC runtime type for event binding | Compilation error |
| Event binding/dispatch | Runtime event subscription registration | Not handled | No event infrastructure exists | Event subscribers never fire; tests that depend on event-driven behavior fail silently |

## 6. UI Objects

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| NavTestPageHandle | `new NavTestPageHandle(this, pageId)` | Not handled | 154 occurrences; core to BC test methodology | Compilation error; most real-world tests cannot compile |
| TestPage.GetField | `page.GetField(fieldId).ALSetValue(session, value)` | Not handled | Field-level page interaction | Compilation error |
| TestPage.GetPart | `page.GetPart(partId)` for subpage access | Not handled | Subpage navigation | Compilation error |
| TestPage.GetAction | `page.GetAction(actionId).ALInvoke()` | Not handled | Action invocation on test pages | Compilation error |
| TestPage.GetBuiltInAction | `page.GetBuiltInAction((FormResult)N).ALInvoke()` | Not handled | Built-in action (OK, Cancel, etc.) | Compilation error |
| TestPage.ALTrap | `page.ALTrap()` | Not handled | Intercepts next page open | Compilation error |
| TestPage.ALClose | `page.ALClose()` | Not handled | Closes test page | Compilation error |
| NavForm.Run | `NavForm.Run(pageId, record)` | Not handled | 8 occurrences; opens a page with a record | Runtime exception |
| NavReportHandle | `new NavReportHandle(this, reportId)` | Not handled | 8 occurrences; report execution | Compilation error |
| Report.GetDataItem | `report.GetDataItem(itemId)` | Not handled | Report data item access | Compilation error |
| Report.ALSaveAsXml | `report.ALSaveAsXml(path)` | Not handled | Save report output | Runtime exception |

## 7. Dialog / Message

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| Message() | `NavDialog.ALMessage(this.Session, guid, fmt, args)` | Mocked | Rewritten to `AlDialog.Message(fmt, args)` -> Console.WriteLine | None; messages print to console |
| Error() | `NavDialog.ALError(this.Session, guid, fmt, args)` | Mocked | Rewritten to `AlDialog.Error(fmt, args)` -> throws Exception | None; errors throw as expected |
| Confirm() | `NavDialog.ALConfirm(this.Session, ...)` with `ByRef<bool>` | Not handled | Not yet rewritten; needs handler routing | Runtime exception; confirm dialogs crash |
| StrMenu() | `NavDialog.ALStrMenu(...)` | Not handled | Not seen in Bank Deposits but exists in AL | Runtime exception if encountered |

## 8. Code Coverage

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| StmtHit | `StmtHit(N);` as statement | Removed | RoslynRewriter removes entire statement | None |
| CStmtHit | `CStmtHit(N)` as expression (in `if` conditions) | Stub | Replaced with `true` literal | None; condition always evaluates based on actual test logic |

## 9. Compiler Artifacts

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| NavCodeunitOptions | `[NavCodeunitOptions(0, 0, SubType, false)]` | Removed | Stripped by RoslynRewriter | None |
| NavFunctionVisibility | `[NavFunctionVisibility(FunctionVisibility.External)]` | Removed | Stripped by RoslynRewriter | None |
| NavCaption | `[NavCaption(TranslationKey = "...")]` | Removed | Stripped by RoslynRewriter | None |
| NavName | `[NavName("...")]` | Removed | Stripped by RoslynRewriter | None |
| SignatureSpan | `[SignatureSpan(N)]` | Removed | Stripped by RoslynRewriter | None |
| SourceSpans | `[SourceSpans(N, N, ...)]` | Removed | Stripped by RoslynRewriter | None |
| ReturnValue | `[ReturnValue]` | Removed | Stripped by RoslynRewriter | None |
| NavObjectId | `[NavObjectId(ObjectId = N)]` | Removed | Stripped by RoslynRewriter | None |
| NavByReferenceAttribute | `[NavByReferenceAttribute]` | Removed | Stripped by RoslynRewriter | None |
| NavCodeunit base class | `: NavCodeunit` | Removed | Stripped from base list by RoslynRewriter | None |
| NavTestCodeunit base class | `: NavTestCodeunit` | Removed | Stripped from base list by RoslynRewriter | None |
| NavRecord base class | `: NavRecord` | Removed | Stripped from base list by RoslynRewriter | None |
| NavMethodScope\<T\> | Scope class inheritance | Mocked | Rewritten to `: AlScope` | None |
| NavTriggerMethodScope\<T\> | Trigger scope inheritance | Mocked | Rewritten to `: AlScope` | None |
| ITreeObject constructor | `public CU(ITreeObject parent) : base(parent, N)` | Removed | Constructor removed by RoslynRewriter | None |
| __Construct | `public static X __Construct(ITreeObject ...)` | Removed | Method removed by RoslynRewriter | None |
| OnInvoke | `protected override object OnInvoke(int, object[])` | Removed | Method removed by RoslynRewriter | None |
| OnRun (parameterized) | `protected override void OnRun(params)` | Removed | Removed (parameterized version only) | None |
| ObjectName property | `public override string ObjectName => "..."` | Removed | Property removed by RoslynRewriter | None |
| IsCompiledForOnPremise | `public override bool IsCompiledForOnPremise => true` | Removed | Property removed by RoslynRewriter | None |
| Rec / xRec properties | `private RecordX Rec => ...` | Removed | Property removed by RoslynRewriter | None |
| RawScopeId property | `protected override uint RawScopeId { get; set; }` | Removed | Property removed by RoslynRewriter | None |
| alpha-scopeId field | `public static uint \u03b1scopeId` | Removed | Field removed by RoslynRewriter | None |
| beta-parent parameter | Constructor param `Codeunit50100 \u03b2parent` | Removed | Parameter removed; `base(parent)` initializer removed | None |
| "this" in scope constructor call | `new Scope(this, ...)` | Mocked | First `this` arg removed from scope constructor calls | None |
| NavRuntimeHelpers.CompilationError | `NavRuntimeHelpers.CompilationError(...)` | Mocked | Rewritten to `throw new InvalidOperationException(...)` | None |
| ALCompiler.ToNavValue | `ALCompiler.ToNavValue(x)` | Mocked | Rewritten to `AlCompat.ToNavValue(x)` (when no records) | None for simple codeunits; uses real ALCompiler when records present |
| ALCompiler.ObjectToDecimal | `ALCompiler.ObjectToDecimal(x)` | Mocked | Rewritten to `AlCompat.ObjectToDecimal(x)` (when no records) | None |
| ALCompiler.ObjectToExactINavRecordHandle | `ALCompiler.ObjectToExactINavRecordHandle(x)` | Mocked | Rewritten to `(MockRecordHandle)(x)` cast | None |
| INavRecordHandle type | Variable/field type | Mocked | Rewritten to `MockRecordHandle` | None |
| NavRecordHandle type | `new NavRecordHandle(this, tableId, ...)` | Mocked | Rewritten to `new MockRecordHandle(tableId)` | None |
| NavCodeunitHandle type | `new NavCodeunitHandle(this, cuId)` | Mocked | Rewritten to `MockCodeunitHandle.Create(cuId)` | None |
| .Target. dereference | `handle.Target.Method()` | Mocked | `.Target.` removed by RoslynRewriter for known methods | None |
| Using directives | BC-specific namespaces | Removed | 4 using directives stripped; `AlRunner.Runtime` injected | None |
| NavHandler attribute | `[NavHandler(NavHandlerType.X)]` | Not handled | Not yet removed by rewriter; 20 occurrences | Compilation error (unknown attribute) |
| NavTestPermissions attribute | `[NavTestPermissions(X)]` | Not handled | Not yet removed by rewriter | Compilation error |

## 10. Database

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| ALDatabase.ALCommit | `ALDatabase.ALCommit()` | Not handled | ~16 occurrences across real tests | Runtime exception; could be stubbed as no-op for unit tests |
| ALDatabase.ALSelectLatestVersion | `ALDatabase.ALSelectLatestVersion()` | Not handled | Present in real tests | Runtime exception; safe to stub as no-op |
| Transaction handling | Implicit in BC runtime | Not handled | No transaction infrastructure | Record operations are not transactional; rollback not possible |

## 11. Security

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| SecurityFiltering | `SecurityFiltering.Validated` in NavRecordHandle constructor | Removed | Argument stripped during constructor rewrite | Tests always run without security filtering |
| TestPermissions | `NavTestPermissions.Restrictive` in NavTest attribute | Removed | Attribute stripped | Tests always pass regardless of permissions |

## 12. String / Format

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| AL format placeholders | `%1`, `%2`, ... in format strings | Mocked | AlDialog.ConvertAlFormat converts `%N` to `{N-1}` | None |
| NavFormatEvaluateHelper.Format | `NavFormatEvaluateHelper.Format(value, ...)` | Not handled | 24 occurrences; culture-aware formatting | Runtime exception; needed for value display/comparison |
| ALCompiler.ToNavValue (with records) | `ALCompiler.ToNavValue(x)` when records are present | Passed through | Uses real ALCompiler (requires NavSession) | Runtime exception if NavSession not initialized |
| ALCompiler.ObjectToExactNavValue\<T\> | `ALCompiler.ObjectToExactNavValue<NavTestPageHandle>(x)` | Not handled | Generic type conversion for handler arguments | Compilation error if target type not available |

## 13. Other Runtime

| Feature | Generated C# Pattern | Status | How | Impact if missing |
|---------|----------------------|--------|-----|-------------------|
| this.Session | `this.Session` passed to many methods | Not handled | ~97 occurrences; NavSession reference from base class | Compilation error; Session property does not exist after base class removal |
| OnClear | `protected override void OnClear()` | Not handled | Called on codeunit reset; resets all fields and handles | Compilation error (no override target after base removal) |
| Error handling (asserterror) | `try { ... } catch (NavCSideException)` pattern | Not handled | AL `asserterror` compiles to try/catch | Runtime issue if NavCSideException type not available |
| Scope.Run() | `scope.Run()` calls `OnRun()` | Mocked | AlScope provides Run() -> OnRun() dispatch | None |
| Scope.Dispose() | `using (scope) { scope.Run(); }` | Mocked | AlScope.Dispose() is a no-op | None |
| MockRecordHandle.ResetAll() | Called between test runs | Mocked | Clears all in-memory table data | None |

---

## Critical Path for Real-World Test Execution

The following items block compilation of real MS test apps (ordered by impact):

1. **base.Parent.Method()** -- 767 occurrences; scope classes call parent codeunit methods. Current approach of removing NavMethodScope breaks this entirely.
2. **this.Session** -- 97 occurrences; passed to page/report/format methods. Needs stub or removal.
3. **NavTestPageHandle** -- 154 occurrences; test page interaction is core to BC testing.
4. **NavHandler / NavEvent attributes** -- 26 occurrences; must be removed or compilation fails.
5. **NavEventMethodScope\<T\>** -- 6 occurrences; must be rewritten to AlScope.
6. **ByRef\<T\>** -- 46 occurrences; needed for handler functions and VAR parameters.
7. **OnClear override** -- Present in every test codeunit; no override target after base removal.
8. **Assert codeunit** -- External dependency; tests cannot verify anything without it.
