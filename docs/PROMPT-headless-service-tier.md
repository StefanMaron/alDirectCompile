# Session Prompt — Headless BC Service Tier on Linux

Copy everything below this line into a new session.

---

Read `docs/headless-service-tier.md` first — it has the architecture, feature decisions, and progress.

## Task

Continue patching the BC service tier (v27.5) to run on Linux. The approach: run the real `DynamicsNavServer.Main()` with Docker SQL Server, using `DOTNET_STARTUP_HOOKS` to JMP-patch the methods that crash on Linux.

## Current State

**Patch #1 is done.** `CustomTranslationResolver` recursion fixed. The service tier now crashes with a clean `TypeInitializationException` from `NavEnvironment..cctor()` calling `WindowsIdentity.GetCurrent()`.

**Next:** Patch `NavEnvironment..cctor()` (Patch #2), then work through subsequent blockers one by one.

## How To Work

1. Build: `cd StartupHook && dotnet publish -c Release -o bin/Release/net8.0/publish`
2. Test:
```bash
SERVICE_DIR="artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service"
HOOK="StartupHook/bin/Release/net8.0/publish/StartupHook.dll"
cd "$SERVICE_DIR" && DOTNET_STARTUP_HOOKS="$HOOK" dotnet Microsoft.Dynamics.Nav.Server.dll 2>&1 | head -80
```
3. When it crashes, decompile the crashing method with `ilspycmd`, understand the cause, add a JMP patch to `StartupHook.cs`, rebuild, test again.
4. Repeat until the service tier starts and connects to SQL.

## Technical Constraints

- **No Harmony/MonoMod** — native helper rejected by kernel (executable stack)
- **JMP hooks work on BC methods only** (JIT-compiled). BCL methods are ReadyToRun pre-compiled — `GetFunctionPointer()` returns a pre-stub, patching them has no effect.
- **Decompile with:** `ilspycmd "path/to/Assembly.dll" -t Full.Type.Name`
- **Service tier DLLs:** `artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service/`

## Scope

Pipeline testing only. KEEP: SQL connection, metadata, sessions, app publishing, test execution, events, codeunit dispatch, Dev endpoint, Management API. KILL: all auth, all encryption, reporting, OData, SOAP, client service, MCP, debugger, telemetry, schedulers, blob storage, change tracking — see full list in `docs/headless-service-tier.md`.

## Goal

Service tier starts on Linux, connects to Docker SQL, loads metadata, and can publish .app packages + run AL tests with results identical to production BC.
