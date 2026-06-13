using System.Net;
using LiveCore.Api.Hosting;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// Production secret-management and configuration-contract tests (CORE-OPS-008) over the real production
/// <c>Program</c> with no test seams: only the environment and the contract settings are configured.
///
/// They pin the acceptance criterion's posture, which REUSES the existing fail-closed-when-unconfigured stance
/// rather than adding a new one: with the documented required production settings present the host starts and
/// serves liveness (a properly configured host is never blocked by an over-strict contract); with a required
/// value missing the host still starts and fails closed (it logs a loud, named startup error and reports
/// not-ready — CORE-OPS-005 — rather than crashing an otherwise-live process). The only hard fail-to-start case
/// stays the OIDC audience foot-gun (CORE-OPS-004, covered by <see cref="OidcAudienceStartupGuardTests"/>). No
/// secret is committed: the contract ships as the names-only repository-root <c>.env.example</c>.
/// </summary>
public class ProductionConfigurationContractTests
{
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";
    private const string _connectionString = "Host=db;Port=5432;Database=livecore;Username=livecore;Password=secret";

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
                builder.UseSetting(ProductionConfigurationValidator.DatabaseConnectionStringKey, connectionString);
            }
        });
    }

    [Fact]
    public async Task Host_starts_in_production_when_the_full_configuration_contract_is_satisfied()
    {
        // Every required production value is present, so the contract reports nothing missing and the host
        // starts and serves liveness. This pins that the required set is exactly correct — not so strict that a
        // properly configured host is blocked from starting.
        using var factory = CreateFactory("Production", _authority, _audience, _connectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_still_starts_in_production_when_a_required_value_is_missing()
    {
        // OIDC is fully configured (so the audience guard is satisfied) but persistence is not. A missing
        // required value does NOT crash an otherwise-live process: the host fails loudly via a startup log and
        // reports not-ready (CORE-OPS-005), but liveness stays up so a misconfiguration never triggers a
        // restart loop. The fail-closed posture is reused, not replaced.
        using var factory = CreateFactory("Production", _authority, _audience, connectionString: null);
        using var client = factory.CreateClient();

        var liveResponse = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);

        // It reports not-ready because persistence is unconfigured in production (status-only; threat T7).
        var readyResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
    }
}
