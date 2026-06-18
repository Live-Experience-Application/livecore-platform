// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Startup well-formedness validation tests (CORE-OPS-013) over the real production <c>Program</c> with no test
/// seams: only the environment and one malformed critical value are configured.
///
/// They pin the acceptance criterion's posture, which REUSES the existing missing-value contract (CORE-OPS-008)
/// and not-ready gate (CORE-OPS-005) rather than adding a new one: a Production host with a present-but-malformed
/// CORS origin still STARTS (a malformed value never crashes an otherwise-live process), reports **not-ready** —
/// the malformed value leaves the ready rotation exactly as a missing one does — and logs a loud, named
/// <c>Critical</c> startup error that carries only the offending KEY name, never the configured value (threat T7
/// in docs/07_SECURITY_THREAT_MODEL.md). Outside Production the check is inert (local-development latitude).
/// </summary>
public sealed class MalformedConfigurationContractTests
{
    // A present-but-malformed CORS origin: a real path makes it not a bare scheme+host[+port] origin. Distinctive
    // enough to assert it is NEVER written to the log.
    private const string _malformedOrigin = "https://app.example.com/this/is/a/path";

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        CapturingLoggerProvider logs)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting($"{ProductionConfigurationValidator.CorsAllowedOriginsKey}:0", _malformedOrigin);

            // Capture the startup log alongside the production providers, exactly as the logging-level tests do,
            // so the loud Critical startup line is observable end-to-end.
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
        });
    }

    [Fact]
    public async Task Production_host_with_a_malformed_cors_origin_reports_not_ready_and_logs_the_key_name_only()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = CreateFactory("Production", logs);

        // Building the client starts the real host, which runs the startup well-formedness validation and its log.
        using var client = factory.CreateClient();

        // It still STARTS and stays live: a malformed value never crashes an otherwise-live process.
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // It reports NOT-READY, status-only (which value is malformed never leaks to the unauthenticated endpoint).
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", await ready.Content.ReadAsStringAsync());

        // Isolate the cause: the malformed-configuration readiness check itself reports not-ready (so the 503 is
        // attributable to the malformed CORS origin, not only to the unconfigured persistence/OIDC).
        var report = await factory.Services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(
                registration => registration.Name == ProductionConfigurationValidator.MalformedConfigurationCheckName);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);

        // The loud, named startup diagnostic names the offending KEY only, never the malformed value (threat T7).
        var criticalMessages = logs.Entries
            .Where(entry => entry.Level == LogLevel.Critical)
            .Select(entry => entry.Message)
            .ToList();
        Assert.Contains(
            criticalMessages,
            message => message.Contains(ProductionConfigurationValidator.CorsAllowedOriginsKey, StringComparison.Ordinal));
        Assert.DoesNotContain(
            criticalMessages,
            message => message.Contains(_malformedOrigin, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outside_production_a_malformed_cors_origin_is_tolerated_and_not_gated()
    {
        // Local-development latitude: a Development host with the same malformed origin is not gated and emits no
        // Critical configuration diagnostic — the same inert posture the missing-value contract and readiness gate
        // grant outside Production.
        var logs = new CapturingLoggerProvider();
        using var factory = CreateFactory("Development", logs);
        using var client = factory.CreateClient();

        var report = await factory.Services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(
                registration => registration.Name == ProductionConfigurationValidator.MalformedConfigurationCheckName);
        Assert.Equal(HealthStatus.Healthy, report.Status);

        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Critical);
    }
}
