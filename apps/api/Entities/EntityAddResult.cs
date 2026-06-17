// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of persisting a new entity (CORE-ENT-002).
///
/// An entity has no natural key — it is identified only by its surrogate id — so there is no
/// uniqueness constraint that an insert could violate and therefore no "duplicate" outcome
/// (unlike <c>EntityTypeAddResult</c>, whose per-workspace unique (<c>workspace_id</c>,
/// <c>type_key</c>) index can reject a second writer). This enum mirrors
/// <c>ContentBlockAddResult</c>: it carries a single success value so the Add contract has the
/// same shape as the other modules' and can grow a second outcome later without a breaking
/// signature change; foreign-key violations (a non-existent entity type, workspace or tenant)
/// surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository
/// rather than as a result value.
/// </summary>
public enum EntityAddResult
{
    /// <summary>The entity was persisted.</summary>
    Added = 1,
}
