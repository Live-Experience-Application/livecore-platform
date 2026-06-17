// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the API deprecation/sunset mechanism (CORE-DX-006). They drive a real host over
/// real HTTP through the SAME public mechanism the API host wires (<see cref="ApiDeprecation.WithDeprecation"/>
/// on the endpoint plus <see cref="ApiDeprecation.UseLiveCoreDeprecationHeaders"/> in the pipeline) and assert
/// the acceptance criterion: a route flagged deprecated returns the RFC 8594 <c>Sunset</c> header and the
/// <c>Deprecation</c> header, while a current (non-deprecated) route returns neither. No route in the product is
/// deprecated yet — the convention and mechanism are exercised here against a deprecated probe route so the
/// behavior is proven ahead of the first real deprecation. The CORS exposure of these headers is covered by
/// <see cref="CorsPolicyEndpointTests"/> (it asserts every header in
/// <see cref="CorsConfiguration.ExposedResponseHeaders"/> is exposed cross-origin).
/// </summary>
public sealed class ApiDeprecationEndpointTests
{
    private static readonly DateTimeOffset _sunsetAt = new(2031, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset _deprecatedSince = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<IHost> StartHostAsync()
    {
        var notice = new DeprecationNotice(_sunsetAt, _deprecatedSince);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services => services.AddRouting());
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    // The production wiring: the deprecation header middleware runs after routing has selected
                    // the endpoint, so a flagged endpoint's notice is emitted.
                    app.UseLiveCoreDeprecationHeaders();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/deprecated", () => Results.Ok()).WithDeprecation(notice);
                        endpoints.MapGet("/current", () => Results.Ok());
                    });
                });
            })
            .StartAsync();

        return host;
    }

    [Fact]
    public async Task A_deprecated_route_returns_the_deprecation_and_sunset_headers()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri("/deprecated", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(
            response.Headers.TryGetValues("Deprecation", out var deprecation),
            "A deprecated route must carry the Deprecation header.");
        Assert.Equal(
            _deprecatedSince,
            DateTimeOffset.ParseExact(Assert.Single(deprecation), "R", CultureInfo.InvariantCulture));

        Assert.True(
            response.Headers.TryGetValues("Sunset", out var sunset),
            "A deprecated route must carry the RFC 8594 Sunset header.");
        Assert.Equal(
            _sunsetAt,
            DateTimeOffset.ParseExact(Assert.Single(sunset), "R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_current_route_returns_no_deprecation_or_sunset_headers()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri("/current", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The signal is strictly opt-in: a route that is not flagged deprecated must not carry the headers.
        Assert.False(response.Headers.Contains("Deprecation"), "A current route must not carry Deprecation.");
        Assert.False(response.Headers.Contains("Sunset"), "A current route must not carry Sunset.");
    }
}
