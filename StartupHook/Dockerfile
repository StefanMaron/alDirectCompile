FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

# Create the temp directory BC expects
RUN mkdir -p "/usr/share/Microsoft/Microsoft Dynamics NAV/270/Server"

# Copy BC service tier (use JSON array form to handle spaces)
COPY ["artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service/", "/bc/service/"]

# Copy startup hook (pre-built)
COPY ["StartupHook/bin/Release/net8.0/publish/", "/bc/hook/"]

# Set up environment
ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
WORKDIR /bc/service

# Expose BC ports
EXPOSE 17045 17047 17048 17049 17085 17086

ENTRYPOINT ["dotnet", "Microsoft.Dynamics.Nav.Server.dll", "/console"]
