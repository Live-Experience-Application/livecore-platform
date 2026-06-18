// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics.Metrics;
using System.Net;
using LiveCore.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// End-to-end wiring tests for the CORE-OBS-007 service-level indicators on the request path — the story's
/// required tests: "a 401/403 increments the auth-failure metric; a 429 increments the rate-limit metric;
/// labels carry no tenant/principal detail". They drive the REAL application over real HTTP and observe the
/// REAL <see cref="LiveCoreMetrics"/> instruments the request-metrics middleware records onto, so the proof is
/// against the genuine pipeline (authentication, authorization and the rate limiter), not a mock.
///
/// The meter name is process-global, so a plain <see cref="MeterListener"/> would also capture another
/// concurrently-running test host's measurements. Each test therefore scopes its listener to THIS host's meter
/// INSTANCE (<see cref="LiveCoreMetrics.Meter"/>), making the assertions deterministic under the suite's
/// parallelism.
/// </summary>
public sealed class SliMetricsEndpointTests
{
    private const string _issuer = TestAuthenticationHandler.DefaultIssuer;

    private const string _methodTag = "http.request.method";
    private const string _routeTag = "http.route";
    private const string _statusTag = "http.response.status_code";

    private static readonly string[] _expectedRequestTagKeys = [_methodTag, _routeTag, _statusTag];

    [Fact]
    public async Task An_anonymous_401_increments_the_auth_failure_metric_with_no_principal_detail()
    {
        await using var factory = new WorkspaceApiFactory();
        using var tap = new MeterTap(factory.Services.GetRequiredService<LiveCoreMetrics>().Meter);
        using var client = factory.CreateAnonymousClient();

        // An anonymous call to a protected route is rejected fail-closed with 401 — counted as an
        // authentication/authorization failure, NOT a server error.
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var measurement = Assert.Single(tap.For("livecore.api.auth.failures"));
        Assert.Equal(_expectedRequestTagKeys.OrderBy(k => k), measurement.Keys.OrderBy(k => k));
        // The route is the low-cardinality TEMPLATE (it has no resource id, so it carries no tenant/principal
        // detail; threat T7) — never the concrete path.
        Assert.Contains("api/v1/me", measurement[_routeTag]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(401, measurement[_statusTag]);
        // The fail-closed 401 is NOT a server error, and it is not a rate-limit rejection.
        Assert.Empty(tap.For("livecore.api.request.errors"));
        Assert.Empty(tap.For("livecore.api.rate_limit.rejections"));
    }

    [Fact]
    public async Task A_forbidden_403_increments_the_auth_failure_metric()
    {
        // A service-account principal holds no user profile, so /api/v1/me denies it fail-closed with 403 — the
        // other half of the auth-failure SLI (a 403 distinct from the 401 above, told apart by the status tag).
        await using var factory = new WorkspaceApiFactory();
        using var tap = new MeterTap(factory.Services.GetRequiredService<LiveCoreMetrics>().Meter);
        using var client = factory.CreateClientFor("svc-sli", _issuer, "northwind-labs");
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var measurement = Assert.Single(tap.For("livecore.api.auth.failures"));
        Assert.Equal(_expectedRequestTagKeys.OrderBy(k => k), measurement.Keys.OrderBy(k => k));
        Assert.Equal(403, measurement[_statusTag]);
        Assert.Empty(tap.For("livecore.api.request.errors"));
    }

    [Fact]
    public async Task A_rate_limited_429_increments_the_rate_limit_metric_with_no_principal_detail()
    {
        const string subject = "sli-rate-limit-user";

        // Per-principal global limit of 1 per window: the first read is admitted, the second crosses the ceiling
        // and is rejected 429 by the rate limiter — counted on the rate-limit SLI, not the error counter.
        await using var factory = new SliRateLimitedFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "1",
            ["RateLimiting:Global:WindowSeconds"] = "60",
        });
        using var tap = new MeterTap(factory.Services.GetRequiredService<LiveCoreMetrics>().Meter);
        using var client = factory.CreateClientFor(subject, _issuer, "northwind-labs");

        var allowed = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var throttled = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        var measurement = Assert.Single(tap.For("livecore.api.rate_limit.rejections"));
        Assert.Equal(_expectedRequestTagKeys.OrderBy(k => k), measurement.Keys.OrderBy(k => k));
        Assert.Equal(429, measurement[_statusTag]);

        // The 429 is not counted as a server error, and the label set carries no tenant/principal detail (T7).
        Assert.Empty(tap.For("livecore.api.request.errors"));
        foreach (var value in measurement.Values)
        {
            Assert.NotEqual(subject, value?.ToString());
            Assert.NotEqual(_issuer, value?.ToString());
        }
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that layers the supplied <c>RateLimiting:*</c> overrides on top of
    /// the production configuration, so a test can drive a deliberately low limit (mirrors the private factory
    /// in <see cref="RateLimitingEndpointTests"/>).
    /// </summary>
    private sealed class SliRateLimitedFactory(IReadOnlyDictionary<string, string?> overrides) : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
        }
    }

    /// <summary>
    /// A <see cref="MeterListener"/> scoped to a single <see cref="Meter"/> instance. It captures the tag map of
    /// every measurement recorded on that meter's instruments, so a test can assert exactly which instrument
    /// fired and with which (non-sensitive) tags — without capturing another parallel host's measurements.
    /// </summary>
    private sealed class MeterTap : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly Lock _gate = new();
        private readonly List<(string Name, IReadOnlyDictionary<string, object?> Tags)> _measurements = [];

        public MeterTap(Meter target)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, target))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument.Name, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument.Name, tags));
            _listener.Start();
        }

        private void Record(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>(tags.Length);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            lock (_gate)
            {
                _measurements.Add((name, map));
            }
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> For(string instrumentName)
        {
            lock (_gate)
            {
                return _measurements
                    .Where(measurement => measurement.Name == instrumentName)
                    .Select(measurement => measurement.Tags)
                    .ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
