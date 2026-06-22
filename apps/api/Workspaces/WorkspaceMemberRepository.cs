// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceMemberRepository"/>
/// (CORE-WS-002), backed by the <c>workspace_members</c> table mapped in
/// <see cref="WorkspaceMemberConfiguration"/>.
/// </summary>
internal sealed class WorkspaceMemberRepository : IWorkspaceMemberRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceMemberRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<WorkspaceMember?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored membership (ids are generated
        // non-empty), so the lookup fails fast instead of returning an
        // arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // All three predicates translate to parameterized SQL equality. The
        // (workspace_id, user_id) pair is the unique natural key; adding the
        // organization_id predicate enforces the tenant boundary at the row
        // level (checked before the workspace boundary), so a membership in
        // another workspace, or in the same workspace id under a different
        // organization, is never returned (threat T5/T1). The unique index
        // guarantees at most one row.
        return await _dbContext.WorkspaceMembers
            .FirstOrDefaultAsync(
                member => member.OrganizationId == organizationId
                    && member.WorkspaceId == workspaceId
                    && member.UserProfileId == userProfileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Reuse the scoped lookup so the deny-by-default, tenant-isolation and
        // workspace-isolation semantics live in exactly one place: no membership
        // in this workspace (or a membership only in another workspace, or only
        // under another organization) denies.
        var member = await FindAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);
        return member is not null;
    }

    /// <inheritdoc />
    public async Task<bool> HasRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role,
        CancellationToken cancellationToken)
    {
        // Reject undefined enum values a cast could smuggle in before touching
        // the database, so the check can never silently pass an invalid role.
        if (!WorkspaceMember.IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        // Reuse the scoped lookup, then match the role exactly in memory. The
        // matrix is non-linear, so there is no ordering comparison: a different
        // role (in either direction) denies, and a membership in a foreign
        // workspace or tenant is never consulted (deny-by-default, threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md). The aggregate's HasRole keeps the
        // exact-match rule in one place.
        var member = await FindAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);
        return member is not null && member.HasRole(role);
    }

    /// <inheritdoc />
    public async Task<WorkspaceMemberAddResult> AddAsync(
        WorkspaceMember member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        _dbContext.WorkspaceMembers.Add(member);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceMemberAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(member).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row for this
            // (workspace, subject) pair exists now, the unique index rejected
            // the insert as a duplicate (typically a lost create race). Any
            // other failure (for example a foreign-key violation for a
            // non-existent workspace or tenant) is rethrown unchanged.
            var duplicateExists = await _dbContext.WorkspaceMembers
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.WorkspaceId == member.WorkspaceId
                        && existing.UserProfileId == member.UserProfileId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceMemberAddResult.DuplicateMembership;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceMember?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (memberId == Guid.Empty)
        {
            throw new ArgumentException("Member id must not be empty.", nameof(memberId));
        }

        // All three predicates translate to parameterized SQL equality. The id is the row's own key, but
        // the organization_id and workspace_id predicates additionally pin the tenant and workspace
        // boundaries (the organization boundary checked before the workspace boundary), so a membership with
        // that id in another workspace, or in the same workspace id under a different organization, is never
        // returned (threats T1/T5).
        return await _dbContext.WorkspaceMembers
            .FirstOrDefaultAsync(
                member => member.Id == memberId
                    && member.OrganizationId == organizationId
                    && member.WorkspaceId == workspaceId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountByRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        MembershipRole role,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // Reject undefined enum values a cast could smuggle in before touching the database.
        if (!WorkspaceMember.IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        // The role is persisted as its stable name (HasConversion<string>), so EF translates this equality
        // to the stored name; the matrix is non-linear, so this is an EXACT match, never an ordering
        // comparison. Scoped by organization and workspace so only this workspace's members are counted
        // (threat T5).
        return await _dbContext.WorkspaceMembers
            .CountAsync(
                member => member.OrganizationId == organizationId
                    && member.WorkspaceId == workspaceId
                    && member.Role == role,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(WorkspaceMember member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        _dbContext.WorkspaceMembers.Remove(member);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceMember>> ListBySubjectInOrganizationAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored membership (ids are generated non-empty), so the read fails fast
        // instead of scanning the whole table.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // TENANT-SCOPED export read (CORE-PRIV-004): the predicate LEADS with the tenant column (the organization
        // boundary checked before the workspace boundary) and matches the subject, so a membership the subject
        // holds in another tenant is never returned even when the surrogate ids would otherwise be addressable
        // (threat T5/T1). The read is tracking-free (it never mutates — the export only reads the subject's data
        // back) and ordered oldest-first by the time-ordered surrogate id (UUIDv7), provider-independent because
        // SQLite cannot ORDER BY a DateTimeOffset.
        return await _dbContext.WorkspaceMembers
            .AsNoTracking()
            .Where(member => member.OrganizationId == organizationId
                && member.UserProfileId == userProfileId)
            .OrderBy(member => member.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceMemberRosterEntry>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a tenant or workspace (ids are generated non-empty), so the read fails fast
        // instead of scanning the whole table (mirrors the other tenant/workspace-scoped reads).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // TENANT- and WORKSPACE-scoped roster read (CORE-WSM-001): the predicate LEADS with organization_id (the
        // organization boundary checked before the workspace boundary) and matches the workspace, so a membership
        // in another workspace or tenant is never returned (threats T1/T5). The audience-safe display name is
        // joined READ-ONLY from the subject's users profile with a LEFT JOIN (the workspace_members.user_id FK
        // cascades on user erasure, so a row without a profile cannot normally exist, but the left join keeps the
        // read defensive); ONLY the display-name column is projected — never the profile's email, token or any
        // other column — so the projection is data-minimized at the query (threats T6/T7). Tracking-free (it never
        // mutates) and ordered oldest-first by the time-ordered surrogate id (UUIDv7), provider-independent
        // because SQLite cannot ORDER BY a DateTimeOffset. Bounded by Skip/Take so a single response can never
        // materialize an unbounded array (threat T9; CORE-DX-003).
        var query =
            from member in _dbContext.WorkspaceMembers.AsNoTracking()
            where member.OrganizationId == organizationId && member.WorkspaceId == workspaceId
            join profile in _dbContext.UserProfiles.AsNoTracking()
                on member.UserProfileId equals profile.Id into profiles
            from profile in profiles.DefaultIfEmpty()
            orderby member.Id
            select new WorkspaceMemberRosterEntry(
                member.Id,
                member.OrganizationId,
                member.WorkspaceId,
                member.UserProfileId,
                member.Role,
                profile != null ? profile.DisplayName : null,
                member.CreatedAt,
                member.UpdatedAt);

        return await query
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
