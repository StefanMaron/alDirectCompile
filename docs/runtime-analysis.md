# BC Service Tier Runtime Analysis

## Investigation Summary

Decompiled and traced the full dependency chain of the BC service tier DLLs
(`Microsoft.Dynamics.Nav.Ncl.dll` and dependencies) to determine where the
infrastructure boundary sits and whether we can reference real MS assemblies
while only mocking the storage layer.

**Conclusion: The runtime is too entangled with NavSession/NavEnvironment to
use directly. A shim assembly approach is needed.**

---

## Assembly Map

| Assembly | Contents | Usable Standalone? |
|---|---|---|
| `Nav.Types.dll` | NavValue, NavText, NavCode, NavDecimal, NavInteger, NavBoolean, Decimal18, NavGuid, NavBigInteger, NavType, DataError, NavOption, NCLOptionMetadata, NavRecordId | **YES** — pure value types, no infrastructure deps |
| `Nav.Common.dll` | Language/culture utilities | **YES** — utility code |
| `Nav.Language.dll` | ALSystemString (StrSubstNo, etc.) | **YES** — string operations work standalone |
| `Nav.Core.dll` | NavFormatEvaluateHelper, some shared types | **PARTIAL** — Format() needs session for culture |
| `Nav.Ncl.dll` | NavRecord, NavCodeunit, NavRecordHandle, NavSession, NavEnvironment, ALCompiler, NavDialog, DataAccess, DataProvider, TreeHandler, NavMethodScope | **NO** — deeply entangled with NavSession |
| `Nav.Types.Report*.dll` | Report runtime types | **YES** as metadata references |

## Critical Type Analysis

### NavEnvironment (Nav.Ncl.dll)
**Blocker:** Static initializer calls `WindowsIdentity.GetCurrent()` which crashes on Linux.
- Singleton pattern: `NavEnvironment.Instance`
- Referenced by: `NavGlobal.SystemTenant`, `NavGlobal.MetadataProvider`
- Triggered by: `NavTextConstant` ctor, `NCLEnumMetadata.Create()`, `ALCompiler.ToNavValue()`, various session-dependent methods

### NavSession (Nav.Ncl.dll)
**Blocker:** Requires NavTenant, NavDatabase, NavEnvironment, license, permissions, diagnostics.
- 200+ fields, massive constructor chain
- Every NavApplicationObjectBase stores a NavSession reference
- Every TreeHandler stores a NavSession reference
- Used for: transactions, metadata lookup, permission checks, culture/formatting

### ITreeObject / TreeHandler (Nav.Ncl.dll)
```csharp
public interface ITreeObject {
    TreeHandler Tree { get; }
}
```
- TreeHandler requires NavSession in its constructor
- Forms parent-child tree for object lifecycle management
- Every NavCodeunit, NavRecord, NavRecordHandle extends this

### NavCodeunit (Nav.Ncl.dll)
```
NavCodeunit : NavApplicationObjectBase : NavComplexValue : NavValue, ITreeObject
```
- Constructor: `NavCodeunit(ITreeObject parent, int objectId)`
- Requires ITreeObject parent → TreeHandler → NavSession
- DoRunAsync uses: session.BeginTransaction(), session.EndTransaction(), session.NCLMetadata, session.DataAccessSource
- Properties like ObjectName, MetaCodeunit access session

### NavRecordHandle (Nav.Ncl.dll)
```
NavRecordHandle : NavApplicationObjectBaseHandle<NavRecord> : INavRecordHandle
```
- Constructor: `NavRecordHandle(ITreeObject parent, int id, bool temporary, SecurityFiltering securityFiltering)`
- Creates NavRecord via `NavGlobal.NCLMetadata.GetMetaTableById()` → NavEnvironment
- NavRecord.Target → RecordImplementation → DataAccess → DataProvider → SQL

### NavRecord (Nav.Ncl.dll)
```
NavRecord : NavApplicationObjectBase, INavRecordHandle
```
- All data operations delegate to internal `RecordImplementation` field
- RecordImplementation → DataAccess → DataProvider (abstract, internal)
- DataProvider implementations: SqlTableDataProvider (SQL Server), VirtualDataProvider (system tables)
- Fields stored as `MutableRecordBuffer` → `ReadOnlyRecordBuffer` → `NavValue[]`
- Filter engine: `FiltersAndMarks`, `TableState`, complex filter evaluation

### DataProvider (Nav.Ncl.dll) — The Storage Boundary
```csharp
internal abstract class DataProvider {
    abstract ValueTask<ReadOnlyRecordBufferResult<bool>> TryGetByPrimaryKeyAsync(RecordIdProviderRequest);
    abstract ValueTask<IAsyncEnumerable<ReadOnlyRecordBuffer>> FindAsync(FindProviderRequest);
    abstract ValueTask<bool> ExistsAsync(ExistsProviderRequest);
    abstract ValueTask<int> CountAsync(DataProviderRequest);
    abstract ValueTask<ReadOnlyRecordBufferResult<InsertResult>> InsertAsync(...);
    abstract ValueTask<ReadOnlyRecordBufferResult<ModifyResult>> ModifyAsync(...);
    abstract ValueTask<DeleteResult> DeleteAsync(...);
    abstract ValueTask DeleteAllAsync(...);
}
```
- **This IS the storage boundary** — abstract class with SQL implementation
- **BUT:** It's `internal`, not `public`. Cannot be implemented from outside the assembly.
- Request types (FindProviderRequest, etc.) are also internal
- All methods are async (ValueTask-based)

### NavMethodScope (Nav.Ncl.dll)
```
NavMethodScope : NavScope
```
- Requires NavSession for construction
- Manages statement tracking, debugger integration, permissions
- Has CancellationToken, error handling, event subscription support

### ALCompiler (Nav.Ncl.dll)
```csharp
public static class ALCompiler
```
- Static methods for type conversion
- Some methods (ObjectToDecimal, CompareNavValues) work without session
- Others (ConvertObjectToClrValueOfType) use `NavCurrentThread.Session.WindowsCulture`
- ToNavValue chains through NavValueFormatter → NavSession → NavEnvironment

## Dependency Chain Summary

```
Transpiled C# code
    references → NavRecordHandle, NavCodeunit, NavMethodScope<T>
    which need → ITreeObject (parent parameter)
    which need → TreeHandler
    which need → NavSession
    which need → NavTenant, NavDatabase, NavEnvironment
    which need → SQL Server, Windows Identity, License, ...
```

Every single BC runtime type ultimately chains back to NavSession/NavEnvironment.
There is no clean seam to inject a mock.

## Architecture Decision: Shim Assembly Approach

### What Works Without Changes
- `Nav.Types.dll`: All NavValue types (NavText, NavCode, Decimal18, NavInteger, NavBoolean, NavGuid, etc.)
- `Nav.Types.dll`: NavType enum, DataError enum, SecurityFiltering enum
- `Nav.Types.dll`: NavOption, NCLOptionMetadata
- `Nav.Language.dll`: ALSystemString.ALStrSubstNo (string formatting)
- `Nav.Common.dll`: Culture utilities

### What Must Be Shimmed (Nav.Ncl.dll types)
These types exist in the transpiled C# but cannot be used from the real assembly:

| Type | Used As | Shim Strategy |
|---|---|---|
| `NavCodeunit` | Base class for codeunit classes | Minimal base class with lifecycle |
| `NavTestCodeunit` | Base class for test codeunits | Extends shimmed NavCodeunit |
| `NavRecordHandle` | Record variable (field storage + CRUD) | In-memory store with filter engine |
| `INavRecordHandle` | Interface for record handles | Interface matching NavRecordHandle shim |
| `NavCodeunitHandle` | Cross-codeunit dispatch | Reflection-based dispatch |
| `NavInterfaceHandle` | Interface implementation handle | Simple delegation |
| `NavMethodScope<T>` | Base class for scope (method) classes | Minimal scope with error handling |
| `NavTriggerMethodScope<T>` | Trigger method scopes | Same as NavMethodScope shim |
| `NavEventMethodScope<T>` | Event method scopes | Same as NavMethodScope shim |
| `NavRecord` | Record data access (.Target) | In-memory record with field storage |
| `NavDialog` | Message/Error/Confirm dialogs | Console output + exceptions |
| `ALCompiler` | Static type conversion helpers | Re-implemented without session |
| `NavEnvironment` | Static singleton (avoided) | Not referenced in shim |
| `NavSession` | Session context | Minimal stub or eliminated |
| `ITreeObject` | Parent reference in constructors | Simplified implementation |
| `TreeHandler` | Object lifecycle tree | Simplified without session |
| `ALSystemErrorHandling` | Error text/code/callstack | Static properties |
| `NavRuntimeHelpers` | CompilationError etc. | Exception throwing |
| `NavFormatEvaluateHelper` | Format() | Direct formatting without session |
| `NavRecordRef` | Dynamic record reference | Stub |
| `NavTextConstant` | Text constants (AVOID) | Rewrite to NavText at transpile time |
| `NavVariant` | Variant type | Use object |
| `NCLEnumMetadata` | Enum metadata (AVOID) | Use NCLOptionMetadata.Default |
| `NavEventScope` | Event subscriber scope | Stub |

### Two-Phase Strategy

**Phase 1: Keep Roslyn Rewriting, Improve Systematically**
The current rewriter approach works and has proven results (8/9 tests passing).
Rather than a complete rewrite to shim assemblies, improve the existing approach:
- Make the rewriter more systematic (table-driven rules instead of ad-hoc patterns)
- Cover more transpiled patterns as we expand test coverage
- This is the pragmatic path to 100+ passing tests quickly

**Phase 2: Shim Assembly (Future)**
Once the rewriter patterns stabilize, extract them into a shim assembly:
- Create `AlRunner.NavShim.dll` with matching namespaces/types
- Compile transpiled C# against shim + real Nav.Types.dll
- Eliminate Roslyn rewriting entirely
- This is the clean architecture path, but requires the patterns to be well-understood first

The current Roslyn rewriting is effectively DISCOVERING what the shim assembly needs to contain.
Each rewriter rule maps 1:1 to a method/type that the shim must provide.
