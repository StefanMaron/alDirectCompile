# ALRunner Progress

## Approach 1: Headless Service Tier (PRIMARY)

**Status: Nearly working — ONE remaining blocker**

### What Works (as of 2026-03-23)
- BC v27.5 service tier boots on Linux in Docker
- 12 startup patches applied via DOTNET_STARTUP_HOOKS
- SQL connected to CRONUS database (Docker SQL Server 2022)
- All BC extensions compiled (Base Application, System Application, ~30 extensions)
- 7 Kestrel API hosts created (ports 18001-18007)

### Patches Applied
1. CustomTranslationResolver — satellite assembly recursion fix (JMP hook)
2. NavEnvironment .cctor — WindowsIdentity bypass (JMP hook + DynamicMethod)
3. Win32 P/Invoke stubs — libwin32_stubs.so (kernel32/user32/advapi32/rpcrt4/httpapi)
4. EventLogWriter — DispatchProxy no-op
5. Geneva ETW exporter — stub DLL (GenevaStub/)
6. System.Drawing.Common — stub via AssemblyLoadContext.Resolving (DrawingStub/)
7. Encryption provider — PassthroughEncryptionProxy + RSA key in DB
8. PerformanceCounter — stub DLL (PerfCounterStub/)
9. Topology proxy — LinuxTopologyProxy for ACL bypass
10. WindowsPrincipal — framework-level stub (WindowsPrincipalStub/)
11. HttpSys→Kestrel — framework-level stub (HttpSysStub/)
12. Microsoft.Data.SqlClient — cross-platform Unix build from NuGet

### Remaining Blocker
- SPN registration NullRef: SpnRegister tries Active Directory registration using SecurityIdentifier from our stub — should be no-op'd

### Next Steps
1. Fix SPN registration blocker (no-op SpnRegister or improve SecurityIdentifier stub)
2. Verify API endpoints are reachable
3. Publish a test .app via dev endpoint
4. Execute tests via management API
5. Build self-contained Docker image (specify artifact URL at startup)
6. Test with v28 artifacts

## Approach 2: Standalone Transpiler (paused)

TESTS: Spike 3/3 | RecommendedApps 8/9 | StatisticalAccounts 0/8 | DataArchive 0/6 | ContosoCoffee 0/2
BLOCKERS: Most tests need System Application codeunits at runtime. Some apps still have Roslyn errors.
DECISIONS: SetRange/SetFilter complete. MockArray<T> replaces NavArray<T>. ALCommit/ALSelectLatestVersion are no-ops.
