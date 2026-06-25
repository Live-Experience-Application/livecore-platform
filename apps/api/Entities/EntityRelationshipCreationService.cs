// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// The entity-relationship create command of the Entities module (CORE-ENT-008, the "Entity Graph and Search
/// Completeness" epic). An authoring role can author a new directed <see cref="EntityRelationship"/> edge
/// between two entities in a workspace; this service resolves BOTH endpoints within the caller's own
/// (organization, workspace), creates the edge with a SERVER-MINTED surrogate id and persists it. It makes the
/// entity-relationship graph authorable — until this story an edge could only be removed
/// (<c>DELETE .../entity-relationships/{relationshipId}</c>, CORE-LIFE-002), never created or read
/// (ARC-GAP-118).
///
/// SAME-WORKSPACE-ENDPOINTS coupling (the documented carry-over on <see cref="EntityRelationship"/> and
/// <see cref="IEntityRelationshipRepository"/>). An edge connects two entities, and the
/// <c>entity_relationships.source_entity_id</c> / <c>target_entity_id</c> foreign keys guarantee the endpoints
/// EXIST but NOT that they live in the edge's own workspace — exactly like <c>Entity.EntityTypeId</c> and
/// <c>ContentBlock.SceneId</c>. The create application flow is responsible for enforcing that coupling, so
/// this service resolves EACH endpoint through the tenant- AND workspace-scoped
/// <see cref="IEntityRepository.FindByIdAsync"/> FIRST: an endpoint in another workspace or tenant, or an
/// unknown endpoint, resolves to <see langword="null"/> and yields
/// <see cref="EntityRelationshipCreationStatus.UnknownSourceEntity"/> /
/// <see cref="EntityRelationshipCreationStatus.UnknownTargetEntity"/> — no edge is created. The surrogate id
/// alone never authorizes anything; every lookup is scoped by (organization, workspace), so an endpoint
/// reference can never reach across a workspace or tenant boundary (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace (the endpoint performed the
/// authentication, tenant resolution and role authorization before calling in; this service is the authorized
/// command's effect). The created edge is bound to exactly that (organization, workspace) — the
/// <see cref="EntityRelationship"/> aggregate fixes them immutably at construction — so it can never be
/// authored into another tenant or workspace (threat T5).
///
/// CONTENT BOUNDARY (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). The edge's
/// <see cref="EntityRelationship.RelationshipKind"/> is a template-/host-supplied canonical slug, validated
/// only for shape by the aggregate — never inspected for vocabulary and never branched on. The endpoint
/// validates the kind and the distinct-endpoints (no self-loop) structural rule before calling in, so the
/// aggregate factory does not throw here.
///
/// NO AUDIT / NO EVENT. Creating an edge records a recorded fact only; it adds no event and no audit record,
/// staying faithful to the CORE-ENT-003 / CORE-LIFE-002 precedent where adding and removing an edge emitted
/// neither (the edge model carries no visibility surface). So — unlike <see cref="EntityCreationService"/> —
/// this service composes no audit repository and needs no transaction: the single
/// <see cref="IEntityRelationshipRepository.AddAsync"/> insert is the whole effect.
/// </summary>
internal sealed class EntityRelationshipCreationService
{
    private readonly IEntityRepository _entities;
    private readonly IEntityRelationshipRepository _relationships;

    public EntityRelationshipCreationService(
        IEntityRepository entities,
        IEntityRelationshipRepository relationships)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        _entities = entities;
        _relationships = relationships;
    }

    /// <summary>
    /// Creates a new directed edge in the given tenant and workspace from <paramref name="sourceEntityId"/> to
    /// <paramref name="targetEntityId"/> of the given <paramref name="relationshipKind"/>. Returns
    /// <see cref="EntityRelationshipCreationResult.Created"/> carrying the created edge when both endpoints
    /// resolved within the (organization, workspace);
    /// <see cref="EntityRelationshipCreationResult.UnknownSourceEntity"/> /
    /// <see cref="EntityRelationshipCreationResult.UnknownTargetEntity"/> when an endpoint does not resolve
    /// there (an unknown id, or one belonging to another workspace/tenant — nothing is created); or
    /// <see cref="EntityRelationshipCreationResult.Duplicate"/> when the workspace already holds the same
    /// directed edge of the same kind.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the edge is authored into.</param>
    /// <param name="sourceEntityId">The source endpoint (resolved within the workspace).</param>
    /// <param name="targetEntityId">The target endpoint (resolved within the workspace).</param>
    /// <param name="relationshipKind">The edge's kind (a canonical slug; the endpoint validated its shape).</param>
    /// <param name="now">The command timestamp (the edge's created/updated time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// An id is empty, or the endpoint ids or kind violate an <see cref="EntityRelationship"/> invariant (the
    /// endpoint validates these before calling, so a throw here is defensive).
    /// </exception>
    public async Task<EntityRelationshipCreationResult> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        Guid targetEntityId,
        string relationshipKind,
        DateTimeOffset now,
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

        if (sourceEntityId == Guid.Empty)
        {
            throw new ArgumentException("Source entity id must not be empty.", nameof(sourceEntityId));
        }

        if (targetEntityId == Guid.Empty)
        {
            throw new ArgumentException("Target entity id must not be empty.", nameof(targetEntityId));
        }

        // SAME-WORKSPACE-ENDPOINTS coupling: resolve BOTH endpoints WITHIN the resolved tenant AND workspace.
        // FindByIdAsync leads with organization_id then workspace_id then the entity id, so an entity in
        // another workspace or tenant is never returned even when the surrogate id matches; an unknown id is
        // simply null. An unresolved endpoint creates nothing — the create is rejected as an unknown endpoint
        // (threats T1/T5). The source is resolved before the target only for a stable order; the response for
        // an unresolved endpoint leaks nothing about which side or which tenant it lives in.
        var source = await _entities
            .FindByIdAsync(organizationId, workspaceId, sourceEntityId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return EntityRelationshipCreationResult.UnknownSourceEntity;
        }

        var target = await _entities
            .FindByIdAsync(organizationId, workspaceId, targetEntityId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return EntityRelationshipCreationResult.UnknownTargetEntity;
        }

        // Both endpoints are in the caller's own workspace. The aggregate mints the server-side surrogate id
        // (UUIDv7), fixes the tenant, workspace and endpoints immutably, canonicalizes the kind and enforces
        // the structural invariants (the endpoint already validated the kind shape and the distinct-endpoints
        // rule, so Create does not throw here).
        var relationship = EntityRelationship.Create(
            organizationId, workspaceId, sourceEntityId, targetEntityId, relationshipKind, now);

        // Persist. A lost create race against the per-workspace unique edge index surfaces as Duplicate
        // (mapped to 409 by the endpoint), leaving the existing edge unchanged.
        var addResult = await _relationships.AddAsync(relationship, cancellationToken).ConfigureAwait(false);
        return addResult == EntityRelationshipAddResult.Duplicate
            ? EntityRelationshipCreationResult.Duplicate
            : EntityRelationshipCreationResult.Created(relationship);
    }
}
