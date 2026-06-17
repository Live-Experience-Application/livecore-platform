// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// Production readiness-hardening tests (CORE-OPS-005) over the real production <c>Program</c> with no test
/// seams: only the environment and the required-dependency configuration are set.
///
/// They pin the acceptance criterion: in a Production environment the API reports NOT-READY (503 on
/// <c>/health/ready</c>) when persistence or OIDC is unconfigured — previously such a host reported READY
/// while every domain route fails closed (503/401), so orchestration would route live traffic at an API that
/// cannot serve it. Outside Production the gate is inert (the existing local-development latitude is
/// preserved), liveness is never affected by it, and the readiness response stays status-only so the missing
/// dependency never leaks to the unauthenticated endpoint (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public class ProductionReadinessHealthCheckTests
{
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        string? authority = null,
        string? audience = null,
        string? connectionString = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);

            if (authority is not null)
            {
                builder.UseSetting($"{OidcAuthenticationExtensions.ConfigurationSection}:Authority", authority);
            }

            if (audience is not null)
            {
                builder.UseSetting($"{OidcAuthenticationExtensions.ConfigurationSection}:Audience", audience);
            }

            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Database", connectionString);
            }
        });
    }

    [Fact]
    public async Task Ready_is_503_in_production_when_persistence_and_oidc_are_unconfigured()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Status-only: which dependency is missing never leaks to the unauthenticated endpoint (threat T7).
        Assert.Equal("""{"status":"Unhealthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_is_503_in_production_when_only_persistence_is_unconfigured()
    {
        // OIDC fully configured (so the host starts and the audience guard is satisfied), but no database
        // connection string. The database readiness check is therefore not registered, so the required-
        // dependency gate is the sole readiness check — and it reports NOT-READY because persistence is
        // missing. This isolates the "persistence unconfigured" path from any database-connectivity result.
        using var factory = CreateFactory("Production", _authority, _audience);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_is_200_outside_production_when_unconfigured()
    {
        // Local-development latitude is preserved: with no required dependency configured a Development host
        // still reports READY (the gate is inert outside Production).
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_stays_200_in_production_even_when_unconfigured()
    {
        // The required-dependency gate is a READINESS check only: a not-ready misconfiguration must never make
        // an orchestrator restart an otherwise live process.
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
    }
}
