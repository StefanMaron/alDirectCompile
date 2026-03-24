FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

# Install tools for artifact download and SQL setup
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl unzip python3 gnupg && \
    curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list && \
    ACCEPT_EULA=Y apt-get update && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Copy startup hook (pre-built)
COPY ["StartupHook/bin/Release/net8.0/publish/", "/bc/hook/"]

# Copy patched DLLs (Cecil-modified to fix Linux-specific bugs)
# Includes: CodeAnalysis.dll, Mono.Cecil.dll, merged assemblies (netstandard, OpenXml, Drawing, Core)
COPY ["StartupHook/patched/", "/bc/patched/"]

# Copy .NET reference assemblies (originals — used as base, overridden by forwarding assemblies)
COPY ["StartupHook/refasm/", "/bc/refasm/"]

# Copy DrawingStub to Add-Ins dir (compile-time type resolution for System.Drawing.Common)
# This is SEPARATE from /bc/hook/ DrawingStub which is loaded at runtime
COPY ["StartupHook/DrawingStub/bin/Release/net8.0/System.Drawing.Common.dll", "/bc/addins-overlay/System.Drawing.Common.dll"]

# Copy MockTest.dll for test framework (required by System Application Test Library)
COPY ["StartupHook/testdata/MockTest.dll", "/bc/addins-overlay/MockTest.dll"]

# Copy scripts and test runner
COPY ["scripts/download-artifacts.sh", "scripts/entrypoint.sh", "/bc/scripts/"]
COPY ["TestRunnerExtension/TestRunner.app", "/bc/testrunner/TestRunner.app"]

RUN chmod +x /bc/scripts/*.sh

# Directories created at runtime based on BC version
RUN mkdir -p /bc/service /bc/artifacts

ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
EXPOSE 7045-7054 7083 7085-7089

ENTRYPOINT ["/bc/scripts/entrypoint.sh"]
