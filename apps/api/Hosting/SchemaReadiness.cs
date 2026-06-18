// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Reports whether the database is MISSING any migration the build expects (CORE-OBS-010, the "Observability
/// Depth" epic). The schema ships as checked-in EF Core migrations applied by a separate, run-to-completion
/// deployment step BEFORE the API rolls out (CORE-OPS-001) — the host never migrates implicitly. If that step
/// is skipped or fails, or the API image is rolled forward ahead of its schema, the database lacks a migration
/// the running build expects and the first domain query would 500. The probe answers the single yes/no
/// question behind the schema-version readiness gate.
///
/// The probe returns a BOOLEAN only — never the names of the missing migrations — so no schema detail can ever
/// reach the unauthenticated readiness response (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). It is the
/// schema-version counterpart of the live reachability probes (<see cref="IDependencyReadinessProbe"/>,
/// CORE-OBS-009) and is distinct from them: those check that a configured dependency is reachable, this checks
/// that the connected database carries the schema this build was compiled against.
/// </summary>
internal interface ISchemaMigrationProbe
{
    /// <summary>
    /// Returns <see langword="true"/> when the database is missing one or more migrations the build expects
    /// (the schema is behind the build), and <see langword="false"/> when the schema is current. The caller
    /// bounds this call by the short readiness timeout (CORE-RES-004/005) and treats any throw as not-ready
    /// (fail-closed), so an implementation should issue a single cheap query and let failures propagate.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query when the readiness timeout elapses.</param>
    Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="ISchemaMigrationProbe"/> over the live <see cref="LiveCoreDbContext"/>. It asks EF Core for
/// the migrations the build defines that the database has not recorded as applied
/// (<see cref="RelationalDatabaseFacadeExtensions.GetPendingMigrationsAsync"/>) — a single read of the
/// migrations-history table bounded by the configured database command timeout (CORE-RES-004). It returns only
/// whether ANY are missing; the migration names stay inside the probe (threat T7).
/// </summary>
internal sealed class DbContextSchemaMigrationProbe : ISchemaMigrationProbe
{
    private readonly LiveCoreDbContext _dbContext;

    public DbContextSchemaMigrationProbe(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        var pending = await _dbContext.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);

        return pending.Any();
    }
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> (tagged <see cref="HealthEndpoints.ReadinessTag"/>) that gates
/// readiness on the database schema being up to date (CORE-OBS-010). It is registered only when persistence is
/// configured — alongside the database connectivity check (<c>AddDbContextCheck</c>) — and is the schema-version
/// counterpart to that connectivity probe: connectivity proves the database ANSWERS, this proves it carries the
/// schema this build expects. So a host whose migrate step was skipped/failed, or that was rolled forward ahead
/// of its schema, reports NOT-READY (503 on <c>/health/ready</c>) and leaves the rotation instead of advertising
/// Ready and then 500-ing on the first domain query. Liveness (<c>/health/live</c>) is separate and shallow — it
/// runs no checks — so a stale schema never restarts an otherwise-live process.
///
/// <para>
/// BOUNDED AND FAIL-CLOSED (CORE-RES-004/005). The migrations-history read is bounded both by the configured
/// database command timeout (CORE-RES-004) and, as a wall-clock backstop, by the short, configurable readiness
/// timeout (<see cref="DependencyReadinessOptions.ProbeTimeout"/>, the same bound the deep reachability probes
/// use). A read that exceeds the bound OR throws — including a database that is unreachable — is counted as
/// not-ready, so any uncertainty resolves to NOT-READY: the check can only ever leave a host out of rotation,
/// never falsely keep it in.
/// </para>
///
/// <para>
/// STATUS-ONLY (threat T7). The failure description is a fixed, schema-free sentence for the server-side
/// structured log / health UI; the unauthenticated readiness response carries ONLY the overall status
/// (<see cref="HealthEndpoints"/>), so neither which migrations are missing nor any other schema detail leaks to
/// an anonymous caller. The vocabulary is product-neutral (AGENTS.md).
/// </para>
/// </summary>
internal sealed class SchemaUpToDateHealthCheck : IHealthCheck
{
    /// <summary>
    /// Name the readiness check is registered under. Kept server-side (the readiness endpoint writes only the
    /// overall status), never leaked to the unauthenticated response.
    /// </summary>
    public const string CheckName = "database-schema";

    private readonly ISchemaMigrationProbe _probe;
    private readonly TimeSpan _timeout;

    public SchemaUpToDateHealthCheck(ISchemaMigrationProbe probe, DependencyReadinessOptions options)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);

        _probe = probe;
        _timeout = options.ProbeTimeout;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Hard-bound the schema read to the short readiness timeout regardless of whether the underlying query
        // honors cancellation, so a hung connection can never hold the readiness response open. A cooperative
        // read also sees the linked token cancelled and can release its resources promptly.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTask = EvaluateAsync(timeoutSource.Token);
        var timeoutTask = Task.Delay(_timeout, CancellationToken.None);

        var completed = await Task.WhenAny(probeTask, timeoutTask).ConfigureAwait(false);
        if (completed != probeTask)
        {
            // Timed out: ask the read to stop, observe its eventual result so it never surfaces as an unobserved
            // task exception, and report not-ready (fail-closed).
            timeoutSource.Cancel();
            _ = probeTask.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            return Unready;
        }

        return await probeTask.ConfigureAwait(false);
    }

    private async Task<HealthCheckResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hasPending = await _probe.HasPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);

            // Schema-free description either way: it is inspected server-side only and never written to the
            // unauthenticated readiness response (threat T7).
            return hasPending ? Unready : HealthCheckResult.Healthy();
        }
        catch
        {
            // Any failure — a transport error, a non-success read or a cancellation — leaves the schema state
            // unknown. Fail closed: report not-ready rather than guessing the schema is current.
            return Unready;
        }
    }

    private static HealthCheckResult Unready => HealthCheckResult.Unhealthy(
        "The database schema is not up to date: one or more expected migrations are not applied.");
}

/// <summary>
/// Registers the database-schema readiness check (CORE-OBS-010). It adds the
/// <see cref="DbContextSchemaMigrationProbe"/> over the configured <see cref="LiveCoreDbContext"/> plus the
/// single readiness check (tagged <see cref="HealthEndpoints.ReadinessTag"/>) that runs it, bounded and
/// fail-closed. It is called only inside the persistence conditional in <c>Program.cs</c> — there is no schema
/// to gate on without a configured database — so it sits beside the database connectivity check it complements
/// (CORE-OBS-009 covers dependency reachability; this covers schema version).
/// </summary>
internal static class SchemaReadinessExtensions
{
    /// <summary>
    /// Adds the database-schema readiness check and its probe. Registered only when persistence is configured
    /// (the caller is inside the persistence conditional), so it never runs against an absent database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddSchemaReadinessCheck(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: the probe reads through the scoped (pooled) DbContext the health-check service resolves within
        // the per-probe scope it creates, exactly like the database connectivity check.
        services.AddScoped<ISchemaMigrationProbe, DbContextSchemaMigrationProbe>();

        services.AddHealthChecks().AddCheck<SchemaUpToDateHealthCheck>(
            SchemaUpToDateHealthCheck.CheckName,
            failureStatus: HealthStatus.Unhealthy,
            tags: [HealthEndpoints.ReadinessTag]);

        return services;
    }
}
