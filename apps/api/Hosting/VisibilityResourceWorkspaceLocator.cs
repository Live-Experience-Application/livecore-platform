// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Scenes;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Composition-root ADAPTER fulfilling the Visibility module's
/// <see cref="IVisibilityResourceWorkspaceLocator"/> port (CORE-SVIS-005) by consulting the owning
/// resource modules. The visibility-rule create command enforces the same-workspace invariant
/// server-side — a rule may only reference a resource that lives in the rule's own workspace — but the
/// Visibility module (THE central security module) is, by the enforced module dependency graph
/// (CORE-ARCH-001; docs/05_MODULE_CONTRACTS.md), NOT allowed to reference the Scenes, Content or Entities
/// modules. This adapter lives in the shared-kernel composition root (<c>LiveCore.Api.Hosting</c>, which
/// references every module by design), so it can resolve the polymorphic
/// (<see cref="VisibilityResourceType"/>, resource id) reference through the three resource repositories
/// without adding a forbidden Visibility -&gt; Scenes/Content/Entities edge — the same hexagonal
/// port-and-adapter shape the module boundary expects.
///
/// Each lookup goes through the resource module's TENANT- AND WORKSPACE-scoped <c>FindByIdAsync</c>
/// (which leads its predicate with <c>organization_id</c> then <c>workspace_id</c>), so a resource in
/// another tenant or workspace is never returned even when the surrogate id matches; an unknown id, an
/// empty id or an undefined resource type resolves to <see langword="false"/> — fail-closed (threats
/// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). The adapter never branches on a vertical resource meaning;
/// it maps the generic Core resource kind to the owning repository only.
/// </summary>
internal sealed class VisibilityResourceWorkspaceLocator : IVisibilityResourceWorkspaceLocator
{
    private readonly ISceneRepository _scenes;
    private readonly IContentBlockRepository _contentBlocks;
    private readonly IEntityRepository _entities;

    public VisibilityResourceWorkspaceLocator(
        ISceneRepository scenes,
        IContentBlockRepository contentBlocks,
        IEntityRepository entities)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(contentBlocks);
        ArgumentNullException.ThrowIfNull(entities);
        _scenes = scenes;
        _contentBlocks = contentBlocks;
        _entities = entities;
    }

    /// <inheritdoc />
    public async Task<bool> ResourceExistsInWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // An empty resource id can never address a stored resource, so fail closed without a query (the
        // repositories would reject it anyway). The (organization, workspace) ids come from the loaded
        // session, so they are non-empty.
        if (resourceId == Guid.Empty)
        {
            return false;
        }

        // Map the generic Core resource kind to the owning repository's workspace-scoped find. An undefined
        // enum value matches no resource and falls through to false (fail-closed).
        return resourceType switch
        {
            VisibilityResourceType.Scene =>
                await _scenes.FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false) is not null,
            VisibilityResourceType.ContentBlock =>
                await _contentBlocks.FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false) is not null,
            VisibilityResourceType.Entity =>
                await _entities.FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false) is not null,
            _ => false,
        };
    }
}
