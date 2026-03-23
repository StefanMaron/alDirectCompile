# CI Pipeline Progress — BC Headless on GitHub Actions

## Session: 2026-03-23

### What Was Achieved

**1. BC Service Tier Runs on GitHub Actions (GREEN PIPELINE)**

The headless BC service tier now starts, compiles extensions, publishes apps, and
executes AL tests on ubuntu-latest GitHub Actions runners. Full end-to-end proof:

```
5 tests passed on CI:
  ✓ Sample Data Tests PPC::TestGenerateSampleData
  ✓ Sample Data Tests PPC::TestClearAllData
  ✓ Sample Data Tests PPC::TestProcessLargeDataSet
  ✓ Sample Data Tests PPC::TestSampleDataValidation
  ✓ Sample Data Tests PPC::TestSampleLineCalculation
```

Working workflow: `PipelinePerformanceComparison/.github/workflows/linux-full-pipeline.yml`

**2. Root Causes Found and Fixed**

| Bug | Root Cause | Fix |
|-----|-----------|-----|
| FileNotFoundException on CI | `find \| head -1` picked net9.0 SqlClient DLL instead of net8.0 (same size, different content). net9.0 references System.Data.Common v9.0.0.0 which doesn't exist in .NET 8 base image | Explicit net8.0 verification in CI workflow |
| No entrypoint logs visible | bash stdout is pipe-buffered when PID 1 has no TTY. On container crash, buffer is lost. stderr is unbuffered | `exec 1>&2` at top of entrypoint.sh |
| AL compiler picks wrong files | `aldirectcompile/` clone in project dir contains .al files with IDs outside app.json range | `rm -rf aldirectcompile` before `AL compile` |
| Test runner API 404 | Custom API pages served on port 7052 (HttpSysStub maps `/api` → `:7052`), not OData port 7048 | Fixed URL to port 7052 |
| Extension publish ID conflict | TestRunner.app and sample ext both use IDs 50002-50004 | Don't publish separate TestRunner (sample ext already includes it) |
| Watson crash handler | `WatsonReporting.GetRegistryValue` calls Windows registry → NullRef | Patch #13: JMP hook SendReport to no-op |

**3. BCApps System Application Compiles on Linux**

```
Component                   | Files | Time
System Application          | 1264  | ~6s
System Application Test Lib | 169   | ~5s
System Application Test     | 224   | ~10s
TOTAL                       | 1657  | ~21s
```

Key discovery: AL compiler's Cecil type loader crashes with .NET RUNTIME DLLs in
assembly probing paths (type-forwarding chains cause NullRef in
`IsTypeForwardingCircular`). **Fix: use .NET 8 REFERENCE assemblies instead.**
Reference assemblies are stubs with type definitions but no forwarding chains.

BCApps tag `releases/27.5/StrictMode` matches our v27.5 platform artifacts exactly
(the `releases/26.0` branch had codeunit renames that didn't match).

**4. Infrastructure**

- 13 startup patches (was 12, added Watson reporting no-op)
- Persistent `/bc/service` volume for fast restarts (~35s vs ~160s)
- .NET runtime tuning (Server GC, tiered compilation)
- Separate workflow files: `linux-full-pipeline.yml`, `bcapps-system-test.yml`
- `docs/performance-strategy.md` with pitch and optimization ideas

### What Is NOT Yet Working

**1. BCApps Test Execution on CI**

The System Application Test Library and Test extensions fail to PUBLISH to BC via the
dev endpoint (HTTP 422). The dev endpoint triggers server-side recompilation, and BC's
server-side compiler can't resolve standard .NET types (`GenericList1`, `XmlDocument`)
because:

- These are defined in .NET runtime DLLs (System.Collections.dll, System.Private.Xml.dll)
- BC's server-side compiler looks in `/bc/service/Add-Ins/` for probing
- The .NET runtime DLLs use type-forwarding (facade → implementation assembly)
- BC's server-side compiler doesn't follow these forwarding chains from Add-Ins

The management API (which could install pre-compiled apps without recompilation)
returns 404 due to Windows auth requirements.

**2. ~6 min Startup Delay (Win32Exception)**

Every cold start has ~10 retries of a Win32Exception (0x80004005) with 30s backoff.
This is NOT the SPN registration (we patched `AllowToRegisterServicePrincipalName`
to return false). The actual source is unclear — the exception stack trace is empty
in the BC error log format. It happens during extension compilation on startup.
Despite the errors, BC eventually starts successfully.

### Architecture (Current)

```
GitHub Actions Runner (ubuntu-latest, 7GB RAM)
├── Pre-downloads BC artifacts (curl + unzip on host)
├── Builds Docker image (alDirectCompile/StartupHook/Dockerfile)
│   └── Contains: StartupHook.dll + 6 stubs + libwin32_stubs.so + scripts
├── docker compose up -d
│   ├── sql (SQL Server 2022, healthcheck)
│   └── bc (entrypoint.sh)
│       ├── Waits for artifacts (docker compose cp from host)
│       ├── Copies service tier to /bc/service/
│       ├── Patches CustomSettings.config
│       ├── Restores CRONUS, creates user, imports license
│       ├── Starts: DOTNET_STARTUP_HOOKS=... dotnet Microsoft.Dynamics.Nav.Server.dll /console
│       └── Background: publishes TestRunner.app via dev endpoint
├── Workflow: AL compile /project:. → produces .app
├── Workflow: curl POST .app to dev endpoint (publish)
└── Workflow: curl to port 7052 (custom API) → run tests → read logEntries
```

### Port Map (HttpSysStub Kestrel routing)

| Original URL prefix | Kestrel port | Service |
|---------------------|-------------|---------|
| `:7047/InstanceName` | 7047 | SOAP (legacy) |
| `:7048/InstanceName/ODataV4` | 7048 | OData V4 |
| `:7048/InstanceName/api` | 7052 | Custom API pages |
| `:7048/InstanceName/api/webhooks` | 7051 | Webhooks |
| `:7049/InstanceName/dev` | 7049 | Dev endpoint (publish) |
| `:7085/InstanceName/client` | 7085 | Web client |
| `:7086/InstanceName/managementapi` | 7087 | Management API |

### Key Technical Decisions

1. **Reference assemblies for AL compiler probing** — Runtime DLLs crash Cecil loader.
   Ref assemblies provide type definitions without forwarding chains. This ONLY works
   for client-side AL compiler tool, not BC's server-side compiler.

2. **No tmpfs for CI** — Tested tmpfs for `/bc/service` and `/var/opt/mssql/data`.
   Was 1.5 min SLOWER on 7GB runners (pre-allocates RAM, less for OS page cache).

3. **Persistent volume for `/bc/service`** — Named Docker volume survives container
   restarts. First boot: ~160s. Subsequent restarts: ~35s. Essential for iterative
   development locally. Not used in CI (each run is clean).

4. **BCApps tag not branch** — `releases/27.5/StrictMode` tag matches v27.5 artifacts.
   The `releases/26.0` branch has codeunit renames that cause compile errors.

### Constraints

- **No pre-baked artifacts**: Must be version-agnostic (support n, n-1, n-2, all minors)
- **No persistent state between CI runs**: Each run is clean (no test cross-contamination)
- **7GB RAM on GitHub runners**: Must fit SQL + BC + tools in ~7GB
- **Compile-only is out of scope**: Microsoft already supports AL compile on Linux.
  The value-add is the full service tier path (compile + publish + test execution)

### Next Steps (Priority Order)

1. **Solve server-side extension publish for BCApps tests**
   - Option A: Get management API working (needs auth bypass for NTLM/Windows auth)
   - Option B: Direct SQL — insert compiled .app into `$ndo$navapppackage` table
   - Option C: Copy .NET runtime DLLs to Add-Ins AND patch BC's assembly resolver
     to handle type forwarding correctly
   - Option D: Ready-to-run packages — clear installed extensions, boot empty, publish
     pre-compiled apps that don't need server-side recompilation

2. **Reduce the ~6 min Win32Exception startup delay**
   - Identify the actual source (extension compilation? network? something else?)
   - Patch or configure to eliminate the retry loop

3. **Collect timing numbers for the pitch**
   - Linux headless vs Windows container for sample extension
   - Linux headless vs Windows container for BCApps System Application
   - Fill in the numbers table in performance-strategy.md

4. **Clean up debug commits** — Many debug/fix commits from this session.
   Squash into clean commits once everything is stable.
