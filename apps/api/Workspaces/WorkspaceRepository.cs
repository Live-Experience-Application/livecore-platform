// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceRepository"/> (CORE-WS-001),
/// backed by the <c>workspaces</c> table mapped in
/// <see cref="WorkspaceConfiguration"/>.
/// </summary>
internal sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Workspace?> FindByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace (ids are generated
        // non-empty), so the lookup fails fast instead of returning an
        // arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(id));
        }

        // Both predicates translate to parameterized SQL equality on the
        // (organization_id, id) index: the lookup is exactly tenant-scoped, so a
        // workspace under another organization is never returned even when the
        // surrogate id matches (threat T5/T1).
        return await _dbContext.Workspaces
            .FirstOrDefaultAsync(
                workspace => workspace.OrganizationId == organizationId && workspace.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Workspace?> FindBySlugAsync(
        Guid organizationId,
        string slug,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        // Canonicalize first (and reject malformed input): a stored slug is
        // always canonical, so a malformed value can never address a workspace.
        var canonicalSlug = Workspace.CanonicalizeSlug(slug);

        // The predicate translates to parameterized SQL equality on the unique
        // (organization_id, slug) index, which is exact and case-sensitive under
        // the default binary-style collations of PostgreSQL: a near-match, or
        // the same slug in another organization, never addresses a foreign
        // workspace (threat T5). The unique index guarantees at most one row per
        // (organization, slug).
        return await _dbContext.Workspaces
            .FirstOrDefaultAsync(
                workspace => workspace.OrganizationId == organizationId && workspace.Slug == canonicalSlug,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Workspace>> ListByMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // Join workspaces to workspace_members on the workspace id, scoped on
        // BOTH boundaries: the organization (the tenant) and the subject. Every
        // predicate translates to parameterized SQL equality, leading with
        // organization_id, so the listing returns only the tenant's workspaces
        // the subject actually has a membership row for. A workspace the subject
        // is not a member of, or any workspace in another organization, is never
        // returned (deny-by-default; threat T5/T1). The membership row's own
        // organization_id is matched as well so a membership can never bridge a
        // workspace from a foreign tenant. The status predicate (translated to
        // status = 'Active' through the string value converter) EXCLUDES archived
        // workspaces, because this is the ACTIVE workspace list (CORE-LIFE-009: an
        // archived workspace is "excluded from active lists"). Ordering by the
        // (time-ordered) UUIDv7 id keeps the listing stable.
        return await _dbContext.Workspaces
            .AsNoTracking()
            .Where(workspace => workspace.OrganizationId == organizationId
                && workspace.Status == WorkspaceStatus.Active
                && _dbContext.WorkspaceMembers.Any(member =>
                    member.WorkspaceId == workspace.Id
                    && member.OrganizationId == organizationId
                    && member.UserProfileId == userProfileId))
            .OrderBy(workspace => workspace.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Workspace>> ListPageByMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // The same deny-by-default, tenant- AND membership-scoped, archived-excluded query as
        // ListByMemberAsync (the active workspace list; threat T5/T1), but bounded by Skip/Take so an unbounded
        // list is never materialized (threat T9). Ordering by the time-ordered UUIDv7 id keeps the page stable.
        return await _dbContext.Workspaces
            .AsNoTracking()
            .Where(workspace => workspace.OrganizationId == organizationId
                && workspace.Status == WorkspaceStatus.Active
                && _dbContext.WorkspaceMembers.Any(member =>
                    member.WorkspaceId == workspace.Id
                    && member.OrganizationId == organizationId
                    && member.UserProfileId == userProfileId))
            .OrderBy(workspace => workspace.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceAddResult> AddAsync(
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _dbContext.Workspaces.Add(workspace);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(workspace).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this slug
            // exists now in the same organization, the unique
            // (organization_id, slug) index rejected the insert as a duplicate
            // (typically a lost create race). Any other failure is rethrown
            // unchanged.
            var duplicateExists = await _dbContext.Workspaces
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.OrganizationId == workspace.OrganizationId
                        && existing.Slug == workspace.Slug,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceAddResult.DuplicateSlug;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // The workspace was loaded and mutated within this scope's change
        // tracker (or is attached here); only the mutable display name and the
        // update timestamp change. The organization, slug and id are immutable on
        // the aggregate, so an update can never move the row to another tenant
        // (threat T5).
        _dbContext.Workspaces.Update(workspace);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
