// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Assets;

/// <summary>
/// Dependency-injection wiring for the background asset cleanup job (CORE-AST-006). The cleanup job runs in
/// the worker host (docs/02_ARCHITECTURE.md: the worker owns "cleanup" and async jobs), which is a separate
/// process from the API. Rather than have the worker reach into the Assets module's internal types, this
/// PUBLIC extension keeps the wiring inside the module that owns it: the worker calls
/// <see cref="AddAssetCleanup"/> and registers its own scheduling <c>BackgroundService</c>, and the internal
/// repository (<see cref="AssetRepository"/>) and fail-closed storage default (<see cref="UnconfiguredAssetStorage"/>)
/// stay internal.
///
/// Everything the job needs is registered here: the EF Core <see cref="LiveCoreDbContext"/> (PostgreSQL via
/// Npgsql, exactly as the API host registers it), the asset repository, the fail-closed
/// <see cref="IAssetStorage"/> default, the cleanup <see cref="AssetCleanupOptions"/> and a
/// <see cref="TimeProvider"/>, plus the <see cref="ExpiredPendingAssetCleanupService"/> itself. As with the
/// API host, persistence is GATED on a configured connection string: with none, this registers nothing and
/// returns <see langword="false"/>, so the worker runs without a database (and registers no cleanup loop)
/// rather than failing — mirroring how the API host runs without persistence. No storage credentials are
/// read here; the concrete S3-compatible adapter is supplied by the deployment and overrides the fail-closed
/// default (docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006; threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class AssetCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the asset cleanup job's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>). Returns whether persistence — and therefore the cleanup job — is
    /// configured, so the caller can decide whether to register the scheduling background service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Assets:Cleanup</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured and the job's services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddAssetCleanup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no cleanup loop,
            // exactly as the API host runs without persistence (Program.cs). Fail-safe, not fail-open.
            return false;
        }

        // Connection resilience (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on
        // the retrying execution strategy so a transient PostgreSQL disruption is retried automatically instead of
        // surfacing as a worker-job exception, and bounds each command at the configured client CommandTimeout and
        // server statement_timeout — the same policy the API host and the other worker jobs apply.
        services.AddDbContext<LiveCoreDbContext>(options =>
            options.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the cleanup service compares an asset's age against the retention window.
        services.AddSingleton(TimeProvider.System);

        // Storage adapter, selected conditionally on Assets:Storage:* configuration (CORE-OPS-006): the
        // concrete S3-compatible adapter when an endpoint and credentials are configured, so the cleanup job
        // deletes real objects, otherwise the fail-closed UnconfiguredAssetStorage so the sweep removes
        // nothing and never deletes a metadata row whose object it could not delete (CORE-AST-002; threat T4).
        // The same wiring the API host uses, so the two hosts never diverge.
        services.AddAssetStorage(configuration);

        // Deployment cleanup policy (retention/interval/batch size), read once from configuration with safe
        // defaults so the worker runs without any cleanup configuration.
        services.AddSingleton(AssetCleanupOptions.FromConfiguration(configuration));

        // The Assets module's repository and the cleanup application service. Scoped so each sweep runs in
        // its own DbContext scope (the background service creates a scope per run).
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<ExpiredPendingAssetCleanupService>();

        return true;
    }
}
