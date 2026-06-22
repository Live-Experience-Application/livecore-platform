// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Scenes;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Composition-root ADAPTER fulfilling the Visibility module's <see cref="IVisibleResourceAudienceProjector"/>
/// port (CORE-APROJ-002) by resolving each visible resource's AUDIENCE-SAFE label/body through the owning
/// resource module's existing AUDIENCE projection. The participant-visible feed needs a rendered,
/// audience-safe title (and, where applicable, a short body) per item, but the Visibility module (THE central
/// security module) is, by the enforced module dependency graph (CORE-ARCH-001; docs/05_MODULE_CONTRACTS.md),
/// NOT allowed to reference the Scenes, Content or Entities modules. This adapter lives in the shared-kernel
/// composition root (<c>LiveCore.Api.Hosting</c>, which references every module by design), so it can resolve
/// the polymorphic (<see cref="VisibilityResourceType"/>, resource id) reference through the three resource
/// repositories without adding a forbidden Visibility -&gt; Scenes/Content/Entities edge — the same
/// port-and-adapter shape <see cref="VisibilityResourceWorkspaceLocator"/> (CORE-SVIS-005) uses.
///
/// AUDIENCE-SAFE BY CONSTRUCTION. The label/body are produced ONLY through the resource kind's existing
/// role-based AUDIENCE projection, never the raw host title/body, so no host-only content leaks (threats
/// T2/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>Scene -&gt; <see cref="ParticipantSceneResponse"/>: the audience-safe scene Title; no body.</item>
///   <item>ContentBlock -&gt; <see cref="ParticipantContentBlockResponse"/>: the audience-safe generic kind
///   (Text/Media/Data) as the label; the body is the host-only payload the audience projection deliberately
///   STRIPS (threat T2), so no body is exposed.</item>
///   <item>Entity -&gt; <see cref="ParticipantEntityResponse"/>: the audience-safe entity Name; no body.</item>
/// </list>
///
/// Each lookup goes through the resource module's TENANT- AND WORKSPACE-scoped <c>FindByIdAsync</c> (which
/// leads its predicate with <c>organization_id</c> then <c>workspace_id</c>), so a resource in another tenant
/// or workspace is never read even when the surrogate id matches; an unknown id, an empty id, an undefined
/// resource type or a dangling rule whose resource was deleted resolves to <see langword="null"/> — the feed
/// item then degrades to a null label/body WITHOUT error (threats T1/T5). This adapter NEVER recomputes
/// visibility: it renders an audience-safe label/body for a resource the central
/// <see cref="VisibilityPolicy"/> has already decided the participant may see. It never branches on a vertical
/// resource meaning; it maps the generic Core resource kind to the owning repository only.
/// </summary>
internal sealed class VisibleResourceAudienceProjector : IVisibleResourceAudienceProjector
{
    private readonly ISceneRepository _scenes;
    private readonly IContentBlockRepository _contentBlocks;
    private readonly IEntityRepository _entities;

    public VisibleResourceAudienceProjector(
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
    public async Task<VisibleResourceAudienceProjection?> ProjectAudienceSafeAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // An empty resource id can never address a stored resource, so fail soft without a query (the
        // repositories would reject it anyway). The (organization, workspace) ids come from the loaded
        // participant/session, so they are non-empty.
        if (resourceId == Guid.Empty)
        {
            return null;
        }

        // Map the generic Core resource kind to the owning repository's workspace-scoped find, then project
        // through that kind's existing AUDIENCE DTO. An undefined enum value matches no resource and falls
        // through to null (fail-soft). A missing resource (deleted/dangling rule) likewise yields null.
        switch (resourceType)
        {
            case VisibilityResourceType.Scene:
                var scene = await _scenes
                    .FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (scene is null)
                {
                    return null;
                }

                // The audience-safe scene label is its Title (ParticipantSceneResponse keeps it); scenes
                // carry no audience body.
                return new VisibleResourceAudienceProjection(ParticipantSceneResponse.From(scene).Title, Body: null);

            case VisibilityResourceType.ContentBlock:
                var contentBlock = await _contentBlocks
                    .FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (contentBlock is null)
                {
                    return null;
                }

                // The audience-safe content-block label is its generic kind (Text/Media/Data) — the only
                // field ParticipantContentBlockResponse keeps; its host body is deliberately stripped, so no
                // audience body is exposed (threat T2).
                return new VisibleResourceAudienceProjection(
                    ParticipantContentBlockResponse.From(contentBlock).Type, Body: null);

            case VisibilityResourceType.Entity:
                var entity = await _entities
                    .FindByIdAsync(organizationId, workspaceId, resourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (entity is null)
                {
                    return null;
                }

                // The audience-safe entity label is its Name — the same field the entity's audience
                // projection (ParticipantEntityResponse) keeps; entities carry no audience body. The feed item
                // needs only the label, not the entity-type discriminator key the participant entity DTO also
                // carries (CORE-APROJ-003), so the Name is read directly without resolving the type key.
                return new VisibleResourceAudienceProjection(entity.Name, Body: null);

            default:
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<VisibleSceneAudienceProjection?> ProjectAudienceSafeSceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored scene, so fail soft without a query (mirrors
        // ProjectAudienceSafeAsync). The (organization, workspace) ids come from the loaded participant/session,
        // so they are non-empty.
        if (sceneId == Guid.Empty)
        {
            return null;
        }

        // The lookup is tenant- AND workspace-scoped (the predicate leads with organization_id then
        // workspace_id), so a scene in another tenant or workspace is never read even when the surrogate id
        // matches (threats T1/T5). A missing scene (deleted / dangling rule) yields null so the feed's current
        // scene degrades to null without error.
        var scene = await _scenes
            .FindByIdAsync(organizationId, workspaceId, sceneId, cancellationToken)
            .ConfigureAwait(false);
        if (scene is null)
        {
            return null;
        }

        // Project ONLY through the scene's existing AUDIENCE projection (ParticipantSceneResponse keeps exactly
        // id/title/order), never the raw host scene, so no host-only scene field (the tenant/workspace boundary
        // ids or the host preparation timestamps) can leak (threats T2/T7).
        var audience = ParticipantSceneResponse.From(scene);
        return new VisibleSceneAudienceProjection(audience.Id, audience.Title, audience.Order);
    }
}
