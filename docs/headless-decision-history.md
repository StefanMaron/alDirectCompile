# Headless Service Tier — Decision History

Investigated 2026-03-22. Four approaches were evaluated.

## Path A: Standalone Transpiler (ALRunner)
AL → C# → Roslyn rewrite → compile → run. Working (8/9 tests on Recommended Apps).
**Rejected:** Forever chasing runtime parity. Rewriter is 1,674 lines and growing.
Events, triggers, filters, permissions all need reimplementation. For 40,000 tests
would need to reimplement 60-70% of BC runtime. Tests have an asterisk: "results
may differ from real BC."

## Path B: Headless Service Tier (no SQL, in-memory mocks)
Replace SQL with in-memory mocks inside Nav.Ncl.dll via Harmony patches.
**Rejected:** Everything is `internal`. DataProvider, NCLMetadata, DataAccessSource.
NavSession has 200+ fields. 50-80+ patches, many requiring fake implementations of
complex interfaces. Metadata loading from .app packages needs complete reimplementation.

## Path C: Fake SQL Server (TDS protocol)
Leave service tier untouched, implement TDS protocol server.
**Rejected:** No open-source TDS server in .NET. Babelfish (AWS, 25K lines of C)
is the only option. BC uses heavy T-SQL (OUTPUT clauses, table hints, savepoints).
Building a T-SQL engine is building a database. 3-6 months minimum.

## Path D: Real SQL Server + Patched Service Tier (CHOSEN)
Docker SQL Server (zero effort, full compatibility) + real service tier with ~5-10
patches for Linux compatibility.
**Chosen because:** Fewest patches, highest fidelity, version-independent.
Real BC code executes tests — no asterisk on results.

## Key Investigation Findings

### DataProvider (Nav.Ncl.dll)
- `internal abstract class` with ~13 abstract methods
- `TempTableDataProvider` (~1,052 lines) already exists as in-memory implementation
- `DataAccessSource.CreateTenantDataProvider()` is `protected virtual` — overridable
- `NavSession` constructor accepts `Func<NavSession, DataAccessSource>` — DI hook
- 85 virtual table providers + 10+ direct SQL bypass paths

### NavSession
- 200+ fields, massive constructor chain
- Needs NavTenant → NavDatabase → SQL connection
- Even TempTableDataProvider needs NavDatabase.CollationAwareStringComparer
- 20-30 minimum patches for standalone operation

### Service Tier Startup (~40 patches without SQL)
- 25 "easy" no-op patches (telemetry, perf counters, heartbeat)
- 10 "medium" patches (auth bypass, license, connection mocks)
- 5+ "impossible without rebuilding" (metadata loading, DataProvider replacement)

### With Docker SQL: Only ~5-10 patches needed
- WindowsIdentity/Linux compatibility
- Http.sys → Kestrel
- Auth bypass
- Perf counters/ETW no-op
- Side services no-op

## Microsoft MVP Pitch
"Your service tier runs on .NET 8. With 3-5 changes it could support a lightweight
test mode: expose `DataAccessSource` factory methods, load metadata from .app packages,
add a `--test-mode` flag using TempTableDataProvider. The infrastructure already exists
in your codebase (TempTableDataProvider, the DI hook in NavSession constructor)."
