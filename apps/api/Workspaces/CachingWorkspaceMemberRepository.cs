// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Caching decorator over <see cref="IWorkspaceMemberRepository"/> (CORE-PERF-003). After the tenant context
/// resolver, many endpoints RE-QUERY the caller's workspace membership and role — for example the
/// participant-visible feed runs two <see cref="HasRoleAsync"/> checks (Host, then CoHost) per request. This
/// decorator serves the underlying <see cref="FindAsync"/> — and the <see cref="IsMemberAsync"/> /
/// <see cref="HasRoleAsync"/> built on it — from the shared <see cref="AuthorizationLookupCache"/>, so a high
/// request rate by the same principal does not re-issue the identical workspace-membership SELECT each time and for
/// each role probed.
///
/// <para>
/// It is a TRANSPARENT decorator over the concrete <see cref="WorkspaceMemberRepository"/>, so every endpoint is
/// unchanged. Only a positive membership is cached (no membership is never cached, so a non-member / wrong-role
/// caller is always re-checked, fail-closed); the cached row is read-only (a workspace membership is created and
/// removed, never updated, so the role is stable), and the role is matched in memory against the cached membership
/// exactly as the inner repository does — so the EXACT, non-linear role check is unchanged. REMOVING a membership
/// invalidates the subject's cache group, revoking their cached workspace access on the next request, fail-closed;
/// an erasure or tenant deletion that CASCADE-removes the row invalidates the same subject/organization groups
/// through the user-profile / organization decorators. All other operations delegate to the inner repository.
/// </para>
/// </summary>
internal sealed class CachingWorkspaceMemberRepository : IWorkspaceMemberRepository
{
    private const string _memberKeyPrefix = "auth:ws:mem:";

    private readonly IWorkspaceMemberRepository _inner;
    private readonly AuthorizationLookupCache _cache;

    public CachingWorkspaceMemberRepository(IWorkspaceMemberRepository inner, AuthorizationLookupCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<WorkspaceMember?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
        => _cache.GetOrAddAsync(
            MemberKey(organizationId, workspaceId, userProfileId),
            ct => _inner.FindAsync(organizationId, workspaceId, userProfileId, ct),
            static member =>
            [
                AuthorizationLookupCache.OrganizationGroup(member.OrganizationId),
                AuthorizationLookupCache.SubjectGroup(member.UserProfileId),
            ],
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
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
        // Reject undefined enum values before consulting the cache, exactly as the inner implementation does, so
        // the check can never silently pass an invalid role.
        if (!WorkspaceMember.IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        // Reuse the cached membership, then match the role EXACTLY in memory (the matrix is non-linear, so there is
        // no ordering comparison) — the same decision the inner repository makes, served from the cache.
        var member = await FindAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);
        return member is not null && member.HasRole(role);
    }

    /// <inheritdoc />
    public Task<WorkspaceMemberAddResult> AddAsync(WorkspaceMember member, CancellationToken cancellationToken)
        => _inner.AddAsync(member, cancellationToken);

    /// <inheritdoc />
    public Task<WorkspaceMember?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid memberId,
        CancellationToken cancellationToken)
        => _inner.FindByIdAsync(organizationId, workspaceId, memberId, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountByRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        MembershipRole role,
        CancellationToken cancellationToken)
        => _inner.CountByRoleAsync(organizationId, workspaceId, role, cancellationToken);

    /// <inheritdoc />
    public async Task RemoveAsync(WorkspaceMember member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        await _inner.RemoveAsync(member, cancellationToken).ConfigureAwait(false);

        // The membership row IS the access grant. Evicting the subject's cache group drops their cached workspace
        // membership/role, so the next request re-queries and denies — fail-closed, the moment access is revoked.
        _cache.InvalidateSubject(member.UserProfileId);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkspaceMember>> ListBySubjectInOrganizationAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
        => _inner.ListBySubjectInOrganizationAsync(organizationId, userProfileId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkspaceMemberRosterEntry>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
        => _inner.ListByWorkspaceAsync(organizationId, workspaceId, skip, take, cancellationToken);

    private static string MemberKey(Guid organizationId, Guid workspaceId, Guid userProfileId)
        => string.Concat(
            _memberKeyPrefix,
            organizationId.ToString("N"),
            ":",
            workspaceId.ToString("N"),
            ":",
            userProfileId.ToString("N"));
}
