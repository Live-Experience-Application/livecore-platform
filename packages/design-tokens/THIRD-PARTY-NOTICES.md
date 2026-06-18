# Third-Party Notices

The LiveCore Core Platform is licensed AGPL-3.0-or-later (see `LICENSE`). It
redistributes third-party components whose licenses require their copyright and
permission notices to be preserved. This inventory is **generated** from
`csv/third_party_notices.csv` by `scripts/generate-third-party-notices.ps1` and
is drift-gated in CI (CORE-LIC-003); do not edit it by hand. The authoritative,
per-build component list for a published image is its CycloneDX SBOM
(CORE-DEP-003), which the license-compliance gate scans.

Upstream source: https://github.com/Live-Experience-Application/livecore-platform

## NuGet (.NET) runtime dependencies

### AWSSDK.Core 3.7.x

- License: Apache-2.0
- Copyright: Amazon.com, Inc. or its affiliates
- Source: https://github.com/aws/aws-sdk-net
- Notes: Core runtime of the AWS SDK pulled in by AWSSDK.S3.

### AWSSDK.S3 3.7.305.22

- License: Apache-2.0
- Copyright: Amazon.com, Inc. or its affiliates
- Source: https://github.com/aws/aws-sdk-net
- Notes: S3-compatible asset storage SigV4 pre-signing and object delete (CORE-OPS-006).

### Google.Protobuf 3.x

- License: BSD-3-Clause
- Copyright: Google Inc.
- Source: https://github.com/protocolbuffers/protobuf
- Notes: Protocol Buffers runtime under the OTLP exporter.

### Grpc.Net.Client 2.x

- License: Apache-2.0
- Copyright: The gRPC Authors
- Source: https://github.com/grpc/grpc-dotnet
- Notes: gRPC transport under the OTLP exporter.

### Microsoft.AspNetCore.Authentication.JwtBearer 10.0.9

- License: MIT
- Copyright: Microsoft Corporation
- Source: https://github.com/dotnet/aspnetcore
- Notes: OIDC JWT bearer validation (docs/adr/0005).

### Microsoft.AspNetCore.OpenApi 10.0.9

- License: MIT
- Copyright: Microsoft Corporation
- Source: https://github.com/dotnet/aspnetcore
- Notes: OpenAPI document generation for the v1 API (CORE-OAS-001).

### Microsoft.AspNetCore.SignalR.StackExchangeRedis 10.0.9

- License: MIT
- Copyright: Microsoft Corporation
- Source: https://github.com/dotnet/aspnetcore
- Notes: SignalR Redis/Valkey backplane for realtime scale-out (CORE-OPS-007).

### Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.9

- License: MIT
- Copyright: Microsoft Corporation
- Source: https://github.com/dotnet/efcore
- Notes: Database health check used by the readiness probe.

### Microsoft.NET / ASP.NET Core shared framework net10.0

- License: MIT
- Copyright: .NET Foundation and Contributors
- Source: https://github.com/dotnet/runtime
- Notes: The .NET runtime and ASP.NET Core shared framework provided by the base image.

### Npgsql 8.x

- License: PostgreSQL
- Copyright: The Npgsql Project
- Source: https://github.com/npgsql/npgsql
- Notes: PostgreSQL ADO.NET driver under the EF Core provider.

### Npgsql.EntityFrameworkCore.PostgreSQL 10.0.2

- License: PostgreSQL
- Copyright: The Npgsql Project
- Source: https://github.com/npgsql/efcore.pg
- Notes: PostgreSQL EF Core provider (docs/02_ARCHITECTURE.md).

### OpenTelemetry.Exporter.OpenTelemetryProtocol 1.16.0

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: OTLP span/metric export to a configured collector (CORE-OBS-003).

### OpenTelemetry.Exporter.Prometheus.AspNetCore 1.16.0-beta.1

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: Prometheus pull-based /metrics exposition (CORE-OBS-001).

### OpenTelemetry.Extensions.Hosting 1.16.0

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: OpenTelemetry SDK + host integration for metrics/tracing (CORE-OBS-001/003).

### OpenTelemetry.Instrumentation.AspNetCore 1.15.2

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: ASP.NET Core request auto-instrumentation spans (CORE-OBS-005).

### OpenTelemetry.Instrumentation.EntityFrameworkCore 1.15.1-beta.1

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: EF Core database-query auto-instrumentation spans (CORE-OBS-005).

### OpenTelemetry.Instrumentation.Http 1.15.1

- License: Apache-2.0
- Copyright: The OpenTelemetry Authors
- Source: https://github.com/open-telemetry/opentelemetry-dotnet
- Notes: Outbound HttpClient auto-instrumentation spans (CORE-OBS-005).

### StackExchange.Redis 2.x

- License: MIT
- Copyright: Stack Exchange
- Source: https://github.com/StackExchange/StackExchange.Redis
- Notes: Redis/Valkey client under the SignalR backplane.

## Container base image

### Debian GNU/Linux base image packages (see image SBOM)

- License: various
- Copyright: Respective Debian package maintainers
- Source: https://www.debian.org
- Notes: OS packages in the mcr.microsoft.com/dotnet/aspnet base image; their exact set and licenses for a given build are recorded in the per-image CycloneDX SBOM (CORE-DEP-003) and checked by the license-compliance gate.

## npm (TypeScript packages) runtime dependencies

### (none)

- License: AGPL-3.0-or-later
- Copyright: LiveCore
- Source: https://github.com/Live-Experience-Application/livecore-platform
- Notes: The published @livecore/* packages ship only first-party compiled TypeScript (dist/) and declare no third-party npm runtime dependencies, so there is no third-party npm code to attribute.

Full license texts for the SPDX identifiers above are available from the
respective upstream sources and from https://spdx.org/licenses/.
