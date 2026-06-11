# syntax=docker/dockerfile:1

# LiveCore API host image (CORE-FND-005).
#
# Multi-stage build: the SDK stage restores and publishes, the runtime stage
# carries only the published output. Build from the repository root so the
# repository-wide build configuration (Directory.Build.props, .editorconfig)
# applies; .dockerignore keeps the context small:
#
#   docker build -f apps/api/Dockerfile -t livecore-api .
#
# Security baseline:
#   - the runtime stage runs as the non-root user built into the official
#     .NET images (numeric UID via APP_UID, verifiable by runtime policies
#     such as runAsNonRoot)
#   - no SDK, package caches or build tooling in the runtime image
#   - only the unprivileged HTTP port 8080 is exposed
#   - no HEALTHCHECK instruction on purpose: the runtime image ships no HTTP
#     client tooling and none is installed just for probing. Orchestration
#     platforms probe GET /health/live (liveness) and GET /health/ready
#     (readiness) over HTTP instead (see README, "Container images").

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore with only the project and repository-wide build files in the layer,
# so the NuGet restore layer stays cached until dependencies change.
COPY Directory.Build.props .editorconfig ./
COPY apps/api/LiveCore.Api.csproj apps/api/
RUN dotnet restore apps/api/LiveCore.Api.csproj

# Copy the source and publish without restoring again. The repository quality
# gates (EnforceCodeStyleInBuild, TreatWarningsAsErrors) apply here too.
COPY apps/api/ apps/api/
RUN dotnet publish apps/api/LiveCore.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Kestrel listens on the unprivileged container port 8080. This is the
# default of the official .NET runtime images; it is restated here so the
# port contract of the image is explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "LiveCore.Api.dll"]
