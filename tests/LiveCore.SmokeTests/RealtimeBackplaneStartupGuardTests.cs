// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// Realtime backplane topology-guard tests (CORE-RES-006) over the real production <c>Program</c> with no test
/// seams: only the environment and the realtime topology settings are configured.
///
/// They pin the acceptance criterion — the app-side defence in depth for the misconfiguration CORE-DEP-009 made
/// the Helm chart refuse to render. When the API is configured for more than one instance with no realtime
/// backplane, the host REFUSES TO START with a clear, named error (the in-process backplane would silently drop
/// cross-instance delivery; docs/11_REALTIME_SYNC.md "Scale-out"), exactly as the OIDC audience guard refuses to
/// start (CORE-OPS-004). A single-instance run (the default) starts normally — a single-instance development run
/// is unaffected — and a configured backplane is silent for every topology (the multi-instance + real backplane
/// path is exercised by <c>RedisBackplanePropagationTests</c> when a backplane server is available).
/// </summary>
public class RealtimeBackplaneStartupGuardTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        string? instanceCount)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);

            if (instanceCount is not null)
            {
                builder.UseSetting(RealtimeBackplaneStartupValidator.InstanceCountKey, instanceCount);
            }
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Host_refuses_to_start_when_more_than_one_instance_is_configured_without_a_backplane(string environment)
    {
        // More than one declared instance with no backplane is broken in any environment, so the guard fails fast
        // before the host is built — irrespective of whether the host is in Production.
        using var factory = CreateFactory(environment, instanceCount: "2");

        // Touching Services builds the real host, which runs the realtime topology guard.
        var exception = Record.Exception(() => _ = factory.Services);

        Assert.NotNull(exception);
        var flattened = Flatten(exception!);
        // The error names the misconfiguration and points at the keys to fix — never any connection string value.
        Assert.Contains("backplane", flattened, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RealtimeBackplaneStartupValidator.InstanceCountKey, flattened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_starts_for_a_single_instance_without_a_backplane()
    {
        // The default single-instance development run is unaffected: the host starts and serves liveness with no
        // backplane and no instance-count setting.
        using var factory = CreateFactory("Development", instanceCount: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_starts_for_an_explicit_single_instance_without_a_backplane()
    {
        // An explicit single instance is also fine — the guard only fails fast for more than one instance.
        using var factory = CreateFactory("Development", instanceCount: "1");
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
