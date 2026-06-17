using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Organizations;

/// <summary>
/// The authorized tenant organization deletion command of the Organizations module (CORE-PRIV-002, the "Privacy
/// and Data Lifecycle" epic, tenant offboarding / data deletion). An authorized Owner can delete an
/// organization; this service appends the platform-level offboarding audit fact and deletes the tenant root,
/// triggering the schema's existing cascade — all inside ONE database transaction so the teardown is applied
/// whole or not at all.
///
/// WHAT IS DELETED (the cascade the deletion relies on). Deleting the <c>organizations</c> row lets the database
/// honor every <c>ON DELETE CASCADE</c> foreign key declared into <c>organizations(id)</c>: the tenant's
/// workspaces (and their sessions, scenes, content blocks, entities, participants, visibility rules, assets,
/// asset links, exports, recaps and session events), its organization and workspace memberships and invitations,
/// and the tenant's OWN audit log and audit-log sequence counter. The audit log is INTENTIONALLY part of the
/// tenant teardown: an offboarded tenant leaves no tenant-scoped data behind. No application-level child
/// enumeration is performed — the schema cascade IS the teardown.
///
/// THE OFFBOARDING IS AUDITED AT THE PLATFORM LEVEL. The deletion is security-relevant, so it is recorded as an
/// append-only audit fact (<see cref="AuditAction.OrganizationDeleted"/>) capturing the actor (the Owner who
/// deleted the tenant) and the deleted organization (by id). Because the deleted tenant's OWN audit log is
/// cascade-removed, a tenant-scoped record would be torn down with it; the offboarding is therefore recorded as a
/// PLATFORM-LEVEL fact (a null organization, outside the per-tenant hash chain), exactly like the
/// entitlement/store facts (CORE-SPEC-002), so the security record SURVIVES the teardown. The audit references are
/// recorded facts, not foreign keys, so the row outlives the now-deleted organization and never carries the
/// tenant's name or any content (threat T7).
///
/// SCOPE / AUTHORIZATION. The command is AUTHORIZED within a tenant: the endpoint resolves an Owner of exactly
/// the target tenant (the trusted <see cref="TenantContext"/>) before calling in, and deletion is an OWNER-ONLY
/// privilege (docs/06_AUTHORIZATION_MATRIX.md "Delete organization") — a non-Owner tenant member is denied
/// <c>403</c> and a foreign/unknown tenant is hidden as <c>404</c> at the endpoint (threats T1/T5). This service
/// is the authorized command's effect.
///
/// ATOMICITY. The audit append and the cascade delete run inside one explicit transaction over the shared
/// <see cref="LiveCoreDbContext"/> (<see cref="TransactionalUnitOfWork"/>, CORE-CONC-002), so either the whole
/// teardown commits together — the platform-level record AND the cascade — or a failure rolls the lot back,
/// leaving the tenant intact and no audit row written. The audit append is recorded BEFORE the delete so the
/// offboarding is recorded as a fact or it does not happen.
/// </summary>
internal sealed class OrganizationDeletionService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IOrganizationRepository _organizations;
    private readonly IAuditLogRepository _audit;

    public OrganizationDeletionService(
        TransactionalUnitOfWork unitOfWork,
        IOrganizationRepository organizations,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _organizations = organizations;
        _audit = audit;
    }

    /// <summary>
    /// Deletes the organization identified by <paramref name="organizationId"/>, recording the platform-level
    /// offboarding audit fact and triggering the schema's cascade — all atomically. Returns
    /// <see cref="OrganizationDeletionResult.Deleted"/> on success, or
    /// <see cref="OrganizationDeletionResult.OrganizationNotFound"/> (changing nothing) when no organization
    /// exists for the id.
    /// </summary>
    /// <param name="organizationId">The tenant to delete (the resolved tenant the caller is an Owner of).</param>
    /// <param name="actorUserProfileId">The authenticated Owner who executed the deletion — the audited actor.</param>
    /// <param name="now">The command timestamp (the audit time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The organization id or actor id is empty.</exception>
    public async Task<OrganizationDeletionResult> DeleteAsync(
        Guid organizationId,
        Guid actorUserProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // ONE unit of work (CORE-CONC-002): the audit append and the cascade delete commit together or roll back
        // together, so a teardown is applied whole or not at all. The organization is loaded INSIDE the delegate
        // so a retry (which clears the change tracker) reloads the rolled-back state and re-runs cleanly. Every
        // injected repository writes through the same scoped DbContext the unit of work begins the transaction on.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                var organization = await _organizations
                    .FindByIdAsync(organizationId, transactionCancellationToken)
                    .ConfigureAwait(false);
                if (organization is null)
                {
                    // Defensive: the endpoint already resolved the tenant, so it should exist; if it was deleted
                    // concurrently, change nothing and let the endpoint hide it as 404 (fail-closed).
                    return OrganizationDeletionResult.OrganizationNotFound;
                }

                // AUDIT FIRST, at the platform level (null tenant). The deleted tenant's own audit log is
                // cascade-removed below, so a tenant-scoped record would be torn down with it; recording the
                // offboarding as a platform-level fact (outside the per-tenant hash chain) keeps the security
                // record after the teardown. Captures the actor and the deleted organization by id, never its name
                // or any content (threat T7).
                var entry = AuditLogEntry.ForOrganizationDeletion(
                    actorUserProfileId,
                    nameof(Organization),
                    organization.Id,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                // Delete the tenant root; the schema's ON DELETE CASCADE foreign keys remove the rest of the
                // tenant (workspaces, sessions, participants, memberships, the tenant's audit log, and so on).
                await _organizations.DeleteAsync(organization, transactionCancellationToken).ConfigureAwait(false);

                return OrganizationDeletionResult.Deleted;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
