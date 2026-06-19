// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace LiveCore.Worker;

/// <summary>
/// The worker's production secret-management and configuration backstop (CORE-OBS-013, the "Worker and Health
/// Observability Completeness" epic) — the worker counterpart of the API's
/// <see cref="ProductionConfigurationValidator"/> (CORE-OPS-008 / CORE-OPS-013).
///
/// <para>
/// The worker had no equivalent of the API's startup configuration contract: a worker deployed to Production
/// with no database (or a present-but-malformed connection string) ran every loop silently disabled / failing
/// with no loud, named diagnostic. This is the single, testable list of the settings a correct PRODUCTION
/// worker must supply, plus the pure decisions reporting which are missing or malformed. Every value is supplied
/// at runtime from configuration only — no credential lives in source (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
///
/// It pairs with the worker readiness gate (<see cref="WorkerReadiness"/>): the readiness gate reports NOT-READY
/// when persistence is unconfigured, while this reports the SAME not-ready posture for a present-but-malformed
/// critical value and logs a loud, NAMED <c>Critical</c> startup line (<see cref="WorkerHostFactory"/>) so the
/// misconfiguration is unmissable. It reuses the same environment-aware, fail-closed posture as the API
/// contract rather than adding a new one:
/// <list type="bullet">
/// <item>Outside Production the contract is inert (an empty list), preserving the local-development latitude the
/// host already grants — a Development worker with no database still starts.</item>
/// <item>In Production a missing/malformed value does NOT crash the worker: the host stays up, schedules no loop
/// it cannot back, and reports NOT-READY (<see cref="WorkerReadiness"/>), exactly as the API stays up and fails
/// closed.</item>
/// </list>
///
/// The worker's required/critical set is NARROWER than the API's: the worker serves no authenticated request, so
/// it requires NO OIDC Authority/Audience and consumes no CORS/forwarded-headers configuration — its one
/// always-required production value is the database connection string the job loops are gated on. The decisions
/// report only the missing/malformed KEY NAMES, never any configured value, so they can be logged without
/// leaking a secret (threat T7) — the same reason the readiness response stays status-only.
/// </summary>
public static class WorkerProductionConfiguration
{
    /// <summary>
    /// Configuration key of the PostgreSQL connection string (<c>ConnectionStrings__Database</c>) — the SAME key
    /// the API validates (reused from <see cref="ProductionConfigurationValidator.DatabaseConnectionStringKey"/>
    /// so the two hosts can never disagree on the name). It carries the database password, so it is injected from
    /// the deployment's secret store only (docs/13_SELF_HOSTING_REQUIREMENTS.md).
    /// </summary>
    public const string DatabaseConnectionStringKey = ProductionConfigurationValidator.DatabaseConnectionStringKey;

    /// <summary>
    /// The configuration keys that MUST be present for a correct production WORKER deployment. The worker's job
    /// loops are all gated on a configured database, so the connection string is its one always-required value;
    /// the conditionally-required secret groups (object storage, the store adapters) are required only for the
    /// features that use them and stay fail-closed when unset, so they are documented in the contract
    /// (<c>.env.example</c>, docs/13) rather than hard-required here.
    /// </summary>
    public static IReadOnlyList<string> RequiredProductionSettings { get; } = [DatabaseConnectionStringKey];

    /// <summary>
    /// The single, pure decision behind the worker startup configuration contract: in a Production environment,
    /// which of the <see cref="RequiredProductionSettings"/> are absent (null or whitespace). Pure (no services,
    /// no configuration side effects) so the behavior is unit-testable directly, mirroring
    /// <see cref="ProductionConfigurationValidator.FindMissingRequiredSettings"/>.
    /// </summary>
    /// <param name="configuration">The host configuration the required values are read from.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <returns>
    /// The required setting keys that are missing, in declared order. Empty outside Production (the contract is
    /// inert, preserving local-development latitude) and empty in Production when every required value is
    /// present. Only the KEY NAMES are returned, never the configured values (threat T7).
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> FindMissingRequiredSettings(
        IConfiguration configuration,
        bool isProductionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Outside Production the contract is inert, the same local-development latitude the readiness gate
        // (CORE-OBS-013) and the API contract (CORE-OPS-008) grant: a dev worker without a database still starts.
        if (!isProductionEnvironment)
        {
            return [];
        }

        var missing = new List<string>();
        foreach (var key in RequiredProductionSettings)
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    /// <summary>
    /// Name the malformed-configuration readiness check is registered under. Kept server-side (the readiness
    /// endpoint writes only the overall status), never leaked to the unauthenticated response (threat T7).
    /// </summary>
    public const string MalformedConfigurationCheckName = "worker-configuration-well-formed";

    /// <summary>
    /// The pure decision behind the worker startup well-formedness contract (CORE-OBS-013): in a Production
    /// environment, which of the critical values that can be validated WITHOUT I/O are PRESENT but malformed.
    /// Pure (no services, no configuration side effects) so the behavior is unit-testable directly, exactly like
    /// <see cref="FindMissingRequiredSettings"/> and <see cref="ProductionConfigurationValidator.FindMalformedCriticalSettings"/>.
    ///
    /// <para>
    /// A value is checked only when it is PRESENT (non-blank): an absent value is the missing-value contract's
    /// concern (<see cref="FindMissingRequiredSettings"/>), so the two diagnostics never double-report the same
    /// key. The worker's only critical value that can be decided locally with no network call AND that is not
    /// already rejected at host build is the database connection string — the metrics listen URL
    /// (<see cref="WorkerMetricsOptions.FromConfiguration"/>) and the heartbeat staleness threshold
    /// (<see cref="WorkerHeartbeatOptions.FromConfiguration"/>) already throw at startup on a malformed value, so
    /// they need no readiness re-check, whereas a present-but-malformed connection string is registered with EF
    /// Core lazily and would otherwise surface only on the first sweep:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ConnectionStrings:Database</c> — parses as a PostgreSQL connection string.</item>
    /// </list>
    /// </summary>
    /// <param name="configuration">The host configuration the critical values are read from.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <returns>
    /// The malformed critical setting keys, in declared order. Empty outside Production (the contract is inert,
    /// preserving local-development latitude) and empty in Production when every present critical value is
    /// well-formed. Only the KEY NAMES are returned, never the configured values (threat T7).
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> FindMalformedCriticalSettings(
        IConfiguration configuration,
        bool isProductionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Outside Production the contract is inert, the same local-development latitude the missing-value
        // contract grants: a dev worker with a half-finished value still starts and fails closed.
        if (!isProductionEnvironment)
        {
            return [];
        }

        var malformed = new List<string>();

        var connectionString = configuration[DatabaseConnectionStringKey];
        if (!string.IsNullOrWhiteSpace(connectionString) && !IsParseableConnectionString(connectionString))
        {
            malformed.Add(DatabaseConnectionStringKey);
        }

        return malformed;
    }

    /// <summary>
    /// Registers the worker's malformed-configuration readiness check (tagged
    /// <see cref="WorkerHealthEndpoints.ReadinessTag"/>) so <c>/health/ready</c> reports NOT-READY in Production
    /// when the connection string is present but malformed — the same not-ready posture a MISSING/unconfigured
    /// dependency already produces (<see cref="WorkerReadiness"/>). Configuration is fixed at startup, so the
    /// well-formedness verdict is captured here once rather than re-evaluated on every probe. Registered
    /// UNCONDITIONALLY (independent of whether persistence is configured) so it runs even when persistence is
    /// absent, and the readiness response stays status-only so WHICH key is malformed never leaks (threat T7).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration the critical values are read from.</param>
    /// <param name="environment">The host environment (the check is environment-aware: inert outside Production).</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddMalformedWorkerConfigurationReadinessCheck(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var hasMalformedConfiguration =
            FindMalformedCriticalSettings(configuration, environment.IsProduction()).Count > 0;

        services.AddHealthChecks().AddCheck(
            MalformedConfigurationCheckName,
            new WorkerMalformedConfigurationHealthCheck(hasMalformedConfiguration),
            tags: [WorkerHealthEndpoints.ReadinessTag]);

        return services;
    }

    private static bool IsParseableConnectionString(string value)
    {
        try
        {
            // NpgsqlConnectionStringBuilder is the same parser the persistence layer (and the API's
            // ProductionConfigurationValidator) uses; constructing it rejects an unparseable string, an unknown
            // keyword or a value that cannot be coerced to its typed keyword (e.g. a non-numeric Port).
            _ = new NpgsqlConnectionStringBuilder(value);
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> behind
/// <see cref="WorkerProductionConfiguration.AddMalformedWorkerConfigurationReadinessCheck"/> (CORE-OBS-013). It
/// captures the startup-time well-formedness verdict and reports NOT-READY (Unhealthy) when the worker's
/// connection string is present but malformed, so a malformed value leaves the ready rotation exactly as an
/// unconfigured dependency does (<see cref="WorkerReadiness"/>). The failure description stays generic and
/// server-side (the readiness endpoint writes only the overall status), so it never tells an anonymous caller
/// WHICH key is malformed (threat T7).
/// </summary>
internal sealed class WorkerMalformedConfigurationHealthCheck : IHealthCheck
{
    private readonly bool _hasMalformedConfiguration;

    public WorkerMalformedConfigurationHealthCheck(bool hasMalformedConfiguration)
        => _hasMalformedConfiguration = hasMalformedConfiguration;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_hasMalformedConfiguration
            ? HealthCheckResult.Unhealthy("A critical worker production configuration value is present but malformed.")
            : HealthCheckResult.Healthy());
}
