// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Exports;

/// <summary>
/// Dependency-injection wiring for the background export processing job (CORE-JOB-002). The job runs in the
/// worker host (docs/02_ARCHITECTURE.md: the worker owns async jobs), which is a separate process from the API.
/// Like the recap generation wiring (<c>AddRecapGeneration</c>) and the asset cleanup wiring
/// (<c>AddAssetCleanup</c>), this PUBLIC extension keeps the registration inside the module that owns it: the
/// worker calls <see cref="AddExportProcessing"/> and registers its own scheduling <c>BackgroundService</c>,
/// while the internal repositories and readers stay internal.
///
/// Everything the job needs is registered here: the EF Core <see cref="LiveCoreDbContext"/> (PostgreSQL via
/// Npgsql, exactly as the API host and the other worker wirings register it), the export job and manifest
/// repositories, the queued-job and workspace-inventory readers, the processing
/// <see cref="ExportProcessingOptions"/> and a <see cref="TimeProvider"/>, plus the
/// <see cref="ExportProcessingService"/> itself. As with the other jobs, persistence is GATED on a configured
/// connection string: with none, this registers nothing and returns <see langword="false"/>, so the worker runs
/// without a database (and registers no processing loop) rather than failing closed. The shared infrastructure
/// (the <see cref="LiveCoreDbContext"/> and the <see cref="TimeProvider"/>) is registered with
/// TryAdd/AddDbContext semantics so it composes safely when the worker also wires the asset cleanup and recap
/// generation jobs in the same container. No credentials or secrets are read here (only the connection string
/// from configuration); none live in this repository (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class ExportProcessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the export processing job's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>). Returns whether persistence — and therefore the processing job — is
    /// configured, so the caller can decide whether to register the scheduling background service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Exports:Processing</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured and the job's services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddExportProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no processing loop,
            // exactly as the API host and the other jobs run without persistence. Fail-safe, not fail-open.
            return false;
        }

        // AddDbContext registers the context and its options with TryAdd semantics, so calling it here and in
        // the other worker job wirings (same connection string) is safe — the later calls are no-ops.
        // Connection resilience (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on
        // the retrying execution strategy so a transient PostgreSQL disruption is retried automatically instead of
        // surfacing as a worker-job exception, and bounds each command at the configured client CommandTimeout and
        // server statement_timeout — the same policy the API host and the other worker jobs apply.
        services.AddDbContext<LiveCoreDbContext>(options =>
            options.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the processing service stamps each transition and manifest from it. TryAdd so it
        // composes with the other jobs' TimeProvider registration (all are TimeProvider.System).
        services.TryAddSingleton(TimeProvider.System);

        // Deployment processing policy (cadence/batch size), read once from configuration with safe defaults so
        // the worker runs without any export configuration.
        services.AddSingleton(ExportProcessingOptions.FromConfiguration(configuration));

        // The Exports module's repositories, the queued-job and workspace-inventory readers and the processing
        // application service. Scoped so each sweep runs in its own DbContext scope (the background service
        // creates a scope per run).
        services.AddScoped<IExportJobRepository, ExportJobRepository>();
        services.AddScoped<IExportManifestRepository, ExportManifestRepository>();
        services.AddScoped<IQueuedExportJobReader, QueuedExportJobReader>();
        services.AddScoped<IWorkspaceExportInventoryReader, WorkspaceExportInventoryReader>();
        services.AddScoped<ExportProcessingService>();

        return true;
    }
}
