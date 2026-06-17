// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LiveCore.SmokeTests;

/// <summary>
/// Production OIDC audience-guard tests (CORE-OPS-004) over the real production <c>Program</c> with no test
/// seams: only the environment and the <c>Authentication:Oidc:*</c> settings are configured.
///
/// They pin the acceptance criterion: when an OIDC Authority is configured, a blank Audience silently
/// disables audience scoping (any token the issuer signs would be accepted), so in a Production environment
/// that is a misconfiguration and the host REFUSES TO START (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). A configured Audience is accepted (the host starts and audience
/// validation is wired), the existing fail-closed handler for the unconfigured-Authority case is preserved,
/// and outside Production a blank Audience stays tolerated (local-development latitude).
/// </summary>
public class OidcAudienceStartupGuardTests
{
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        string? authority,
        string? audience)
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
        });
    }

    [Fact]
    public void Host_refuses_to_start_in_production_when_an_authority_is_configured_but_the_audience_is_blank()
    {
        using var factory = CreateFactory("Production", _authority, audience: null);

        // Touching Services builds the real host, which runs the OIDC wiring and its startup guard.
        var exception = Record.Exception(() => _ = factory.Services);

        // The host refuses to start rather than serving requests with audience validation off.
        Assert.NotNull(exception);
        Assert.Contains("Audience", Flatten(exception!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Host_starts_in_production_with_a_configured_audience_and_enables_audience_validation()
    {
        using var factory = CreateFactory("Production", _authority, _audience);
        using var client = factory.CreateClient();

        // A valid audience is accepted (not treated as a misconfiguration): the host started and the
        // anonymous liveness endpoint responds.
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And the configured audience is actually wired into token validation, not silently ignored.
        var jwtOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.True(jwtOptions.TokenValidationParameters.ValidateAudience);
        Assert.Equal(_audience, jwtOptions.TokenValidationParameters.ValidAudience);
    }

    [Fact]
    public async Task Host_starts_in_production_when_no_authority_is_configured()
    {
        // The unconfigured-Authority case keeps its existing fail-closed behavior: the guard does not fire
        // (no Authority means no token is ever accepted), so the host still starts.
        using var factory = CreateFactory("Production", authority: null, audience: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_starts_outside_production_even_when_the_audience_is_blank()
    {
        // Outside Production a blank Audience stays tolerated (the same local-development latitude
        // RequireHttpsMetadata=false allows), so a dev run against a local identity provider still starts.
        using var factory = CreateFactory("Development", _authority, audience: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
