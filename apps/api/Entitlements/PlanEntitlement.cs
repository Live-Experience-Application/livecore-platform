// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// One granted entitlement of a <see cref="PlanDefinition"/> (CORE-ENTL-001): the binding that says "this plan
/// grants this <see cref="EntitlementDefinition"/> at this value". A plan entitlement is part of the plan
/// aggregate — it is never created or queried on its own, only ever through its owning
/// <see cref="PlanDefinition"/> (created together by <see cref="PlanDefinition.GrantFlag"/> /
/// <see cref="PlanDefinition.GrantQuota"/> and loaded with the plan), so it carries no global catalog identity
/// of its own beyond its surrogate id and the two ids it links.
///
/// VALUE SHAPE MATCHES THE DEFINITION. The granted value's shape is fixed by the referenced
/// <see cref="EntitlementDefinition.ValueKind"/>: a <see cref="EntitlementValueKind.Flag"/> entitlement is
/// granted a boolean <see cref="FlagValue"/> (and no quota limit); a <see cref="EntitlementValueKind.Quota"/>
/// entitlement is granted a numeric <see cref="QuotaLimit"/> — a non-negative cap, or <see langword="null"/>
/// meaning an UNLIMITED (fair-use) grant (and no flag value). The owning plan's grant factories enforce that
/// the value shape matches the definition's kind, so a plan can never bind a boolean to a quota or a number to
/// a flag; the value is decided server-side and never trusted from a client (docs/21 "Never trust client-side
/// premium flags"). The numeric quota's per-subject usage and the quota-status calculation are the later quota
/// story (CORE-ENTL-003); this story only records the granted limit.
///
/// IMMUTABLE. A grant is a recorded value, so its properties never change after creation; a plan changes its
/// grants by being re-defined, not by mutating a row in place (mirrors the immutable export manifest entry).
/// </summary>
public sealed class PlanEntitlement
{
    private PlanEntitlement(
        Guid id,
        Guid planDefinitionId,
        Guid entitlementDefinitionId,
        EntitlementValueKind valueKind,
        bool? flagValue,
        long? quotaLimit)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Plan entitlement id must not be empty.", nameof(id));
        }

        if (planDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Plan definition id must not be empty.", nameof(planDefinitionId));
        }

        if (entitlementDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(entitlementDefinitionId));
        }

        if (!Enum.IsDefined(valueKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueKind),
                valueKind,
                "Value kind is not a defined entitlement value kind.");
        }

        // The stored columns must be self-consistent with the value kind, so a row can never carry an
        // ambiguous value (a flag with a quota limit, or a quota with a boolean). The owning plan's grant
        // factories build only consistent values; this guard also rejects an inconsistent materialization.
        switch (valueKind)
        {
            case EntitlementValueKind.Flag:
                if (flagValue is null)
                {
                    throw new ArgumentException("A flag entitlement must carry a boolean value.", nameof(flagValue));
                }

                if (quotaLimit is not null)
                {
                    throw new ArgumentException("A flag entitlement must not carry a quota limit.", nameof(quotaLimit));
                }

                break;

            case EntitlementValueKind.Quota:
                if (flagValue is not null)
                {
                    throw new ArgumentException("A quota entitlement must not carry a flag value.", nameof(flagValue));
                }

                if (quotaLimit is < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(quotaLimit),
                        quotaLimit,
                        "A quota limit must not be negative; pass null for an unlimited (fair-use) grant.");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, "Unhandled value kind.");
        }

        Id = id;
        PlanDefinitionId = planDefinitionId;
        EntitlementDefinitionId = entitlementDefinitionId;
        ValueKind = valueKind;
        FlagValue = flagValue;
        QuotaLimit = quotaLimit;
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PlanEntitlement()
    {
    }

    /// <summary>Surrogate key of the grant row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The id of the <see cref="PlanDefinition"/> this grant belongs to (the <c>plan_definition_id</c> foreign
    /// key into <c>plan_definitions</c>, cascade on delete). Immutable; a grant never moves to another plan.
    /// </summary>
    public Guid PlanDefinitionId { get; }

    /// <summary>
    /// The id of the <see cref="EntitlementDefinition"/> this grant is for (the <c>entitlement_definition_id</c>
    /// foreign key into <c>entitlement_definitions</c>, restricted on delete so a referenced definition cannot
    /// be hard-deleted). Immutable.
    /// </summary>
    public Guid EntitlementDefinitionId { get; }

    /// <summary>
    /// The value kind of the granted entitlement, recorded on the grant so the row is self-describing. It
    /// always equals the referenced definition's <see cref="EntitlementDefinition.ValueKind"/> (enforced by
    /// the owning plan's grant factories).
    /// </summary>
    public EntitlementValueKind ValueKind { get; }

    /// <summary>
    /// The granted boolean value for a <see cref="EntitlementValueKind.Flag"/> entitlement; <see langword="null"/>
    /// for a quota entitlement.
    /// </summary>
    public bool? FlagValue { get; }

    /// <summary>
    /// The granted numeric cap for a <see cref="EntitlementValueKind.Quota"/> entitlement — a non-negative
    /// limit, or <see langword="null"/> meaning an UNLIMITED (fair-use) grant; always <see langword="null"/>
    /// for a flag entitlement.
    /// </summary>
    public long? QuotaLimit { get; }

    /// <summary>Whether this grant is a boolean flag.</summary>
    public bool IsFlag => ValueKind == EntitlementValueKind.Flag;

    /// <summary>Whether this grant is a numeric quota.</summary>
    public bool IsQuota => ValueKind == EntitlementValueKind.Quota;

    /// <summary>
    /// Whether this is an UNLIMITED (fair-use) quota grant — a quota entitlement with no numeric cap. Always
    /// false for a flag grant.
    /// </summary>
    public bool IsUnlimitedQuota => IsQuota && QuotaLimit is null;

    /// <summary>
    /// Builds a flag grant for the given plan and entitlement definition. Internal to the Entitlements module:
    /// grants are only ever built by <see cref="PlanDefinition"/> while validating the definition's kind.
    /// </summary>
    internal static PlanEntitlement CreateFlag(Guid planDefinitionId, Guid entitlementDefinitionId, bool value)
        => new(Guid.CreateVersion7(), planDefinitionId, entitlementDefinitionId, EntitlementValueKind.Flag, value, quotaLimit: null);

    /// <summary>
    /// Builds a quota grant for the given plan and entitlement definition. A <see langword="null"/> limit is an
    /// unlimited (fair-use) grant; a non-null limit must be non-negative. Internal to the Entitlements module.
    /// </summary>
    internal static PlanEntitlement CreateQuota(Guid planDefinitionId, Guid entitlementDefinitionId, long? limit)
        => new(Guid.CreateVersion7(), planDefinitionId, entitlementDefinitionId, EntitlementValueKind.Quota, flagValue: null, limit);

    /// <summary>Identifier-only representation that is safe for structured logs (threat T7).</summary>
    public override string ToString()
        => $"PlanEntitlement {Id} plan={PlanDefinitionId} entitlement={EntitlementDefinitionId} "
            + $"kind={ValueKind} flag={(FlagValue is { } flag ? flag.ToString() : "-")} "
            + $"quota={(IsUnlimitedQuota ? "unlimited" : QuotaLimit?.ToString() ?? "-")}";
}
