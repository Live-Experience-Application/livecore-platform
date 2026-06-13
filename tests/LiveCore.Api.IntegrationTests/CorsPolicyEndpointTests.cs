using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the CORS allow-list applied to browser/PWA clients (CORE-OPS-003). They
/// drive the REAL application over real HTTP and assert the acceptance criteria: a browser preflight from
/// an allowed origin succeeds (the response carries <c>Access-Control-Allow-Origin</c> for that origin),
/// a preflight from a disallowed origin is rejected (no such header), the SAME allow-list governs the
/// <c>/hubs</c> SignalR endpoint, and the default is FAIL-CLOSED (with no configured origins every
/// cross-origin request is denied).
///
/// CORS is a browser-enforced boundary layered on the OIDC/tenant authorization every endpoint already
/// applies; a preflight is an unauthenticated <c>OPTIONS</c> request, so the CORS middleware answers it
/// before the authentication challenge — which is why these run without seeding a database or a token.
/// </summary>
public sealed class CorsPolicyEndpointTests
{
    private const string _allowedOrigin = "https://app.allowed.example";
    private const string _disallowedOrigin = "https://evil.example";

    private static WebApplicationFactory<Program> CreateFactory(params string[] allowedOrigins)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var overrides = new Dictionary<string, string?>();
                for (var i = 0; i < allowedOrigins.Length; i++)
                {
                    overrides[$"{CorsConfiguration.ConfigurationSection}:{CorsConfiguration.AllowedOriginsKey}:{i}"] =
                        allowedOrigins[i];
                }

                configuration.AddInMemoryCollection(overrides);
            }));
    }

    private static HttpRequestMessage Preflight(string path, string origin, string requestedMethod = "GET")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", requestedMethod);
        return request;
    }

    [Fact]
    public async Task Preflight_from_an_allowed_origin_is_permitted_on_the_rest_api()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/api/v1/me", _allowedOrigin));

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values),
            "An allowed origin's preflight must carry Access-Control-Allow-Origin.");
        Assert.Equal(_allowedOrigin, Assert.Single(values));
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_is_rejected_on_the_rest_api()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/api/v1/me", _disallowedOrigin));

        // The disallowed origin gets no Access-Control-Allow-Origin header, so a browser blocks the call.
        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "A disallowed origin must not receive Access-Control-Allow-Origin.");
    }

    [Fact]
    public async Task Preflight_from_an_allowed_origin_is_permitted_on_the_signalr_hub()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        // The same allow-list governs the /hubs SignalR endpoint (RequireCors on the hub mapping).
        using var response = await client.SendAsync(Preflight("/hubs/session", _allowedOrigin, "POST"));

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values),
            "The hub preflight from an allowed origin must carry Access-Control-Allow-Origin.");
        Assert.Equal(_allowedOrigin, Assert.Single(values));
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_is_rejected_on_the_signalr_hub()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/hubs/session", _disallowedOrigin, "POST"));

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "A disallowed origin must not receive Access-Control-Allow-Origin on the hub.");
    }

    [Fact]
    public async Task Actual_request_from_an_allowed_origin_carries_the_cors_header()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        // A real (non-preflight) cross-origin GET against the anonymous health endpoint.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", _allowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values),
            "An allowed origin's actual request must carry Access-Control-Allow-Origin.");
        Assert.Equal(_allowedOrigin, Assert.Single(values));
    }

    [Fact]
    public async Task Default_is_fail_closed_when_no_origins_are_configured()
    {
        // No configured origins (the repository default): every cross-origin request is denied.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/api/v1/me", _allowedOrigin));

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "With no configured origins the fail-closed default must allow no origin.");
    }

    [Fact]
    public async Task Configured_policy_allows_credentialed_requests_for_an_allowed_origin()
    {
        await using var factory = CreateFactory(_allowedOrigin);
        using var client = factory.CreateClient();

        var preflight = Preflight("/hubs/session", _allowedOrigin, "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var response = await client.SendAsync(preflight);

        // A browser SignalR client connects with credentials; the explicit allow-list (never a wildcard)
        // makes Access-Control-Allow-Credentials valid.
        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var values),
            "A credentialed cross-origin client needs Access-Control-Allow-Credentials.");
        Assert.Equal("true", Assert.Single(values));
    }
}
