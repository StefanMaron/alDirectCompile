FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

# Create the temp directory BC expects
RUN mkdir -p "/usr/share/Microsoft/Microsoft Dynamics NAV/270/Server"

# Copy startup hook (pre-built)
COPY ["StartupHook/bin/Release/net8.0/publish/", "/bc/hook/"]

# Override framework DLLs that throw PlatformNotSupportedException on Linux
RUN cp /bc/hook/System.Security.Principal.Windows.dll /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.*/ && \
    cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll /usr/share/dotnet/shared/Microsoft.AspNetCore.App/8.0.*/

# Copy BC service tier (use JSON array form to handle spaces)
COPY ["artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service/", "/bc/service/"]

# Make side service executables non-executable (BC needs to find them but they can't run on Linux)
# The Win32Exception from trying to execute .exe files on Linux is non-fatal.
# Do NOT delete them — BC throws a fatal SideServiceProcessException if missing.

# Set up environment
ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
WORKDIR /bc/service

# Expose standard BC ports (HttpSysStub extracts port from BC URL config)
EXPOSE 7045 7047 7048 7049 7083 7085 7086

ENTRYPOINT ["dotnet", "Microsoft.Dynamics.Nav.Server.dll", "/console"]
