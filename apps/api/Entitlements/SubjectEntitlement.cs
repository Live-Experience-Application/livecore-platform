// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Subject entitlement ASSIGNMENT aggregate of the Entitlements module (CORE-ENTL-002, the subject entitlement
/// assignment and lookup story of the "Entitlements and Quotas" epic).
///
/// A <see cref="SubjectEntitlement"/> is the server-side record that ONE generic subject — a
/// <see cref="EntitlementSubjectType.User"/> or a <see cref="EntitlementSubjectType.Workspace"/> — holds a
/// granted <see cref="EntitlementDefinition"/> at a concrete value. CORE-ENTL-001 modeled the GLOBAL catalog
/// (what entitlements and plans exist); this story models the PER-SUBJECT assignment of those entitlements and
/// the server-side lookup that resolves them into a subject's effective premium state.
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server entitlements". Premium
/// state is whatever the server has RECORDED here; there is no client input. A subject with no active
/// assignment for an entitlement is simply NOT entitled (fail-closed default), and a revoked assignment (a
/// refund, cancellation or downgrade) is likewise not entitled — so a client can never grant itself premium by
/// asserting a flag (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Never trust client-side premium
/// flags"; "User-visible premium state must come from server entitlements"). The resolver
/// (<see cref="SubjectEntitlementResolver"/>) reads exactly these rows; nothing else feeds the premium state.
///
/// SELF-DESCRIBING VALUE. The granted value's shape is fixed by the referenced
/// <see cref="EntitlementDefinition.ValueKind"/> (validated by the grant factories): a
/// <see cref="EntitlementValueKind.Flag"/> entitlement carries a boolean <see cref="FlagValue"/> (and no quota
/// limit); a <see cref="EntitlementValueKind.Quota"/> entitlement carries a numeric <see cref="QuotaLimit"/> —
/// a non-negative cap, or <see langword="null"/> meaning an UNLIMITED (fair-use) grant (and no flag value).
/// This mirrors <see cref="PlanEntitlement"/>'s value shape, so an assignment can never bind a boolean to a
/// quota or a number to a flag. The stable <see cref="EntitlementKey"/> is denormalized onto the row as a
/// RECORDED FACT (the definition's key is immutable, so it can never drift; mirrors how the audit log and
/// session events record the referenced identity as a stable fact): the row is therefore self-describing — it
/// directly states "this subject holds entitlement KEY at this value" — so the hot-path lookup never needs a
/// join, while the <see cref="EntitlementDefinitionId"/> foreign key still guarantees referential integrity (a
/// referenced definition cannot be hard-deleted out from under an assignment; it is soft-retired via
/// <see cref="EntitlementDefinition.IsActive"/> instead).
///
/// PER-SUBJECT, NOT TENANT CONTENT. The assignment is keyed by the pair (<see cref="SubjectType"/>,
/// <see cref="SubjectId"/>); the subject id is a generic polymorphic reference (no database foreign key,
/// mirroring <c>asset_links.target_id</c>, <c>visibility_rules.resource_id</c> and
/// <c>session_events.visibility_subject_id</c>), so isolation is by the subject pair: one subject's premium
/// state can never be read through another subject's id, and a <see cref="EntitlementSubjectType.User"/> and a
/// <see cref="EntitlementSubjectType.Workspace"/> that happen to share a guid never collide. A subject holds
/// each entitlement AT MOST ONCE (the unique <c>subject_entitlements(subject_type, subject_id,
/// entitlement_definition_id)</c> index).
///
/// LIFECYCLE. An assignment is granted active and is revoked (not deleted) on a refund / cancellation /
/// downgrade so the trail of what a subject was granted is retained (docs/21 "Refunds and chargebacks must
/// revoke or downgrade entitlements"); a revoked assignment resolves to NOT entitled. The optional
/// <see cref="SourcePlanDefinitionId"/> records the plan an assignment was granted from (provenance for a later
/// plan-level downgrade), or <see langword="null"/> for a directly granted entitlement.
/// </summary>
public sealed class SubjectEntitlement
{
    private SubjectEntitlement(
        Guid id,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid entitlementDefinitionId,
        string entitlementKey,
        EntitlementValueKind valueKind,
        bool? flagValue,
        long? quotaLimit,
        Guid? sourcePlanDefinitionId,
        bool isActive,
        DateTimeOffset grantedAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Subject entitlement id must not be empty.", nameof(id));
        }

        if (!Enum.IsDefined(subjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectType),
                subjectType,
                "Subject type is not a defined entitlement subject type.");
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (entitlementDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(entitlementDefinitionId));
        }

        if (!EntitlementDefinition.IsValidKey(entitlementKey))
        {
            throw new ArgumentException("Entitlement key violates the entitlement key invariants.", nameof(entitlementKey));
        }

        if (!Enum.IsDefined(valueKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueKind),
                valueKind,
                "Value kind is not a defined entitlement value kind.");
        }

        if (sourcePlanDefinitionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Source plan definition id must be null (directly granted) or a non-empty id.",
                nameof(sourcePlanDefinitionId));
        }

        // The stored value columns must be self-consistent with the value kind, so a row can never carry an
        // ambiguous value (a flag with a quota limit, or a quota with a boolean). The grant factories build
        // only consistent values; this guard also rejects an inconsistent materialization (mirrors
        // PlanEntitlement).
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

        if (updatedAt < grantedAt)
        {
            throw new ArgumentException(
                "A subject entitlement cannot be updated before it was granted.",
                nameof(updatedAt));
        }

        Id = id;
        SubjectType = subjectType;
        SubjectId = subjectId;
        EntitlementDefinitionId = entitlementDefinitionId;
        EntitlementKey = entitlementKey;
        ValueKind = valueKind;
        FlagValue = flagValue;
        QuotaLimit = quotaLimit;
        SourcePlanDefinitionId = sourcePlanDefinitionId;
        IsActive = isActive;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        GrantedAt = grantedAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private SubjectEntitlement()
    {
        // Non-null reference default for the EF materialization path; the real value is set from the stored
        // column.
        EntitlementKey = null!;
    }

    /// <summary>Surrogate key of the assignment row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>The kind of subject this entitlement is assigned to (a user or a workspace).</summary>
    public EntitlementSubjectType SubjectType { get; }

    /// <summary>
    /// The id of the subject (a <c>users(id)</c> id for a user subject, a <c>workspaces(id)</c> id for a
    /// workspace subject). A generic POLYMORPHIC reference (no database foreign key), so isolation is by the
    /// (<see cref="SubjectType"/>, <see cref="SubjectId"/>) pair. Immutable.
    /// </summary>
    public Guid SubjectId { get; }

    /// <summary>
    /// The id of the granted <see cref="EntitlementDefinition"/> (the <c>entitlement_definition_id</c> foreign
    /// key into <c>entitlement_definitions</c>, RESTRICTED on delete so a referenced definition cannot be
    /// hard-deleted out from under an assignment). Immutable.
    /// </summary>
    public Guid EntitlementDefinitionId { get; }

    /// <summary>
    /// The stable, lower-case dotted <see cref="EntitlementDefinition.Key"/> of the granted entitlement,
    /// denormalized onto the assignment as an immutable RECORDED FACT so the row is self-describing and the
    /// premium-state lookup is join-free. Always equals the referenced definition's key (copied by the grant
    /// factories); the definition key is immutable, so it can never drift. Immutable.
    /// </summary>
    public string EntitlementKey { get; }

    /// <summary>
    /// The value kind of the granted entitlement, recorded on the row so it is self-describing. Always equals
    /// the referenced definition's <see cref="EntitlementDefinition.ValueKind"/> (enforced by the grant
    /// factories). Immutable.
    /// </summary>
    public EntitlementValueKind ValueKind { get; }

    /// <summary>
    /// The granted boolean value for a <see cref="EntitlementValueKind.Flag"/> entitlement; <see langword="null"/>
    /// for a quota entitlement.
    /// </summary>
    public bool? FlagValue { get; private set; }

    /// <summary>
    /// The granted numeric cap for a <see cref="EntitlementValueKind.Quota"/> entitlement — a non-negative
    /// limit, or <see langword="null"/> meaning an UNLIMITED (fair-use) grant; always <see langword="null"/>
    /// for a flag entitlement.
    /// </summary>
    public long? QuotaLimit { get; private set; }

    /// <summary>
    /// The id of the <see cref="PlanDefinition"/> this assignment was granted FROM (the optional
    /// <c>source_plan_definition_id</c> foreign key into <c>plan_definitions</c>, SET NULL on delete), or
    /// <see langword="null"/> for a directly granted entitlement. Provenance for a later plan-level downgrade;
    /// it never participates in the premium-state resolution.
    /// </summary>
    public Guid? SourcePlanDefinitionId { get; private set; }

    /// <summary>
    /// Whether the assignment is currently in force. A revoked (<see cref="Revoke"/>d) assignment stays as a
    /// retained record but resolves to NOT entitled (the fail-closed premium-state default; docs/21 "Refunds
    /// and chargebacks must revoke or downgrade entitlements").
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When this entitlement was first granted to the subject (UTC).</summary>
    public DateTimeOffset GrantedAt { get; }

    /// <summary>When this assignment was last changed — value, source plan or lifecycle (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether this assignment is for a boolean flag entitlement.</summary>
    public bool IsFlag => ValueKind == EntitlementValueKind.Flag;

    /// <summary>Whether this assignment is for a numeric quota entitlement.</summary>
    public bool IsQuota => ValueKind == EntitlementValueKind.Quota;

    /// <summary>
    /// Whether this is an UNLIMITED (fair-use) quota assignment — a quota entitlement with no numeric cap.
    /// Always false for a flag assignment.
    /// </summary>
    public bool IsUnlimitedQuota => IsQuota && QuotaLimit is null;

    /// <summary>
    /// Assigns a boolean FLAG entitlement to a subject. The definition must be an active
    /// <see cref="EntitlementValueKind.Flag"/> entitlement; its stable key and id are copied onto the
    /// assignment. The granted value is recorded server-side and is the only source of the subject's premium
    /// state for this entitlement (docs/21 "Never trust client-side premium flags").
    /// </summary>
    /// <param name="subjectType">The kind of subject the entitlement is assigned to.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="definition">The active flag entitlement definition to assign.</param>
    /// <param name="value">The boolean value granted to the subject.</param>
    /// <param name="sourcePlanDefinitionId">The plan the assignment was granted from, or null for a direct grant.</param>
    /// <param name="grantedAt">When the entitlement is granted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The subject id is empty, the source plan id is empty, or the definition is not an active flag.</exception>
    public static SubjectEntitlement GrantFlag(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        EntitlementDefinition definition,
        bool value,
        Guid? sourcePlanDefinitionId,
        DateTimeOffset grantedAt)
    {
        var entitlementId = ValidateAssignable(definition, EntitlementValueKind.Flag);
        return new SubjectEntitlement(
            Guid.CreateVersion7(),
            subjectType,
            subjectId,
            entitlementId,
            definition.Key,
            EntitlementValueKind.Flag,
            value,
            quotaLimit: null,
            sourcePlanDefinitionId,
            isActive: true,
            grantedAt,
            grantedAt);
    }

    /// <summary>
    /// Assigns a numeric QUOTA entitlement to a subject. The definition must be an active
    /// <see cref="EntitlementValueKind.Quota"/> entitlement; its stable key and id are copied onto the
    /// assignment. A <see langword="null"/> limit is an UNLIMITED (fair-use) grant; a non-null limit must be
    /// non-negative.
    /// </summary>
    /// <param name="subjectType">The kind of subject the entitlement is assigned to.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="definition">The active quota entitlement definition to assign.</param>
    /// <param name="limit">The numeric cap granted, or null for an unlimited (fair-use) grant.</param>
    /// <param name="sourcePlanDefinitionId">The plan the assignment was granted from, or null for a direct grant.</param>
    /// <param name="grantedAt">When the entitlement is granted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The subject id is empty, the source plan id is empty, or the definition is not an active quota.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
    public static SubjectEntitlement GrantQuota(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        EntitlementDefinition definition,
        long? limit,
        Guid? sourcePlanDefinitionId,
        DateTimeOffset grantedAt)
    {
        var entitlementId = ValidateAssignable(definition, EntitlementValueKind.Quota);
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "A quota limit must not be negative; pass null for an unlimited (fair-use) grant.");
        }

        return new SubjectEntitlement(
            Guid.CreateVersion7(),
            subjectType,
            subjectId,
            entitlementId,
            definition.Key,
            EntitlementValueKind.Quota,
            flagValue: null,
            limit,
            sourcePlanDefinitionId,
            isActive: true,
            grantedAt,
            grantedAt);
    }

    /// <summary>
    /// Re-applies a FLAG grant's value to this (existing) assignment: sets the boolean value, records the
    /// source plan and brings the assignment back into force. Used when re-assigning a plan to a subject that
    /// already holds the entitlement (an upgrade or a re-grant after revocation). The value kind is immutable,
    /// so this is rejected on a quota assignment.
    /// </summary>
    /// <exception cref="InvalidOperationException">This assignment is not a flag.</exception>
    /// <exception cref="ArgumentException">The source plan id is empty.</exception>
    public void ApplyFlagGrant(bool value, Guid? sourcePlanDefinitionId, DateTimeOffset updatedAt)
    {
        if (!IsFlag)
        {
            throw new InvalidOperationException(
                $"Subject entitlement '{EntitlementKey}' is a {ValueKind}, not a Flag; cannot apply a flag value.");
        }

        if (sourcePlanDefinitionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Source plan definition id must be null (directly granted) or a non-empty id.",
                nameof(sourcePlanDefinitionId));
        }

        FlagValue = value;
        SourcePlanDefinitionId = sourcePlanDefinitionId;
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Re-applies a QUOTA grant's limit to this (existing) assignment: sets the numeric cap (or null for an
    /// unlimited grant), records the source plan and brings the assignment back into force. Used when
    /// re-assigning a plan to a subject that already holds the entitlement. The value kind is immutable, so this
    /// is rejected on a flag assignment.
    /// </summary>
    /// <exception cref="InvalidOperationException">This assignment is not a quota.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
    /// <exception cref="ArgumentException">The source plan id is empty.</exception>
    public void ApplyQuotaGrant(long? limit, Guid? sourcePlanDefinitionId, DateTimeOffset updatedAt)
    {
        if (!IsQuota)
        {
            throw new InvalidOperationException(
                $"Subject entitlement '{EntitlementKey}' is a {ValueKind}, not a Quota; cannot apply a quota limit.");
        }

        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "A quota limit must not be negative; pass null for an unlimited (fair-use) grant.");
        }

        if (sourcePlanDefinitionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Source plan definition id must be null (directly granted) or a non-empty id.",
                nameof(sourcePlanDefinitionId));
        }

        QuotaLimit = limit;
        SourcePlanDefinitionId = sourcePlanDefinitionId;
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Revokes the assignment (a refund, cancellation or downgrade): it stays as a retained record but resolves
    /// to NOT entitled, so the subject loses the premium state (docs/21). Idempotent — revoking an
    /// already-revoked assignment only refreshes the timestamp.
    /// </summary>
    public void Revoke(DateTimeOffset updatedAt)
    {
        IsActive = false;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Brings a revoked assignment back into force (a renewal or re-grant). Idempotent — reinstating an
    /// already-active assignment only refreshes the timestamp.
    /// </summary>
    public void Reinstate(DateTimeOffset updatedAt)
    {
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether this assignment belongs to exactly the given subject. Matching is exact on the
    /// (<see cref="SubjectType"/>, <see cref="SubjectId"/>) pair; an empty id matches nothing.
    /// </summary>
    public bool IsForSubject(EntitlementSubjectType subjectType, Guid subjectId)
        => subjectId != Guid.Empty && SubjectType == subjectType && SubjectId == subjectId;

    private static Guid ValidateAssignable(EntitlementDefinition definition, EntitlementValueKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // The granted value shape is fixed by the entitlement definition: an assignment can never bind the
        // wrong value shape to an entitlement (docs/21). The value is decided server-side.
        if (definition.ValueKind != expectedKind)
        {
            throw new ArgumentException(
                $"Entitlement '{definition.Key}' is a {definition.ValueKind}, not a {expectedKind}.",
                nameof(definition));
        }

        // A retired entitlement is no longer offered, so it may not be newly assigned to a subject.
        if (!definition.IsActive)
        {
            throw new ArgumentException(
                $"Entitlement '{definition.Key}' is inactive and cannot be assigned.",
                nameof(definition));
        }

        return definition.Id;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: assignment id, subject, entitlement
    /// key, value kind, the granted value and the active flag — identifiers and enums/values only, never any
    /// free-form content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"SubjectEntitlement {Id} subject={SubjectType}:{SubjectId} key={EntitlementKey} "
            + $"kind={ValueKind} flag={(FlagValue is { } flag ? flag.ToString() : "-")} "
            + $"quota={(IsUnlimitedQuota ? "unlimited" : QuotaLimit?.ToString() ?? "-")} active={IsActive}";
}
