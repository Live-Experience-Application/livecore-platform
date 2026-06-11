using System.Net;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LiveCore.SmokeTests;

/// <summary>
/// Readiness behavior tests (CORE-ID-002).
///
/// Covers the unhealthy readiness path (503 with the minimal status-only
/// body, no check or exception details leaked to the unauthenticated
/// endpoint) and the conditional persistence wiring: the database readiness
/// check and the IdentityAccess persistence services exist exactly when a
/// database connection string is configured.
/// </summary>
public class ReadinessHealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Placeholder for a configured database; no connection is ever opened by
    // these tests (the readiness endpoint is not called on this factory).
    private const string _testConnectionString = "Host=localhost;Port=5432;Database=livecore-smoke-test";

    private readonly WebApplicationFactory<Program> _factory;

    public ReadinessHealthCheckTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Ready_returns_503_with_status_only_body_when_a_readiness_check_fails()
    {
        const string secretFailureDetail = "secret-dependency-failure-detail";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "failing-dependency",
                    new ThrowingHealthCheck(secretFailureDetail),
                    tags: ["ready"])));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // Only the overall status leaves the process: no check names, no
        // durations and especially no exception details
        // (docs/07_SECURITY_THREAT_MODEL.md).
        Assert.Equal("""{"status":"Unhealthy"}""", body);
        Assert.DoesNotContain(secretFailureDetail, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failing_readiness_check_does_not_affect_liveness()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "failing-dependency",
                    new ThrowingHealthCheck("dependency down"),
                    tags: ["ready"])));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        // A failing dependency must never make an orchestrator restart an
        // otherwise healthy process.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Database_readiness_check_is_registered_when_a_connection_string_is_configured()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Database", _testConnectionString));

        var options = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        var registration = Assert.Single(
            options.Value.Registrations,
            candidate => candidate.Name == "database");
        Assert.Contains("ready", registration.Tags);
    }

    [Fact]
    public void Database_readiness_check_is_absent_without_a_connection_string()
    {
        var options = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>();

        Assert.DoesNotContain(
            options.Value.Registrations,
            candidate => candidate.Name == "database");
    }

    [Fact]
    public void Persistence_services_are_registered_only_with_a_connection_string()
    {
        using var configuredFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Database", _testConnectionString));

        using var configuredScope = configuredFactory.Services.CreateScope();
        Assert.NotNull(configuredScope.ServiceProvider.GetService<LiveCoreDbContext>());
        Assert.NotNull(configuredScope.ServiceProvider.GetService<IUserProfileRepository>());
        Assert.NotNull(configuredScope.ServiceProvider.GetService<UserProfileReferenceService>());

        using var defaultScope = _factory.Services.CreateScope();
        Assert.Null(defaultScope.ServiceProvider.GetService<LiveCoreDbContext>());
        Assert.Null(defaultScope.ServiceProvider.GetService<IUserProfileRepository>());
        Assert.Null(defaultScope.ServiceProvider.GetService<UserProfileReferenceService>());
    }

    private sealed class ThrowingHealthCheck : IHealthCheck
    {
        private readonly string _failureDetail;

        public ThrowingHealthCheck(string failureDetail)
        {
            _failureDetail = failureDetail;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_failureDetail);
    }
}
