// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Test double that wraps a real <see cref="IVisibilityRuleRepository"/> and deterministically forces the
/// concurrent-first-reveal race the single-rule-per-(session, resource, dimension) constraint closes
/// (CORE-SVIS-002). Just before the FIRST <see cref="AddAsync"/> (the create a first-reveal performs when it
/// finds no rule), it runs <c>injectBeforeFirstAdd</c> — which commits the "winning" concurrent reveal's
/// rule through a separate database context — so the wrapped insert then hits the filtered unique index and
/// is reported as <see cref="VisibilityRuleAddResult.Duplicate"/>. This reproduces, without real threads on
/// a single in-memory SQLite connection, exactly the interleave where two first-reveals both read "no rule"
/// and then both try to create. Every other member delegates straight to the wrapped repository.
/// </summary>
internal sealed class RaceInjectingVisibilityRuleRepository : IVisibilityRuleRepository
{
    private readonly IVisibilityRuleRepository _inner;
    private readonly Func<Task> _injectBeforeFirstAdd;
    private bool _injected;

    public RaceInjectingVisibilityRuleRepository(IVisibilityRuleRepository inner, Func<Task> injectBeforeFirstAdd)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(injectBeforeFirstAdd);
        _inner = inner;
        _injectBeforeFirstAdd = injectBeforeFirstAdd;
    }

    public async Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken)
    {
        if (!_injected)
        {
            _injected = true;
            // The racing writer commits its rule first, so this insert loses the create race.
            await _injectBeforeFirstAdd().ConfigureAwait(false);
        }

        return await _inner.AddAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    public Task<VisibilityRule?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
        => _inner.FindByIdAsync(organizationId, workspaceId, id, cancellationToken);

    public Task<VisibilityRule?> FindByIdInSessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, Guid id, CancellationToken cancellationToken)
        => _inner.FindByIdInSessionAsync(organizationId, workspaceId, sessionId, id, cancellationToken);

    public Task<IReadOnlyList<VisibilityRule>> ListPageBySessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, int skip, int take, CancellationToken cancellationToken)
        => _inner.ListPageBySessionAsync(organizationId, workspaceId, sessionId, skip, take, cancellationToken);

    public Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
        => _inner.ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken);

    public Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
        => _inner.ListByResourceAsync(organizationId, workspaceId, sessionId, resourceType, resourceId, cancellationToken);

    public Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken)
        => _inner.ListByResourcesAsync(organizationId, workspaceId, sessionId, resourceIds, cancellationToken);

    public Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken)
        => _inner.UpdateAsync(rule, cancellationToken);

    public Task<int> RemoveByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
        => _inner.RemoveByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken);
}
