// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Recaps;

/// <summary>
/// Dependency-injection wiring for the background recap generation job (CORE-JOB-001). The job runs in the
/// worker host (docs/02_ARCHITECTURE.md: the worker owns async jobs), which is a separate process from the
/// API. Like the asset cleanup wiring (<c>AddAssetCleanup</c>), this PUBLIC extension keeps the registration
/// inside the module that owns it: the worker calls <see cref="AddRecapGeneration"/> and registers its own
/// scheduling <c>BackgroundService</c>, while the internal repository and eligibility reader stay internal.
///
/// Everything the job needs is registered here: the EF Core <see cref="LiveCoreDbContext"/> (PostgreSQL via
/// Npgsql, exactly as the API host and the cleanup wiring register it), the recap repository, the eligibility
/// reader, the generation <see cref="RecapGenerationOptions"/> and a <see cref="TimeProvider"/>, plus the
/// <see cref="RecapGenerationService"/> itself. As with the cleanup job, persistence is GATED on a configured
/// connection string: with none, this registers nothing and returns <see langword="false"/>, so the worker
/// runs without a database (and registers no generation loop) rather than failing closed. The shared
/// infrastructure (the <see cref="LiveCoreDbContext"/> and the <see cref="TimeProvider"/>) is registered with
/// TryAdd/AddDbContext semantics so it composes safely when the worker also wires the asset cleanup job in the
/// same container. No credentials or secrets are read here (only the connection string from configuration);
/// none live in this repository (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class RecapGenerationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the recap generation job's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>). Returns whether persistence — and therefore the generation job —
    /// is configured, so the caller can decide whether to register the scheduling background service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Recaps:Generation</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured and the job's services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddRecapGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no generation loop,
            // exactly as the API host and the cleanup job run without persistence. Fail-safe, not fail-open.
            return false;
        }

        // DbContext POOLING (CORE-PERF-003): registered with AddDbContextPool, the same pooled registration the
        // API host uses, so a job sweep reuses a pooled LiveCoreDbContext per scope rather than allocating one
        // each run. AddDbContextPool registers the context and its options with TryAdd semantics, so calling it
        // here and in the asset cleanup wiring (same connection string) is safe — the second call is a no-op.
        // Connection resilience (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on
        // the retrying execution strategy so a transient PostgreSQL disruption is retried automatically instead of
        // surfacing as a worker-job exception, and bounds each command at the configured client CommandTimeout and
        // server statement_timeout — the same policy the API host and the other worker jobs apply.
        services.AddDbContextPool<LiveCoreDbContext>(options =>
            options.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the generation service stamps each recap's produced time from it. TryAdd so it
        // composes with the cleanup wiring's TimeProvider registration (both are TimeProvider.System).
        services.TryAddSingleton(TimeProvider.System);

        // Deployment generation policy (cadence/batch size), read once from configuration with safe defaults so
        // the worker runs without any recap configuration.
        services.AddSingleton(RecapGenerationOptions.FromConfiguration(configuration));

        // The Recaps module's repository, the eligibility reader and the generation application service. Scoped
        // so each sweep runs in its own DbContext scope (the background service creates a scope per run).
        services.AddScoped<IRecapRepository, RecapRepository>();
        services.AddScoped<IRecapEligibleSessionReader, RecapEligibleSessionReader>();

        // The Realtime module's append-only session-event read (CORE-RCP-002): the generation service composes
        // each recap from the session's own event stream. Registered with TryAdd so it composes safely if the
        // worker also wires another module that registers the same repository. The append-only contract exposes
        // only an append and a tenant-/session-scoped read, so this read can never widen an event's audience.
        services.TryAddScoped<ISessionEventRepository, SessionEventRepository>();

        services.AddScoped<RecapGenerationService>();

        return true;
    }
}
