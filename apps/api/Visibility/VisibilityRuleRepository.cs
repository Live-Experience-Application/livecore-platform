// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Visibility;

/// <summary>
/// EF Core implementation of <see cref="IVisibilityRuleRepository"/> (CORE-VIS-001), backed by the
/// <c>visibility_rules</c> table mapped in <see cref="VisibilityRuleConfiguration"/>.
/// </summary>
internal sealed class VisibilityRuleRepository : IVisibilityRuleRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public VisibilityRuleRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<VisibilityRule?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored rule (ids are generated non-empty), so the lookup
        // fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Visibility rule id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant
        // column. The lookup is exactly tenant- and workspace-scoped, so a rule under another
        // organization or workspace is never returned even when the surrogate id matches (threat
        // T5/T1).
        return await _dbContext.VisibilityRules
            .FirstOrDefaultAsync(
                rule => rule.OrganizationId == organizationId
                    && rule.WorkspaceId == workspaceId
                    && rule.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's rules, so the lookup fails fast instead
        // of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's rules are
        // never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering is
        // deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VisibilityRule?> FindByIdInSessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored rule (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Visibility rule id must not be empty.", nameof(id));
        }

        // All four predicates translate to parameterized SQL equality, leading with the tenant column then
        // the workspace then the session. The lookup is exactly tenant-, workspace- and session-scoped, so a
        // rule under another organization, workspace or session is never returned even when the surrogate id
        // matches; a reveal of a concurrent session of the same workspace is never reachable here (threats
        // T5/T1; the cross-session leak T3).
        return await _dbContext.VisibilityRules
            .FirstOrDefaultAsync(
                rule => rule.OrganizationId == organizationId
                    && rule.WorkspaceId == workspaceId
                    && rule.SessionId == sessionId
                    && rule.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListPageBySessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session's rules, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);

        // The predicate leads with the tenant column, then matches the workspace and the SESSION, so the page
        // is exactly tenant-, workspace- and session-scoped: another tenant's, workspace's or session's rules
        // are never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // cross-session leak T3). The ordering is deterministic — sorted by the time-ordered surrogate id —
        // and the caller over-fetches one extra row (take = limit + 1) to compute HasMore without a second
        // COUNT (the bounded-list technique, CORE-DX-003; threat T9). Both dimensions are returned (the
        // audience-wide rule and every selected-participant rule of the session).
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.SessionId == sessionId)
            .OrderBy(rule => rule.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored resource's rules, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        // The predicate leads with the tenant column, then matches the workspace, the SESSION, the
        // resource type and the resource id — the documented critical index shape
        // visibility_rules(session_id, resource_type, resource_id) (CORE-SVIS-001). So the list is
        // exactly tenant-, workspace-, session- and resource-scoped: a rule in another session of the
        // same workspace is never returned, so a reveal in one session can never make a resource visible
        // in a concurrent session (the cross-session leak; threat T5/T3). The ordering is deterministic —
        // sorted by the time-ordered surrogate id.
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.SessionId == sessionId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session's rules, so the lookup fails fast instead of
        // returning an arbitrary set of rows (mirrors ListByResourceAsync).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(resourceIds);

        // No requested resources can never match a rule, so the batch returns nothing without touching the
        // database (and avoids an empty IN ()).
        var ids = resourceIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        // The BATCH lookup (CORE-PERF-002): one query for the whole resource set — the predicate leads with
        // the tenant column, then matches the workspace, the SESSION and the resource-id set
        // (resource_id IN (..)) on the session-scoped critical index. So the rules for every distinct subject
        // of a replayed slice are read ONCE, staying exactly tenant-, workspace- and session-scoped (a rule
        // in another session of the same workspace is never returned — the cross-session leak; threat T5/T3).
        // The id is a globally-unique surrogate, so the caller re-pairs each returned rule to its requested
        // (type, id) subject by the rule's own resource type/id. Ordering is deterministic (time-ordered id).
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.SessionId == sessionId
                && ids.Contains(rule.ResourceId))
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _dbContext.VisibilityRules.Add(rule);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return VisibilityRuleAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on
            // the same scope (and, inside the reveal command's transaction, EF rolls back to the
            // SaveChanges savepoint, leaving the transaction alive — the same pattern as
            // QuotaUsageRepository.AddAsync).
            _dbContext.Entry(rule).State = EntityState.Detached;

            // Provider-neutral duplicate detection (CORE-SVIS-002): if a rule already exists in the SAME
            // (session, resource, dimension), the filtered unique index rejected this insert as a
            // duplicate — a concurrent first-reveal that lost the create race. The dimension is matched on
            // target_participant_id (null == audience-wide). Any other failure (for example a session,
            // workspace or tenant foreign key that does not exist) is rethrown unchanged, so the
            // repository's documented foreign-key behavior is preserved.
            var duplicateExists = await _dbContext.VisibilityRules
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.SessionId == rule.SessionId
                        && existing.ResourceType == rule.ResourceType
                        && existing.ResourceId == rule.ResourceId
                        && existing.TargetParticipantId == rule.TargetParticipantId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return VisibilityRuleAddResult.Duplicate;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // The rule was loaded and mutated within this scope's change tracker (or is attached here);
        // only the mutable visibility state and update timestamp change. The organization,
        // workspace, id and governed resource are immutable on the aggregate, so an update can never
        // move the row to another tenant, workspace or resource (threat T5).
        _dbContext.VisibilityRules.Update(rule);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RemoveByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored resource's rules, so the deletion fails fast instead of
        // matching an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        // The predicate is the documented critical index shape
        // visibility_rules(workspace_id, resource_type, resource_id) led by organization_id, so it
        // removes ALL rules for the resource (audience-wide and every selected-participant rule) while
        // staying exactly tenant-, workspace- and resource-scoped: another tenant's or workspace's rules
        // are never removed even when the resource id would otherwise be addressable (threat T5/T1).
        // Load-then-RemoveRange (the codebase's add/remove + SaveChanges convention) so the removed rows
        // participate in any ambient transaction the caller controls.
        var rules = await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return 0;
        }

        _dbContext.VisibilityRules.RemoveRange(rules);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rules.Count;
    }
}
