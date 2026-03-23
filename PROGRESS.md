# ALRunner Progress
PRIORITY: P1 SetRange/SetFilter (DONE) -> P2 Expand real BC test passes
NEXT: Fix remaining Roslyn errors in near-compiling apps (BaseApp 5 errors, Email-M365 1 error, DataArchive 1 error). Include System Application codeunits to unblock runtime CU deps.
TESTS: Spike 3/3 | RecommendedApps 8/9 | StatisticalAccounts 0/8 (CU deps) | DataArchive 0/6 (CU 600 from SysApp) | ContosoCoffee 0/2 (CU 5193)
BLOCKERS: 1. Most tests need System Application codeunits at runtime 2. NavArray->MockArray done but some apps still have Roslyn errors 3. Some test suites need BaseApp Test library codeunits (130500, 131001)
DECISIONS: SetRange/SetFilter complete. MockArray<T> replaces all NavArray<T>. ALCommit/ALSelectLatestVersion are no-ops. NavMedia support added.
