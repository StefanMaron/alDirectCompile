# GitHub Copilot Integration for Business Central

## The Problem

BC is the only major Microsoft development platform that can't run in Codespaces
or be validated by Copilot. The entire modern GitHub developer experience —
dev containers, Codespaces, Copilot coding agents — is Linux-only. BC requires
Windows. This locks BC developers out of the tooling every other Microsoft
ecosystem already has.

GitHub Copilot can suggest AL code today, but it has zero ability to validate it.
It can't compile, can't run tests, can't verify that a suggested change actually
works in Business Central.

## The Solution

A self-contained Linux Docker image that runs the real BC service tier. This image
slots directly into GitHub's existing infrastructure as a sidecar service for
Copilot coding agent sessions.

### How It Works

Two files in any BC repository enable Copilot-powered BC validation:

**1. `.github/workflows/copilot-setup-steps.yml`** — spins up a BC sidecar:

```yaml
name: "Copilot Setup Steps"
on: [workflow_dispatch, push, pull_request]
jobs:
  copilot-setup-steps:
    runs-on: ubuntu-latest
    services:
      bc-server:
        image: ghcr.io/nesst-global/bc-linux:27.5
        ports:
          - 7049:7049
          - 7048:7048
          - 7047:7047
    steps:
      - uses: actions/checkout@v5
```

**2. `.github/copilot-instructions.md`** — tells Copilot how to use BC:

```markdown
## Business Central Test Environment

A live BC service tier is running as a sidecar service at `bc-server`.

### Compiling AL extensions
Compile AL extensions using the dev service endpoint:
- Dev endpoint: `http://bc-server:7049/BC/`

### Running tests
After making changes to AL code, validate by:
1. Publish the extension via the dev endpoint
2. Run tests via the test runner OData API
3. Check results before creating the PR

### Available APIs
- Dev service: `http://bc-server:7049/BC/`
- OData v4: `http://bc-server:7048/BC/ODataV4/`
- SOAP: `http://bc-server:7047/BC/WS/`
```

### What This Enables

With these two files, Copilot coding agents can:

- **Compile AL code** — validate that suggested changes actually compile against
  a real BC runtime
- **Run AL tests** — execute unit tests to verify correctness, not just syntax
- **Validate PRs** — spin up a BC instance during code review to catch real errors
- **Iterate autonomously** — compile, see errors, fix, recompile — all within a
  single agent session

This is the same workflow Copilot already does for TypeScript, Python, Go, etc.
BC developers are currently excluded from this entirely.

## Why This Matters for Microsoft's AI Strategy

- **Copilot for BC is currently incomplete** — it can suggest code but never
  validate it, making it less useful than Copilot for any other language
- **Codespaces for BC doesn't exist** — developers can't "Open in Codespaces"
  for BC projects
- **GitHub Actions for BC is slow and expensive** — requires Windows runners,
  full BC installs, 45+ minute pipelines
- **The BC partner ecosystem** — thousands of ISVs and consultants — is locked
  out of modern GitHub tooling

A Linux BC Docker image fixes all of these simultaneously.

## What We've Built

A headless BC service tier running on Linux via .NET startup hooks and minimal
patches:

- BC v27.5 boots and serves on Linux in a Docker container
- Compiles AL extensions (BCApps System Application: 1264 files in ~6 seconds)
- Publishes apps via the standard BC APIs
- Executes AL tests via OData test runner
- Full CI pipeline running on standard GitHub Actions Linux runners

### Architecture

```
Docker Container (Linux)
├── SQL Server 2022 (CRONUS database)
├── BC Service Tier (DynamicsNavServer.Main)
│   ├── .NET 8 Startup Hook (15 Linux compat patches)
│   ├── Stub libraries (Drawing, Geneva, PerfCounter, HttpSys, Win32)
│   ├── Merged type-forward assemblies (netstandard, OpenXml, Drawing, Core)
│   └── .NET 8 reference assemblies for AL compilation
└── Standard BC APIs (OData, SOAP, Dev Service)
```

### What's Needed from Microsoft

- **Official support** for running BC on Linux — replacing binary patches and
  stubs with supported configuration
- **Lightweight BC image** — a minimal service tier without the Windows desktop
  dependencies
- **Published container images** — on MCR or GitHub Container Registry, versioned
  per BC release

This would make BC a first-class citizen in the GitHub + Copilot ecosystem,
matching every other Microsoft development platform.
