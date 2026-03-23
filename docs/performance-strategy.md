# Performance Strategy: BC Pipeline on Linux

## The Pitch

Microsoft doesn't care about runner costs or licenses — they own the infra.
What they care about is **developer feedback speed**: how fast can a developer
know if their commit broke something? Currently that's ~3 hours (BCApps CICD).

Two concrete improvements:

### 1. Small Apps: Eliminate Pipeline Overhead

**Current state (Windows):** ~20 minutes total for what is essentially a 30-second
compile + test. The overhead is container creation, artifact download, service tier
startup, extension publishing, and teardown.

**Current state (Linux headless):** ~5 minutes overhead. Better, but still 10x the
actual work for small apps.

**Target:** Sub-2-minute overhead. The compile and test should dominate the wall clock.

**Constraints:**
- No pre-baked artifacts — must be version-agnostic (support n, n-1, n-2, all minors)
- No persistent state between runs — each run must be clean (no test cross-contamination)
- Artifact download + DB restore on every run is acceptable

**Ideas to investigate:**

- **Patch out SPN registration.** The Win32Exception retry loop adds ~5 minutes to
  every startup. This is the single biggest overhead item. Patching it via JMP hook
  or stubbing the P/Invoke (`NCL_SpnRegister` in nclcsrts.dll, `DsWriteAccountSpn`
  in ntdsapi.dll) would cut startup from ~6 min to ~1 min.

- **Ready-to-run packages (see section below).** Clear installed extensions, boot
  service tier empty, then deploy only what's needed. Ready-to-run packages publish
  instantly. Could eliminate most of the extension compilation time on cold start.

- **Parallel test sharding.** For apps with many tests, split across N containers.
  Each container runs a subset. Merge results. Linear speedup.

- **Health check endpoint instead of polling.** Instead of polling for "Press Enter",
  use a health check endpoint and start tests immediately when ready.

### 2. Large Apps: Raw Execution Performance

**Target audience:** Microsoft's own BCApps — System Application and Base Application
with 40,000+ test cases.

**Current state:** Compilation and test execution on Windows is slow. The question is
whether Linux gives a raw performance advantage.

**Ideas to investigate:**

- **Measure the baseline.** Run the BCApps System Application test suite on both
  Windows (standard BC container) and Linux (headless). Compare:
  - Time to compile System App + Base App
  - Time to execute the full test suite
  - Memory usage, CPU utilization
  This gives us hard numbers for the pitch.

- **Linux I/O advantage.** ext4/xfs on Linux typically has faster file operations than
  NTFS on Windows. AL compilation is I/O-heavy (reading hundreds of .al files, writing
  .app output). This alone may yield measurable gains.

- **No Windows overhead.** The headless service tier runs directly on .NET 8 without
  Windows services, event log, WMI, or other Windows subsystem overhead. Less OS
  noise = more CPU for actual work.

- **SQL Server on Linux.** SQL Server 2022 on Linux has comparable or better performance
  to Windows for many workloads. The CRONUS database operations (reads during test
  execution) may benefit.

- **Container density.** Linux containers are lighter than Windows containers. On the
  same hardware, you can run more parallel test containers. For large test suites that
  can be sharded, this means faster total wall clock time.

- **Compiler optimization.** The AL compiler runs as a .NET tool. On Linux, the JIT
  may behave differently (different ReadyToRun images, different GC tuning). Worth
  profiling to see if there are easy wins.

- **Memory-mapped compilation.** For very large codebases, the compiler's memory usage
  may be a bottleneck. Linux's memory management (huge pages, better swap behavior)
  could help.

## Measurement Plan

1. **Baseline (done):** BC v27.5 starts on Linux, compiles sample extension, runs tests.
2. **Next:** Run BCApps System Application tests on Linux headless. Compare with Windows.
3. **Optimize:** Profile the slow parts. Is it compilation? Test execution? DB I/O?
4. **Warm image:** Build a pre-baked Docker image. Measure cold vs warm start times.
5. **Sharding:** Test parallel containers with split test suites.

## Numbers to Collect

| Metric | Windows | Linux (cold) | Linux (warm) |
|--------|---------|-------------|-------------|
| Pipeline overhead (small app) | ~20 min | ~5 min | target: <2 min |
| Compile sample extension | ? | ? | ? |
| Run 3 sample tests | ? | ? | ? |
| Compile System App | ? | ? | ? |
| Run System App tests (40k) | ? | ? | ? |
| Compile Base App | ? | ? | ? |
| Container start → ready | ? | ? | ? |

## Ready-to-Run Packages (from Microsoft insider tip)

BC ships some extensions as "ready-to-run" packages — the .app file already contains
the compiled DLLs (C# → IL). On a cold startup, BC normally compiles ALL installed
extensions from AL → C# → DLL, which is a huge time sink.

**Optimization approach:**
1. Clear the `$ndo$installedapp` table (backup first) so BC starts with zero extensions
2. BC boots instantly (no extensions to compile)
3. Deploy only the extensions we need via the dev/management endpoint
4. Extensions that are ready-to-run packages publish almost instantly (DLLs already built)
5. Only our test app needs actual compilation

This could dramatically reduce cold start time, especially for large app stacks where
Base App + System App compilation dominates the startup.

**Investigation needed:**
- Which extensions are available as ready-to-run? (System App, Base App?)
- Does the dev endpoint accept ready-to-run packages?
- Can we pre-populate the installed apps table with just the ready-to-run entries?
- What's the startup time with zero extensions vs full app stack?

## SPN Blocker

The Win32Exception during startup is the SPN (Service Principal Name) registration
attempt. BC tries to register an SPN with Active Directory, which doesn't exist in
the Linux container. It retries ~10 times with ~30s backoff before giving up and
continuing. This adds ~5 minutes to every cold start.

**Fix options:**
- Find the SPN registration code and patch it out via JMP hook
- Set a config flag that disables SPN registration
- The retry/timeout behavior may be configurable in CustomSettings.config
