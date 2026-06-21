// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.SystemModule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Dependency-injection wiring for the background scheduled-reveal sweep job (CORE-VSEAL-002). The job runs in
/// the worker host (docs/02_ARCHITECTURE.md: the worker owns async jobs), a separate process from the API. Like
/// the recap-generation wiring (<c>AddRecapGeneration</c>), this PUBLIC extension keeps the registration inside
/// the module that owns it: the worker calls <see cref="AddScheduledReveal"/> and registers its own scheduling
/// <c>BackgroundService</c>, while the central reveal command (<see cref="RevealService"/>), the due reader and
/// the repositories stay internal. It registers everything the sweep needs to drive the SAME central reveal
/// command as the API — the EF Core <see cref="LiveCoreDbContext"/>, the visibility-rule repository, the
/// idempotency store, the audit repository, the append-only session-event stream, the unit of work, the reveal
/// command, the due reader and the <see cref="ScheduledRevealService"/> — so the auto-reveal is never a
/// duplicated reveal path.
///
/// GATED on a configured connection string AND an explicit opt-in. Server-enforced scheduled reveal is OFF BY
/// DEFAULT (the story's off-by-default requirement): like the billing-gated store-reconciliation job, this returns
/// <see langword="false"/> and registers nothing unless BOTH a database is configured AND
/// <c>Visibility:ScheduledReveal:Enabled=true</c>. So a deployment that does not use scheduled reveals runs no
/// sweep at all (fail-safe, not fail-open). The shared infrastructure (the <see cref="LiveCoreDbContext"/>, the
/// <see cref="TimeProvider"/> and the shared repositories) is registered with TryAdd/AddDbContextPool semantics
/// so it composes safely when the worker also wires the other jobs in the same container. No credentials or
/// secrets are read here (only the connection string and the timing policy); none live in this repository
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class ScheduledRevealServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scheduled-reveal sweep job's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>) AND the job is enabled (<c>Visibility:ScheduledReveal:Enabled=true</c>).
    /// Returns whether the job — and therefore the scheduling background service — should be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Visibility:ScheduledReveal</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured, the job is enabled, and its services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddScheduledReveal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no sweep, exactly as the
            // other jobs run without persistence. Fail-safe, not fail-open.
            return false;
        }

        var options = ScheduledRevealOptions.FromConfiguration(configuration);
        if (!options.Enabled)
        {
            // OFF BY DEFAULT: server-enforced scheduled reveal is opt-in per deployment (like the billing-gated
            // store-reconciliation job). Not enabled -> register no sweep.
            return false;
        }

        // DbContext POOLING (CORE-PERF-003), connection resilience (CORE-CONC-003) and per-command timeouts
        // (CORE-RES-004) via UseLiveCoreNpgsql — the same pooled, resilient registration the API host and the
        // other worker jobs use. AddDbContextPool registers the context and options with TryAdd semantics, so
        // calling it here and in another job's wiring (same connection string) is safe (the second call is a
        // no-op). The audit tamper-protection interceptor is wired by UseLiveCoreNpgsql.
        services.AddDbContextPool<LiveCoreDbContext>(options2 =>
            options2.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the sweep stamps each reveal's time from it. TryAdd so it composes with the other
        // jobs' TimeProvider registration (all TimeProvider.System).
        services.TryAddSingleton(TimeProvider.System);

        // Deployment sweep policy (enabled/cadence/batch size), read once from configuration with safe defaults.
        services.AddSingleton(options);

        // The shared infrastructure the central reveal command needs, all scoped so each sweep runs in its own
        // DbContext scope (the background service creates a scope per run). TryAdd so it composes safely if the
        // worker also wires another module that registers the same service.
        services.TryAddScoped<IVisibilityRuleRepository, VisibilityRuleRepository>();
        services.TryAddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();
        services.TryAddScoped<IAuditLogRepository, AuditLogRepository>();
        services.TryAddScoped<ISessionEventRepository, SessionEventRepository>();
        services.TryAddScoped<TransactionalUnitOfWork>();

        // The central reveal command (the SAME engine the API drives), the due reader and the sweep application
        // service. The auto-reveal goes through RevealService, never a duplicated reveal path.
        services.TryAddScoped<RevealService>();
        services.TryAddScoped<IScheduledRevealDueReader, ScheduledRevealDueReader>();
        services.AddScoped<ScheduledRevealService>();

        return true;
    }
}
