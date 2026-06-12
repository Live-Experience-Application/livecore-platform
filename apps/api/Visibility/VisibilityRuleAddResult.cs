namespace LiveCore.Api.Visibility;

/// <summary>
/// Outcome of persisting a new visibility rule (CORE-VIS-001).
///
/// A visibility rule has no enforced natural key — the documented critical index
/// <c>visibility_rules(workspace_id, resource_type, resource_id)</c> is NON-unique
/// (docs/10_DATABASE_SCHEMA.md), because a resource may accumulate more than one rule over its life
/// (the base audience rule now; per-participant scoped rules in the later selected-participant
/// reveal, CORE-VIS-005) — so there is no uniqueness constraint that an insert could violate and
/// therefore no "duplicate" outcome (unlike <c>EntityTypeAddResult</c>, whose per-workspace unique
/// index can reject a second writer). This enum mirrors <c>EntityAddResult</c>/
/// <c>ContentBlockAddResult</c>: it carries a single success value so the Add contract has the same
/// shape as the other modules' and can grow a second outcome later without a breaking signature
/// change; foreign-key violations (a non-existent workspace or tenant) surface as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as
/// a result value.
/// </summary>
public enum VisibilityRuleAddResult
{
    /// <summary>The visibility rule was persisted.</summary>
    Added = 1,
}
