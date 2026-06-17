// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Exports;
using LiveCore.Api.Persistence;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Retention;

/// <summary>
/// Dependency-injection wiring for the background data-retention sweep (CORE-PRIV-003). The sweep runs in the
/// worker host (docs/02_ARCHITECTURE.md: the worker owns "cleanup" and async jobs), a separate process from the
/// API. Like the asset cleanup, recap generation and export processing wirings, this PUBLIC extension keeps the
/// registration inside the assembly that owns the modules it spans: the worker calls
/// <see cref="AddDataRetention"/> and registers its own scheduling <c>BackgroundService</c>, while the module
/// repositories and the audit log repository stay internal.
///
/// Everything the sweep needs is registered here: the EF Core <see cref="LiveCoreDbContext"/> (PostgreSQL via
/// Npgsql, exactly as the API host and the other worker wirings register it), the module repositories whose
/// records the sweep purges (including the System module's idempotency-key store, CORE-PRIV-006), the audit log
/// repository, the <see cref="TransactionalUnitOfWork"/> that makes each
/// audit-append-plus-delete atomic, the <see cref="IAssetStorage"/> adapter (for the export blob delete), the
/// <see cref="DataRetentionOptions"/> policy and a <see cref="TimeProvider"/>, plus the
/// <see cref="DataRetentionSweepService"/> itself. As with the other jobs, persistence is GATED on a configured
/// connection string: with none, this registers nothing and returns <see langword="false"/>, so the worker runs
/// without a database (and registers no retention loop) rather than failing closed. The shared infrastructure
/// (the DbContext, the TimeProvider, the storage adapter and the repositories) is registered with
/// TryAdd/AddDbContext semantics, so wiring this alongside the other worker jobs in the same container composes
/// safely. No credentials or secrets are read here (only the connection string and the retention policy);
/// storage credentials stay inside the deployment-supplied adapter (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat
/// T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class DataRetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-retention sweep's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>). Returns whether persistence — and therefore the sweep — is configured,
    /// so the caller can decide whether to register the scheduling background service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Retention</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured and the sweep's services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddDataRetention(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no retention loop,
            // exactly as the API host and the other jobs run without persistence. Fail-safe, not fail-open.
            return false;
        }

        // DbContext POOLING (CORE-PERF-003): registered with AddDbContextPool, the same pooled registration the
        // API host uses. AddDbContextPool registers the context and its options with TryAdd semantics, so calling
        // it here and in the other worker job wirings (same connection string) is safe. Connection resilience
        // (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on the retrying execution
        // strategy and bounds each command at the configured client CommandTimeout and server statement_timeout —
        // the same policy the API host and the other worker jobs apply, and the one TransactionalUnitOfWork runs under.
        services.AddDbContextPool<LiveCoreDbContext>(options =>
            options.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the sweep computes each window's cutoff from it. TryAdd so it composes with the other
        // jobs' TimeProvider registration (all are TimeProvider.System).
        services.TryAddSingleton(TimeProvider.System);

        // The storage adapter (the concrete S3-compatible adapter when configured, else the fail-closed default),
        // selected exactly as the API host and the asset cleanup job select it, so the export blob delete uses the
        // same delete path. The export sweep keeps the row when storage is unconfigured (fail-closed; threat T4).
        services.AddAssetStorage(configuration);

        // Deployment retention policy (per-family enable flags + windows, cadence, batch size), read once from
        // configuration with safe defaults so the worker runs without any retention configuration.
        services.AddSingleton(DataRetentionOptions.FromConfiguration(configuration));

        // The owning modules' repositories, the audit log repository and the transactional unit of work. Scoped so
        // each sweep runs in its own DbContext scope (the background service creates a scope per run); TryAdd so
        // they compose with the other worker jobs that register some of the same repositories.
        services.TryAddScoped<ISessionRepository, SessionRepository>();
        services.TryAddScoped<IRecapRepository, RecapRepository>();
        services.TryAddScoped<IExportJobRepository, ExportJobRepository>();
        services.TryAddScoped<IWorkspaceInvitationRepository, WorkspaceInvitationRepository>();
        // The System module's idempotency-key store, whose CORE-PRIV-006 bulk purge bounds the otherwise
        // insert-only idempotency_keys table.
        services.TryAddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();
        services.TryAddScoped<IAuditLogRepository, AuditLogRepository>();
        services.TryAddScoped<TransactionalUnitOfWork>();
        services.TryAddScoped<DataRetentionSweepService>();

        return true;
    }
}
