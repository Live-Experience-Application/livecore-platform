// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LiveCore.SmokeTests;

/// <summary>
/// Smoke tests for the API host skeleton (CORE-FND-001).
/// They verify that the application composes and serves HTTP in-memory.
/// </summary>
public class ApiSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiSmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ApiHost_starts_and_responds()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        // The host intentionally exposes no root endpoint (only the health
        // endpoints from CORE-FND-004 exist, covered by HealthEndpointTests),
        // so the pipeline must answer with 404 rather than failing to start.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ApiHost_resolves_core_services()
    {
        Assert.NotNull(_factory.Services.GetService(typeof(IHostEnvironment)));
    }
}
