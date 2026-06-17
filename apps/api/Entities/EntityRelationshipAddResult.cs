// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of persisting a new entity relationship (CORE-ENT-003).
///
/// Mirrors <see cref="EntityTypeAddResult"/>: an entity relationship has a per-workspace natural
/// key — the directed edge of a given kind — so an insert can be rejected as a DUPLICATE when the
/// workspace already holds the same edge. The (<c>workspace_id</c>, <c>source_entity_id</c>,
/// <c>target_entity_id</c>, <c>relationship_kind</c>) tuple is the per-workspace unique natural key
/// enforced by a unique database index, so a second writer can never create the same directed edge
/// of the same kind twice.
/// </summary>
public enum EntityRelationshipAddResult
{
    /// <summary>The entity relationship was persisted.</summary>
    Added = 1,

    /// <summary>
    /// The same directed edge of the same kind already exists in the same workspace. The existing
    /// relationship was not modified; the (<c>workspace_id</c>, <c>source_entity_id</c>,
    /// <c>target_entity_id</c>, <c>relationship_kind</c>) tuple is the per-workspace unique natural
    /// key, so a second writer can never create a duplicate edge (enforced by the unique database
    /// index). The same pair connected by a DIFFERENT kind, or the reverse edge, is still allowed
    /// (threat T5).
    /// </summary>
    Duplicate = 2,
}
