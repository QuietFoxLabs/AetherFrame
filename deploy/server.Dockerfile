# The AetherFrame server (docs/networking/NETWORK2.md, N2-7), as deployed by N2-8.
# Build from the repository root: docker build -f deploy/server.Dockerfile .
# The base images follow their 10.0 tags, so a rebuild picks up .NET's security patches.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Version.props ./
COPY AetherFrame.Protocol/ AetherFrame.Protocol/
COPY server/Shared/ server/Shared/
COPY server/AetherFrame.Server/ server/AetherFrame.Server/
RUN dotnet restore server/AetherFrame.Server/AetherFrame.Server.csproj --locked-mode \
 && dotnet publish server/AetherFrame.Server/AetherFrame.Server.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .

# The folders the server writes: its database, the image worker's socket, and backups. Their
# owner is the image's unprivileged user, and a named volume mounted there starts with that owner.
RUN mkdir -p /data /run/aetherframe /backups \
 && chown app:app /data /run/aetherframe /backups \
 && chmod 750 /run/aetherframe
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "AetherFrame.Server.dll"]
