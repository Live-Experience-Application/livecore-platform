# syntax=docker/dockerfile:1

# LiveCore database migrations runner image (CORE-OPS-001, locked by CORE-OPS-012).
#
# This is the deterministic migration application path for a deploy: a one-shot
# container that applies every checked-in EF Core migration to the target
# PostgreSQL database, then exits. Run it to completion BEFORE the API rolls out
# (a Kubernetes pre-install/pre-upgrade Job or init-container, a Compose `migrate`
# service the `api` service depends on, or a Railway pre-deploy command) so a fresh
# deploy gets its schema applied before the API serves traffic. The API host itself
# never migrates implicitly on startup, so a multi-instance rollout never races to
# migrate (see docs/13_SELF_HOSTING_REQUIREMENTS.md, "Database migrations").
#
# The runner is the published API assembly invoked with the `migrate` verb
# (apps/api/Hosting/MigrationCommand.cs), NOT a web host: that code path applies the
# migrations and exits without ever building Kestrel. It wraps the apply in a
# SESSION-LEVEL PostgreSQL advisory lock on a fixed app key (CORE-OPS-012,
# LiveCoreMigrationRunner), so two runner invocations racing - a retried Helm
# pre-upgrade hook, an overlapping redeploy, two operator runs - SERIALISE: exactly
# one applies and any other blocks until it finishes, then finds the schema current
# and applies nothing (idempotent, exit 0). That closes the gap the migrate-before-
# API gate alone leaves (it orders the runner before the API, but does not stop two
# runners from racing each other).
#
# Build from the repository root so the repository-wide build configuration
# (Directory.Build.props, .editorconfig) applies; .dockerignore keeps the context
# small:
#
#   docker build -f apps/api/Migrations.Dockerfile -t livecore-migrations .
#
# Run it against the target database (no credentials are baked into the image;
# the connection string is supplied at run time, matching the API's config key):
#
#   docker run --rm \
#     -e ConnectionStrings__Database="Host=db;Port=5432;Database=livecore;Username=livecore;Password=..." \
#     livecore-migrations
#
# The connection string can also be passed as `--connection "<value>"` (forwarded to
# the migrate command), which overrides the environment variable.
#
# Security baseline (mirrors apps/api/Dockerfile):
#   - the runtime stage runs as the non-root user built into the official .NET
#     images (numeric UID via APP_UID, verifiable by runAsNonRoot policies)
#   - no SDK, package caches or build tooling in the runtime image: only the
#     published application is carried. It applies migrations through the runtime EF
#     Core already linked into the assembly (no dotnet-ef CLI tool is needed at run
#     time). It links the API assembly, which is a Web project, so the runtime stage
#     uses the ASP.NET base image for the Microsoft.AspNetCore.App shared framework
#     (as the API and worker images do), but the migrate verb serves no HTTP traffic
#     and exposes no port
#   - no credentials in the image; the connection string is provided at run time
#   - no port is exposed and no HEALTHCHECK is defined: the container is a
#     run-to-completion job whose exit code is the success signal
#
# Supply chain (CORE-DEP-003): the base images are pinned by immutable digest and
# the NuGet restore runs in locked mode against the committed packages.lock.json,
# exactly as apps/api/Dockerfile - see that file for the rationale and how to bump
# a digest.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restore with only the project, repository-wide build files and the committed lock
# file in the layer, so the NuGet restore layer stays cached until dependencies
# change. Locked mode fails the build if the resolved package graph ever drifts from
# the committed packages.lock.json (CORE-DEP-003).
COPY Directory.Build.props .editorconfig ./
COPY apps/api/LiveCore.Api.csproj apps/api/packages.lock.json apps/api/
RUN dotnet restore apps/api/LiveCore.Api.csproj --locked-mode

# Copy the source and publish without restoring again. The repository quality gates
# (EnforceCodeStyleInBuild, TreatWarningsAsErrors) apply here too. The published
# output carries the migrations (compiled into the assembly) and the runtime EF Core
# that applies them; the migrate verb needs no SDK or dotnet-ef tool at run time.
COPY apps/api/ apps/api/
RUN dotnet publish apps/api/LiveCore.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

# Applies all pending migrations to the database named by
# ConnectionStrings__Database under the fixed-key advisory lock, then exits 0
# (already up to date is a no-op). Extra arguments (e.g. `--connection "<value>"`)
# are forwarded to the migrate command.
ENTRYPOINT ["dotnet", "LiveCore.Api.dll", "migrate"]
