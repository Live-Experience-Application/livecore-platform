// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// Dependency-injection wiring for the background store-notification reconciliation job (CORE-JOB-003). The job
/// runs in the worker host (docs/02_ARCHITECTURE.md: the worker owns async jobs), a separate process from the API.
/// Like the export processing wiring (<c>AddExportProcessing</c>) and the recap generation wiring
/// (<c>AddRecapGeneration</c>), this PUBLIC extension keeps the registration inside the module that owns it: the
/// worker calls <see cref="AddStoreNotificationReconciliation"/> and registers its own scheduling
/// <c>BackgroundService</c>, while the internal repositories and reader stay internal.
///
/// <para>
/// GATED ON BILLING, FAIL-CLOSED — the key difference from the other jobs. The job is registered ONLY when BOTH a
/// database connection string is configured (like every job) AND the deployment has explicitly enabled
/// reconciliation (<c>Store:Reconciliation:Enabled=true</c>). Store receipts/billing are out of scope for Core v1
/// (docs/01_PRODUCT_VISION_AND_SCOPE.md), so with the flag unset this registers NOTHING and returns
/// <see langword="false"/>: the worker runs without a reconciliation loop — "only runs when billing is configured"
/// (the story's acceptance criterion), the same fail-closed posture as the store verification/notification parser
/// resolvers that register no adapter until a deployment supplies one.
/// </para>
///
/// <para>
/// Everything the job needs is registered here: the EF Core <see cref="LiveCoreDbContext"/> (PostgreSQL via Npgsql,
/// exactly as the API host and the other worker wirings register it), the Store module's notification and purchase
/// repositories, the reconciliation reader, the reused <see cref="StoreNotificationService"/> and
/// <see cref="PurchaseTransactionService"/>, and the <see cref="StoreNotificationReconciliationOptions"/>. The
/// shared infrastructure (the <see cref="LiveCoreDbContext"/> and the <see cref="TimeProvider"/>) is registered
/// with TryAdd/AddDbContext semantics so it composes safely when the worker also wires the other jobs in the same
/// container. No credentials or secrets are read here (only the connection string and the timing policy from
/// configuration); none live in this repository (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
public static class StoreNotificationReconciliationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store-notification reconciliation job's dependencies when a database connection string is
    /// configured (<c>ConnectionStrings:Database</c>) AND reconciliation is explicitly enabled
    /// (<c>Store:Reconciliation:Enabled=true</c>). Returns whether the job — and therefore its scheduling
    /// background service — should run, so the caller can decide whether to register it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>Store:Reconciliation</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured and the job is enabled and its services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddStoreNotificationReconciliation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no reconciliation loop,
            // exactly as the API host and the other jobs run without persistence. Fail-safe, not fail-open.
            return false;
        }

        var options = StoreNotificationReconciliationOptions.FromConfiguration(configuration);
        if (!options.Enabled)
        {
            // Billing is out of scope for v1 and not configured for this deployment: the reconciliation job stays
            // off (fail-closed). Nothing is registered, so the worker schedules no reconciliation loop.
            return false;
        }

        // AddDbContext registers the context and its options with TryAdd semantics, so calling it here and in the
        // other worker job wirings (same connection string) is safe — the later calls are no-ops.
        // Connection resilience (CORE-CONC-003) + per-command timeouts (CORE-RES-004): UseLiveCoreNpgsql turns on
        // the retrying execution strategy so a transient PostgreSQL disruption is retried automatically instead of
        // surfacing as a worker-job exception, and bounds each command at the configured client CommandTimeout and
        // server statement_timeout — the same policy the API host and the other worker jobs apply.
        services.AddDbContext<LiveCoreDbContext>(dbContextOptions =>
            dbContextOptions.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // Deployment reconciliation policy (enablement/cadence/batch size), read once from configuration. No
        // TimeProvider is registered here: each convergence is stamped with the authoritative notification's own
        // recorded event time, so the job's graph needs no clock.
        services.AddSingleton(options);

        // The Store module's repositories, the reconciliation reader and the reused application services. Scoped so
        // each sweep runs in its own DbContext scope (the background service creates a scope per run).
        services.AddScoped<IStoreNotificationEventRepository, StoreNotificationEventRepository>();
        services.AddScoped<IPurchaseTransactionRepository, PurchaseTransactionRepository>();
        services.AddScoped<IPurchaseEventRepository, PurchaseEventRepository>();
        services.AddScoped<IReconcilablePurchaseReader, ReconcilablePurchaseReader>();
        services.AddScoped<PurchaseTransactionService>();

        // Purchase-entitlement revocation graph (CORE-MON-004): when reconciliation converges a purchase to a
        // revoked state (a missed refund the webhook could not apply), it must revoke the granted entitlement — the
        // same inverse-of-grant path the webhook uses. So the worker wires the same revocation collaborators the API
        // host does: the Store buyer linkage and the Entitlements plan catalog + assignment service, composed by
        // ProductEntitlementGrantService.RevokeForProductAsync and the PurchaseEntitlementRevocationService the
        // StoreNotificationService picks up by constructor injection. No new table — these reuse the existing model.
        services.AddScoped<IBillingAccountLinkRepository, BillingAccountLinkRepository>();
        services.AddScoped<IPlanDefinitionRepository, PlanDefinitionRepository>();
        services.AddScoped<IEntitlementDefinitionRepository, EntitlementDefinitionRepository>();
        services.AddScoped<ISubjectEntitlementRepository, SubjectEntitlementRepository>();
        services.AddScoped<SubjectEntitlementAssignmentService>();
        services.AddScoped<ProductEntitlementGrantService>();
        services.AddScoped<PurchaseEntitlementRevocationService>();

        // Append-only audit log (CORE-SPEC-002): the worker audits the entitlement revocation and the
        // store-notification handling it drives, exactly as the API host does, so a reconciliation-applied
        // revocation/notification is a real audit fact (AuditAction.EntitlementRevoked / StoreNotification*). The
        // grant service and the StoreNotificationService pick it up by constructor injection over the same scoped
        // DbContext; a platform-level fact carries no tenant, so it needs no audit_log_sequences row.
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        // Transactional unit of work (CORE-CONC-002): the StoreNotificationService takes it by constructor injection
        // to make webhook handling atomic (CORE-MON-010). Reconciliation drives only ChangeStatusAsync and writes no
        // ledger row, so this sweep never opens the transaction itself — but the shared service requires the
        // dependency, so it is registered here over the same scoped DbContext, exactly as the API host registers it.
        services.AddScoped<TransactionalUnitOfWork>();

        services.AddScoped<StoreNotificationService>();
        services.AddScoped<StoreNotificationReconciliationService>();

        return true;
    }
}
