# The AetherFrame image worker (decision I2, N2-7c), as deployed by N2-8: one job per run, in a
# container with no network, a read-only root, and no database, key or configuration.
# Build from the repository root: docker build -f deploy/worker.Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Version.props ./
COPY server/Shared/ server/Shared/
COPY server/AetherFrame.ImageWorker/ server/AetherFrame.ImageWorker/
RUN dotnet restore server/AetherFrame.ImageWorker/AetherFrame.ImageWorker.csproj --locked-mode \
 && dotnet publish server/AetherFrame.ImageWorker/AetherFrame.ImageWorker.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .

# A user of its own, in the server's group ("app"), so it can connect to the server's socket (mode
# 0660) and do nothing else there.
RUN useradd --uid 2000 --gid app --no-create-home --shell /usr/sbin/nologin imageworker
USER imageworker

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENTRYPOINT ["dotnet", "AetherFrame.ImageWorker.dll"]
