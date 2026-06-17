// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Outcome of persisting a new visibility rule (CORE-VIS-001; <see cref="Duplicate"/> added by
/// CORE-SVIS-002).
///
/// A visibility rule is unique per (session, resource, DIMENSION): the filtered unique indexes on
/// <c>visibility_rules</c> (audience-wide where <c>target_participant_id IS NULL</c>, and per-participant
/// where <c>target_participant_id IS NOT NULL</c>; docs/10_DATABASE_SCHEMA.md, CORE-SVIS-002) allow at
/// most one active rule in each dimension. So a second insert in the SAME dimension is rejected by the
/// database and surfaces as <see cref="Duplicate"/> (typically a concurrent first-reveal losing the
/// create race) rather than creating a second, un-hideable ghost rule. This mirrors
/// <c>EntityTypeAddResult</c>/<c>QuotaUsageAddResult</c>, whose unique indexes likewise report a
/// duplicate. Foreign-key violations (a non-existent session, workspace or tenant) still surface as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a
/// result value.
/// </summary>
public enum VisibilityRuleAddResult
{
    /// <summary>The visibility rule was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A rule already exists for the same (session, resource, dimension): the filtered unique index
    /// (CORE-SVIS-002) rejected the insert. The caller treats this as a lost create race and converges
    /// onto the existing rule rather than creating a duplicate.
    /// </summary>
    Duplicate = 2,
}
