// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's PORT for resolving the AUDIENCE-SAFE label/body of a visible resource
/// (CORE-APROJ-002), so the participant-visible feed can carry a rendered, audience-safe title (and, where
/// the resource kind has one, a short body) per item — letting a consumer render the revealed content from
/// the feed alone, with no host read.
///
/// THE CENTRAL SECURITY MODULE DOES NOT REACH INTO RESOURCE MODULES (CORE-ARCH-001). The Visibility module
/// is a node in the enforced module dependency graph (docs/05_MODULE_CONTRACTS.md) and is NOT allowed to
/// reference the Scenes, Content or Entities modules. So this is a port the Visibility module OWNS, declared
/// in terms of its own <see cref="VisibilityResourceType"/> and plain ids only; the adapter that fulfils it
/// lives in the COMPOSITION ROOT (the shared-kernel <c>LiveCore.Api.Hosting</c> namespace, which references
/// every module by design) — the SAME hexagonal port-and-adapter shape
/// <see cref="IVisibilityResourceWorkspaceLocator"/> (CORE-SVIS-005) uses.
///
/// AUDIENCE-SAFE BY CONSTRUCTION — THE PROJECTION GOES THROUGH THE EXISTING ROLE-BASED AUDIENCE DTOs. The
/// adapter resolves the label/body ONLY through the resource kind's existing AUDIENCE projection
/// (<c>ParticipantSceneResponse</c> / <c>ParticipantContentBlockResponse</c> / <c>ParticipantEntityResponse</c>),
/// never the raw host title/body, so no host-only content can leak through this port (threats T2/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). Visibility is NOT recomputed here: this port resolves a resource the
/// central <see cref="VisibilityPolicy"/> has ALREADY decided the participant may see; it only renders an
/// audience-safe label/body for it.
/// </summary>
internal interface IVisibleResourceAudienceProjector
{
    /// <summary>
    /// Resolves the audience-safe label/body of a resource of the given kind and id IN exactly the given
    /// (organization, workspace), through the resource kind's existing audience projection. The lookup is
    /// tenant- AND workspace-scoped (the owning repository leads its predicate with <c>organization_id</c>
    /// then <c>workspace_id</c>), so a resource in another tenant or workspace is never read (threats
    /// T1/T5). Returns <see langword="null"/> when the resource does not resolve — an unknown id, an empty
    /// id, an undefined <paramref name="resourceType"/>, or a dangling rule whose resource was deleted — so
    /// the feed item degrades to a null label/body WITHOUT error (the resource's identity and reveal
    /// metadata still render).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the resource belongs to.</param>
    /// <param name="resourceType">The kind of resource to project (Scene/ContentBlock/Entity).</param>
    /// <param name="resourceId">The surrogate id of the resource to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VisibleResourceAudienceProjection?> ProjectAudienceSafeAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the AUDIENCE-SAFE SCENE projection (id, title, order) of the scene of the given id IN exactly
    /// the given (organization, workspace), through the Scenes module's existing AUDIENCE projection
    /// (<c>ParticipantSceneResponse</c>) — so the participant-visible feed can carry the participant's CURRENT
    /// scene (CORE-APROJ-005), letting a consumer render "where we are now" from the feed alone, with no host
    /// read. Unlike <see cref="ProjectAudienceSafeAsync"/> (which yields only the generic label/body shared by
    /// every resource kind), this returns the scene's ordering position too, the third field the audience-safe
    /// <c>ParticipantSceneResponse</c> carries.
    ///
    /// AUDIENCE-SAFE BY CONSTRUCTION — it goes ONLY through the scene's existing audience projection, never the
    /// raw host scene, so no host-only scene field (the tenant/workspace boundary ids or the host preparation
    /// timestamps) can leak (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). Visibility is NOT recomputed
    /// here: the caller resolves a scene the central <see cref="VisibilityPolicy"/> has ALREADY decided the
    /// participant may see (the most-recently-revealed visible scene of their session-scoped visible set); this
    /// port only renders an audience-safe projection of it.
    ///
    /// The lookup is tenant- AND workspace-scoped (the Scenes repository leads its predicate with
    /// <c>organization_id</c> then <c>workspace_id</c>), so a scene in another tenant or workspace is never read
    /// (threats T1/T5). Returns <see langword="null"/> when the scene does not resolve — an unknown id, an empty
    /// id, or a dangling rule whose scene was deleted — so the feed's current scene degrades to <c>null</c>
    /// WITHOUT error.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the scene belongs to.</param>
    /// <param name="sceneId">The surrogate id of the scene to project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VisibleSceneAudienceProjection?> ProjectAudienceSafeSceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The audience-safe label/body of a visible resource (CORE-APROJ-002), resolved by
/// <see cref="IVisibleResourceAudienceProjector"/> through the resource kind's existing AUDIENCE projection.
/// Both fields are audience-safe (the host title/body is never used): <see cref="Title"/> is the resource's
/// audience-safe label (a scene's title, an entity's name, a content block's generic kind) and
/// <see cref="Body"/> is the audience-safe short body where the resource kind's audience projection exposes
/// one. No current Core resource kind's audience projection exposes a body — the content block's body is the
/// host-only payload its audience projection deliberately strips (threat T2) — so <see cref="Body"/> is the
/// forward-compatible slot for a future audience body and is null today.
/// </summary>
/// <param name="Title">The resource's audience-safe label, or <see langword="null"/> when it has none.</param>
/// <param name="Body">The resource's audience-safe short body, or <see langword="null"/> when it has none.</param>
internal readonly record struct VisibleResourceAudienceProjection(string? Title, string? Body);

/// <summary>
/// The AUDIENCE-SAFE projection of a scene (CORE-APROJ-005), resolved by
/// <see cref="IVisibleResourceAudienceProjector.ProjectAudienceSafeSceneAsync"/> through the Scenes module's
/// existing AUDIENCE projection (<c>ParticipantSceneResponse</c>). It carries exactly the audience-safe scene
/// fields — the surrogate <see cref="Id"/>, the audience-safe <see cref="Title"/> and the ordering
/// <see cref="Order"/> — and NEVER a host-only scene field (the tenant/workspace boundary ids or the host
/// preparation timestamps the host <c>SceneResponse</c> carries), so projecting the feed's current scene can
/// never leak host-only content (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Id">The scene's surrogate id — a non-sensitive correlation handle.</param>
/// <param name="Title">The scene's audience-safe display title.</param>
/// <param name="Order">The scene's ordering position within its workspace.</param>
internal readonly record struct VisibleSceneAudienceProjection(Guid Id, string Title, int Order);
