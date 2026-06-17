// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// Health endpoint tests (CORE-FND-004).
///
/// The endpoints are unauthenticated by convention, so these tests also pin
/// the response contract to the minimal status payload: nothing beyond the
/// overall status (no version numbers, configuration values, host names or
/// individual check details) may leak to an anonymous caller.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoint_returns_200_with_healthy_json_status(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoint_exposes_only_the_overall_status(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["status"], propertyNames);

        // No technology or environment fingerprinting headers either.
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoint_response_is_not_cacheable(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        // Probes must always observe the current status, and intermediaries
        // must not retain even the minimal payload.
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control must include no-store.");
    }
}
