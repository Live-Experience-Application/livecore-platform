using System.Globalization;
using System.Net;
using System.Text;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Tests for request rate limiting (CORE-SEC-001, the "Security Hardening" epic). They cover both the
/// configuration parsing (pinned directly against <see cref="RateLimitingConfiguration.RateLimitingSettings.Read"/>,
/// exactly as <see cref="ForwardedHeadersTests"/> pins <c>Apply</c>) and the end-to-end HTTP behavior the story's
/// required tests name, driving the REAL application over real HTTP through <see cref="WorkspaceApiFactory"/>:
/// <list type="bullet">
///   <item>the anonymous store-notification webhook returns <c>429</c> past its per-IP threshold;</item>
///   <item>an authenticated principal is throttled with <c>429</c> past its per-principal global limit;</item>
///   <item>NORMAL TRAFFIC IS UNAFFECTED — a different principal is in its own partition (not throttled by the
///   first principal's burst), and under the generous default limits a normal volume of requests all succeed;</item>
///   <item>the webhook's hard request-body-size cap rejects an oversize body with <c>413</c>.</item>
/// </list>
/// The HTTP tests configure deliberately LOW limits through in-memory configuration (the same override mechanism
/// the CORS/forwarded-headers tests use), so a handful of requests crosses the threshold deterministically; the
/// production defaults stay generous.
/// </summary>
public sealed class RateLimitingEndpointTests
{
    private const string _appleRoute = "/api/v1/store-notifications/apple";
    private const string _issuer = TestAuthenticationHandler.DefaultIssuer;

    // ====================================================================
    // Configuration parsing (limits are configurable; fail-safe defaults).
    // ====================================================================

    [Fact]
    public void Read_uses_safe_defaults_when_nothing_is_configured()
    {
        var settings = RateLimitingConfiguration.RateLimitingSettings.Read(new ConfigurationBuilder().Build());

        Assert.True(settings.Enabled);
        Assert.Equal(RateLimitingConfiguration.DefaultGlobalPermitLimit, settings.GlobalPermitLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultGlobalWindowSeconds, settings.GlobalWindowSeconds);
        Assert.Equal(0, settings.GlobalQueueLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultWebhookPermitLimit, settings.WebhookPermitLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultWebhookWindowSeconds, settings.WebhookWindowSeconds);
        Assert.Equal(0, settings.WebhookQueueLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultWebhookMaxRequestBodyBytes, settings.WebhookMaxRequestBodyBytes);
    }

    [Fact]
    public void Read_reflects_configured_limits()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "false",
                ["RateLimiting:Global:PermitLimit"] = "12",
                ["RateLimiting:Global:WindowSeconds"] = "30",
                ["RateLimiting:Global:QueueLimit"] = "5",
                ["RateLimiting:Webhooks:PermitLimit"] = "4",
                ["RateLimiting:Webhooks:WindowSeconds"] = "15",
                ["RateLimiting:Webhooks:QueueLimit"] = "2",
                ["RateLimiting:Webhooks:MaxRequestBodyBytes"] = "4096",
            })
            .Build();

        var settings = RateLimitingConfiguration.RateLimitingSettings.Read(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal(12, settings.GlobalPermitLimit);
        Assert.Equal(30, settings.GlobalWindowSeconds);
        Assert.Equal(5, settings.GlobalQueueLimit);
        Assert.Equal(4, settings.WebhookPermitLimit);
        Assert.Equal(15, settings.WebhookWindowSeconds);
        Assert.Equal(2, settings.WebhookQueueLimit);
        Assert.Equal(4096, settings.WebhookMaxRequestBodyBytes);
    }

    [Theory]
    [InlineData("RateLimiting:Global:PermitLimit", "0")]
    [InlineData("RateLimiting:Global:PermitLimit", "-5")]
    [InlineData("RateLimiting:Global:WindowSeconds", "0")]
    [InlineData("RateLimiting:Webhooks:PermitLimit", "-1")]
    [InlineData("RateLimiting:Webhooks:MaxRequestBodyBytes", "0")]
    public void Read_falls_back_to_the_default_for_a_non_positive_limit(string key, string value)
    {
        // A misconfiguration must never silently REMOVE a limit; a non-positive value falls back to the default.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var settings = RateLimitingConfiguration.RateLimitingSettings.Read(configuration);

        Assert.Equal(RateLimitingConfiguration.DefaultGlobalPermitLimit, settings.GlobalPermitLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultGlobalWindowSeconds, settings.GlobalWindowSeconds);
        Assert.Equal(RateLimitingConfiguration.DefaultWebhookPermitLimit, settings.WebhookPermitLimit);
        Assert.Equal(RateLimitingConfiguration.DefaultWebhookMaxRequestBodyBytes, settings.WebhookMaxRequestBodyBytes);
    }

    [Fact]
    public void Read_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => RateLimitingConfiguration.RateLimitingSettings.Read(null!));
    }

    // ====================================================================
    // The anonymous webhook returns 429 past the per-IP threshold.
    // ====================================================================

    [Fact]
    public async Task Anonymous_webhook_returns_429_past_the_threshold()
    {
        // Strict per-IP webhook limit of 2 per window. The first two requests reach the handler (and fail closed
        // with 503 because no parser is configured); the third crosses the per-IP ceiling and is rejected 429
        // BEFORE any per-call database work or the external parser runs.
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Webhooks:PermitLimit"] = "2",
            ["RateLimiting:Webhooks:WindowSeconds"] = "60",
        });
        using var client = factory.CreateAnonymousClient();

        var first = await PostWebhookAsync(client, "payload-1");
        var second = await PostWebhookAsync(client, "payload-2");
        var third = await PostWebhookAsync(client, "payload-3");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // ====================================================================
    // An authenticated principal is throttled past its global limit,
    // and a DIFFERENT principal (normal traffic) is unaffected.
    // ====================================================================

    [Fact]
    public async Task Authenticated_principal_is_throttled_past_its_limit_while_another_principal_is_unaffected()
    {
        // Per-principal global limit of 3 per window. Principal A's first three reads succeed; the fourth is 429.
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "3",
            ["RateLimiting:Global:WindowSeconds"] = "60",
        });
        using var principalA = factory.CreateClientFor("ratelimit-user-a", _issuer);
        using var principalB = factory.CreateClientFor("ratelimit-user-b", _issuer);

        for (var i = 0; i < 3; i++)
        {
            var allowed = await principalA.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var throttled = await principalA.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // The limiter is partitioned per principal (issuer+subject): principal B's burst cannot be charged to A,
        // so B is still served — normal traffic is unaffected by another caller's throttling (threats T5/T1).
        var otherPrincipal = await principalB.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, otherPrincipal.StatusCode);
    }

    [Fact]
    public async Task A_throttled_response_is_problem_details_with_retry_after()
    {
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "1",
            ["RateLimiting:Global:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClientFor("ratelimit-user-c", _issuer);

        var allowed = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var throttled = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
        Assert.True(throttled.Headers.Contains("Retry-After"), "A 429 must carry a Retry-After header.");
    }

    // ====================================================================
    // RateLimit-* headers on success and on 429 (CORE-DX-005), and the
    // 429 carries no tenant/principal detail (threat T7).
    // ====================================================================

    [Fact]
    public async Task An_admitted_authenticated_response_carries_the_rate_limit_headers()
    {
        // CORE-DX-005: an admitted per-principal request reports its remaining allowance so a browser SDK can
        // back off before it is throttled. With a window of 5, the first (and only) request reports 4 remaining.
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "5",
            ["RateLimiting:Global:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClientFor("ratelimit-headers-user", _issuer);

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("5", Single(response, RateLimitingConfiguration.RateLimitLimitHeaderName));
        Assert.Equal("4", Single(response, RateLimitingConfiguration.RateLimitRemainingHeaderName));
        Assert.Equal("60", Single(response, RateLimitingConfiguration.RateLimitResetHeaderName));
    }

    [Fact]
    public async Task A_throttled_response_carries_the_rate_limit_headers_without_principal_detail()
    {
        const string subject = "ratelimit-leak-check-user";
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "1",
            ["RateLimiting:Global:WindowSeconds"] = "60",
        });
        using var client = factory.CreateClientFor(subject, _issuer);

        var allowed = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var throttled = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("1", Single(throttled, RateLimitingConfiguration.RateLimitLimitHeaderName));
        Assert.Equal("0", Single(throttled, RateLimitingConfiguration.RateLimitRemainingHeaderName));

        // The reset is a positive seconds-until-window-roll, not a leak.
        var reset = int.Parse(Single(throttled, RateLimitingConfiguration.RateLimitResetHeaderName), CultureInfo.InvariantCulture);
        Assert.InRange(reset, 1, 60);

        // The 429 body (and the headers) carry no tenant/principal/resource detail (threat T7).
        var body = await throttled.Content.ReadAsStringAsync();
        Assert.DoesNotContain(subject, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_issuer, body, StringComparison.OrdinalIgnoreCase);
    }

    private static string Single(HttpResponseMessage response, string header)
    {
        Assert.True(response.Headers.TryGetValues(header, out var values), $"Expected the {header} header.");
        return Assert.Single(values);
    }

    // ====================================================================
    // Normal traffic is unaffected under the generous default limits.
    // ====================================================================

    [Fact]
    public async Task Normal_traffic_under_the_default_limits_is_not_throttled()
    {
        // The unmodified app (production-default generous limits). A normal volume of authenticated reads by one
        // principal all succeed — rate limiting does not disrupt ordinary use.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("ratelimit-normal-user", _issuer);

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ====================================================================
    // The webhook hard body-size cap rejects an oversize body with 413.
    // ====================================================================

    [Fact]
    public async Task Webhook_rejects_an_oversize_body_with_413()
    {
        // A tiny body cap so a modest payload crosses it; the body is rejected 413 before it is buffered or parsed.
        await using var factory = new RateLimitedApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Webhooks:MaxRequestBodyBytes"] = "32",
        });
        using var client = factory.CreateAnonymousClient();

        var oversize = await PostWebhookAsync(client, new string('x', 256));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);

        // A within-cap body is NOT rejected by the size guard (it fails closed 503 because no parser is configured).
        var withinCap = await PostWebhookAsync(client, "small");
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, withinCap.StatusCode);
    }

    // ====================================================================
    // Helpers.
    // ====================================================================

    private static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string payload)
        => await client.PostAsync(_appleRoute, new StringContent(payload, Encoding.UTF8, "application/json"));

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that layers the supplied <c>RateLimiting:*</c> overrides on top of the
    /// production configuration, so a test can drive deliberately low limits without weakening any other
    /// production wiring.
    /// </summary>
    private sealed class RateLimitedApiFactory(IReadOnlyDictionary<string, string?> overrides) : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
        }
    }
}
