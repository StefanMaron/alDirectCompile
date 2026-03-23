FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

# Create the temp directory BC expects
RUN mkdir -p "/usr/share/Microsoft/Microsoft Dynamics NAV/270/Server"

# Copy startup hook (pre-built)
COPY ["StartupHook/bin/Release/net8.0/publish/", "/bc/hook/"]

# Override framework DLLs that throw PlatformNotSupportedException on Linux
RUN cp /bc/hook/System.Security.Principal.Windows.dll /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.*/ && \
    cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll /usr/share/dotnet/shared/Microsoft.AspNetCore.App/8.0.*/

# Copy BC service tier
COPY ["artifacts/onprem/27.5.46862.0/platform/ServiceTier/PFiles64/Microsoft Dynamics NAV/270/Service/", "/bc/service/"]

ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
WORKDIR /bc/service
EXPOSE 7045-7054 7083 7085-7089

ENTRYPOINT ["dotnet", "Microsoft.Dynamics.Nav.Server.dll", "/console"]
