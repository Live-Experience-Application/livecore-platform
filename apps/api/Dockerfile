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
#
# Supply chain (CORE-DEP-003):
#   - the base images are pinned by immutable digest (name:tag@sha256:...), not by
#     the floating '10.0' tag, so a rebuild always resolves the exact same base
#     layers and a silently re-published base image cannot drift into a release.
#     Bump a digest deliberately (resolve it with `docker buildx imagetools
#     inspect mcr.microsoft.com/dotnet/sdk:10.0`); the readable tag is kept beside
#     the digest only for humans - the digest is what Docker resolves.
#   - the NuGet restore runs in locked mode against the committed packages.lock.json,
#     so the published dependency graph is reproducible and cannot float.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore with only the project, repository-wide build files and the committed
# lock file in the layer, so the NuGet restore layer stays cached until
# dependencies change. Locked mode fails the build if the resolved package graph
# ever drifts from packages.lock.json (CORE-DEP-003).
COPY Directory.Build.props .editorconfig ./
COPY apps/api/LiveCore.Api.csproj apps/api/packages.lock.json apps/api/
RUN dotnet restore apps/api/LiveCore.Api.csproj --locked-mode

# Copy the source and publish without restoring again. The repository quality
# gates (EnforceCodeStyleInBuild, TreatWarningsAsErrors) apply here too.
COPY apps/api/ apps/api/
RUN dotnet publish apps/api/LiveCore.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Kestrel listens on the unprivileged container port 8080. This is the
# default of the official .NET runtime images; it is restated here so the
# port contract of the image is explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "LiveCore.Api.dll"]
