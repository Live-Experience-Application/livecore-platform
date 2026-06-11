# syntax=docker/dockerfile:1

# LiveCore worker host image (CORE-FND-005).
#
# Multi-stage build: the SDK stage restores and publishes, the runtime stage
# carries only the published output. Build from the repository root so the
# repository-wide build configuration (Directory.Build.props, .editorconfig)
# applies; .dockerignore keeps the context small:
#
#   docker build -f apps/worker/Dockerfile -t livecore-worker .
#
# Security baseline (mirrors the API image, see apps/api/Dockerfile):
#   - the runtime stage runs as the non-root user built into the official
#     .NET images (numeric UID via APP_UID)
#   - no SDK, package caches or build tooling in the runtime image
#   - the worker serves no HTTP traffic, so no port is exposed and no
#     HEALTHCHECK is defined; liveness is the process itself (the host exits
#     on fatal errors and the orchestration platform restarts the container)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore with only the project and repository-wide build files in the layer,
# so the NuGet restore layer stays cached until dependencies change.
COPY Directory.Build.props .editorconfig ./
COPY apps/worker/LiveCore.Worker.csproj apps/worker/
RUN dotnet restore apps/worker/LiveCore.Worker.csproj

# Copy the source and publish without restoring again. The repository quality
# gates (EnforceCodeStyleInBuild, TreatWarningsAsErrors) apply here too.
COPY apps/worker/ apps/worker/
RUN dotnet publish apps/worker/LiveCore.Worker.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "LiveCore.Worker.dll"]
