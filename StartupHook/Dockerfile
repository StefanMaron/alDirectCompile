FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

# Install tools for artifact download and SQL setup
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl unzip python3 gnupg && \
    curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list && \
    ACCEPT_EULA=Y apt-get update && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /bc/hook /bc/patched /bc/refasm /bc/addins-overlay /bc/testrunner /bc/service /bc/artifacts /bc/scripts

# Copy startup hook (pre-built — required)
COPY ["StartupHook/bin/Release/net8.0/publish/", "/bc/hook/"]

# Copy scripts (required)
COPY ["scripts/download-artifacts.sh", "scripts/entrypoint.sh", "/bc/scripts/"]
RUN chmod +x /bc/scripts/*.sh

# Copy pre-built artifacts if available (speeds up startup for local dev).
# Uses .gitkeep placeholders so COPY works even from a fresh clone.
# The entrypoint generates anything missing from BC artifacts on first boot.
COPY ["StartupHook/patched/", "/bc/patched/"]
COPY ["StartupHook/refasm/", "/bc/refasm/"]
COPY ["StartupHook/testdata/", "/bc/addins-overlay/"]

# DrawingStub and TestRunner (built as part of StartupHook publish)
COPY ["StartupHook/DrawingStub/bin/Release/net8.0/System.Drawing.Common.dll", "/bc/addins-overlay/System.Drawing.Common.dll"]
COPY ["TestRunnerExtension/TestRunner.app", "/bc/testrunner/TestRunner.app"]

ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
EXPOSE 7045-7054 7083 7085-7089

ENTRYPOINT ["/bc/scripts/entrypoint.sh"]
