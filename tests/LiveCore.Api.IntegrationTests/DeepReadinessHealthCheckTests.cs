// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the deep dependency reachability readiness gate (CORE-OBS-009) over the real
/// <c>Program</c>. They pin the acceptance criteria end-to-end:
/// <list type="bullet">
///   <item><c>/health/ready</c> reports Healthy when every configured critical dependency probe is reachable, and
///   Unhealthy when ANY is unreachable — so a host with a healthy database but a dead critical dependency leaves
///   the ready rotation;</item>
///   <item>liveness (<c>/health/live</c>) is UNAFFECTED — a dead dependency never restarts an otherwise-live
///   process (liveness stays shallow/separate);</item>
///   <item>the probes honor the short timeout (CORE-RES-005) and fail closed — a hung probe is bounded and counted
///   not-ready;</item>
///   <item>the unauthenticated readiness response stays status-only, so which dependency is unreachable never
///   leaks (threat T7).</item>
/// </list>
///
/// The reachable/one-down/hung matrix is driven through fake probes substituted into the real readiness check, so
/// it is deterministic and needs no external infrastructure. Three further tests exercise the REAL probes
/// (OIDC/storage/backplane) against unreachable endpoints, proving the production wiring itself fails closed and
/// stays bounded. No credential lives in source: the endpoints are loopback / RFC 5737 reserved addresses and the
/// keys are throwaway fakes (threat T7). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class DeepReadinessHealthCheckTests
{
    // Short bound so the timeout/unreachable tests stay fast while still being well under the dependencies'
    // unbounded SDK defaults (the OIDC backchannel 30s, the AWS SDK 100s).
    private const string _shortProbeTimeout = "00:00:01";

    private static WebApplicationFactory<Program> CreateFactory(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting($"{DependencyReadinessOptions.ConfigurationSection}:{DependencyReadinessOptions.ProbeTimeoutKey}", _shortProbeTimeout);

            if (settings is not null)
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            }

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
    }

    private static void UseProbes(IServiceCollection services, params IDependencyReadinessProbe[] probes)
    {
        // Replace whatever probe set the host configured with exactly these, so the matrix is deterministic.
        services.RemoveAll<IDependencyReadinessProbe>();
        foreach (var probe in probes)
        {
            services.AddSingleton(probe);
        }
    }

    [Fact]
    public async Task Readiness_is_healthy_when_every_configured_dependency_is_reachable()
    {
        using var factory = CreateFactory(configureServices: services => UseProbes(
            services,
            FakeReadinessProbe.Reachable("oidc-discovery"),
            FakeReadinessProbe.Reachable("object-storage"),
            FakeReadinessProbe.Reachable("realtime-backplane")));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_a_configured_dependency_is_unreachable()
    {
        using var factory = CreateFactory(configureServices: services => UseProbes(
            services,
            FakeReadinessProbe.Reachable("oidc-discovery"),
            FakeReadinessProbe.Unreachable("object-storage"),
            FakeReadinessProbe.Reachable("realtime-backplane")));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Status-only: which dependency is unreachable never leaks to the unauthenticated endpoint (threat T7).
        Assert.Equal("""{"status":"Unhealthy"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Liveness_is_unaffected_when_a_configured_dependency_is_unreachable()
    {
        using var factory = CreateFactory(configureServices: services => UseProbes(
            services,
            FakeReadinessProbe.Unreachable("object-storage")));
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        // A dead dependency takes the host out of the ready rotation but must NEVER restart an otherwise-live
        // process: liveness stays shallow and 200.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_honors_the_short_timeout_when_a_probe_hangs()
    {
        using var factory = CreateFactory(configureServices: services => UseProbes(
            services,
            FakeReadinessProbe.Reachable("oidc-discovery"),
            FakeReadinessProbe.Hangs("realtime-backplane")));
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready");
        stopwatch.Stop();

        // Fail-closed and bounded: the hung probe is counted not-ready and the readiness response returns within
        // the short timeout rather than hanging (CORE-RES-005).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Readiness should have been bounded by the short probe timeout, but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_the_configured_oidc_authority_is_unreachable()
    {
        // The REAL OIDC discovery probe against an unreachable authority. An HTTPS authority is used so the bearer
        // pipeline accepts it (an http authority needs RequireHttpsMetadata=false), while loopback port 1 refuses
        // the discovery GET immediately so the probe fails closed.
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://127.0.0.1:1",
        });
        using var client = factory.CreateClient();

        await AssertReadyUnhealthyAndLivenessUp(client);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_configured_object_storage_is_unreachable()
    {
        // The REAL object-storage probe against an unreachable backend (192.0.2.1 is RFC 5737 TEST-NET-1,
        // guaranteed non-routable), bounded by the short readiness timeout.
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "http://192.0.2.1:9000",
            ["Assets:Storage:AccessKeyId"] = "test-access-key",
            ["Assets:Storage:SecretAccessKey"] = "test-secret-key",
            ["Assets:Storage:MaxErrorRetry"] = "0",
        });
        using var client = factory.CreateClient();

        await AssertReadyUnhealthyAndLivenessUp(client);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_the_configured_realtime_backplane_is_unreachable()
    {
        // The REAL realtime backplane probe against an unreachable Redis/Valkey endpoint (RFC 5737 TEST-NET-1),
        // bounded by the short readiness timeout.
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = "192.0.2.1:6379",
        });
        using var client = factory.CreateClient();

        await AssertReadyUnhealthyAndLivenessUp(client);
    }

    private static async Task AssertReadyUnhealthyAndLivenessUp(HttpClient client)
    {
        var stopwatch = Stopwatch.StartNew();
        var ready = await client.GetAsync("/health/ready");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", await ready.Content.ReadAsStringAsync());

        // Bounded well under the dependencies' unbounded SDK defaults, so a dead dependency fails fast.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"The unreachable dependency probe should have failed fast, but readiness took {stopwatch.Elapsed}.");

        // Liveness stays shallow and unaffected.
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private sealed class FakeReadinessProbe : IDependencyReadinessProbe
    {
        private readonly Func<CancellationToken, Task> _probe;

        private FakeReadinessProbe(string dependencyName, Func<CancellationToken, Task> probe)
        {
            DependencyName = dependencyName;
            _probe = probe;
        }

        public string DependencyName { get; }

        public Task ProbeAsync(CancellationToken cancellationToken) => _probe(cancellationToken);

        public static FakeReadinessProbe Reachable(string name) => new(name, _ => Task.CompletedTask);

        public static FakeReadinessProbe Unreachable(string name) =>
            new(name, _ => throw new InvalidOperationException($"{name} is unreachable"));

        public static FakeReadinessProbe Hangs(string name) =>
            new(name, token => Task.Delay(Timeout.InfiniteTimeSpan, token));
    }
}
