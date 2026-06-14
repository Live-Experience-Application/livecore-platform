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
#   - only the unprivileged metrics/health port 9464 is exposed (CORE-DR-003)
#   - no HEALTHCHECK instruction on purpose: the runtime image ships no HTTP
#     client tooling and none is installed just for probing. Orchestration
#     platforms probe GET /health/live (per-loop liveness) over HTTP instead,
#     and Prometheus scrapes GET /metrics (see README, "Worker metrics and
#     per-loop liveness").
#
# The worker hosts background jobs that reuse the Core domain (CORE-AST-006: the
# asset cleanup job), so it references apps/api (the modular monolith's domain
# assembly). Both projects are therefore copied into the build context, and the
# runtime stage uses the ASP.NET base image because the referenced API project
# brings the Microsoft.AspNetCore.App shared framework. The worker now exposes a
# small HTTP surface for its observability (the Prometheus /metrics scrape
# endpoint and the per-loop /health/live endpoint, CORE-DR-003).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore with only the project files and repository-wide build files in the
# layer, so the NuGet restore layer stays cached until dependencies change. The
# referenced API project file is needed for the worker's restore to resolve it.
COPY Directory.Build.props .editorconfig ./
COPY apps/api/LiveCore.Api.csproj apps/api/
COPY apps/worker/LiveCore.Worker.csproj apps/worker/
RUN dotnet restore apps/worker/LiveCore.Worker.csproj

# Copy the source and publish without restoring again. Both the worker and the
# referenced API source are needed. The repository quality gates
# (EnforceCodeStyleInBuild, TreatWarningsAsErrors) apply here too.
COPY apps/api/ apps/api/
COPY apps/worker/ apps/worker/
RUN dotnet publish apps/worker/LiveCore.Worker.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The worker's metrics/health listener binds the unprivileged container port 9464
# by default (Worker:Metrics:Url, CORE-DR-003). Exposed here so the port contract of
# the image is explicit; a deployment restricts the surface at the network edge.
EXPOSE 9464

USER $APP_UID

ENTRYPOINT ["dotnet", "LiveCore.Worker.dll"]
