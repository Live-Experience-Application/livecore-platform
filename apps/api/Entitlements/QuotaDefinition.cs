// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Quota DEFINITION aggregate of the Entitlements module (CORE-ENTL-003, the quota definition and quota status
/// story of the "Entitlements and Quotas" epic).
///
/// A <see cref="QuotaDefinition"/> is the Core-owned, product-neutral catalog entry that says HOW a numeric
/// <see cref="EntitlementValueKind.Quota"/> entitlement is measured for a subject: which generic quota
/// entitlement it caps (<see cref="EntitlementKey"/> / <see cref="EntitlementDefinitionId"/>), the kind of
/// subject the usage is counted FOR (<see cref="SubjectType"/> — a user or a workspace) and the
/// <see cref="QuotaUnit"/> the count is expressed in. CORE-ENTL-001 modeled WHAT entitlements exist and their
/// value shape (flag vs quota); CORE-ENTL-002 assigned a concrete LIMIT to a subject; this story adds the
/// measurement semantics and the server-side <see cref="QuotaStatus"/> calculation that compares a subject's
/// recorded <see cref="QuotaUsage"/> against its granted limit (the epic acceptance criterion: "Quota status is
/// calculated server-side for subjects and workspaces").
///
/// GENERIC AND GLOBAL — NOT TENANT CONTENT. Like <see cref="EntitlementDefinition"/>, the quota definition is the
/// deployment-wide monetization catalog (mirrors the global <c>entitlement_definitions</c>/<c>plan_definitions</c>
/// tables), so it carries NO <c>organization_id</c> and is not tenant-scoped: there is no per-tenant copy and no
/// audience to project away. The measured entitlement's key is GENERIC Core vocabulary (the epic's CORE-ENTL-001
/// acceptance criterion "Generic entitlements can be defined without vertical terminology"; AGENTS.md), so the
/// model holds no vertical product language.
///
/// SELF-DESCRIBING, REFERENTIALLY SOUND. The definition references a <see cref="EntitlementValueKind.Quota"/>
/// <see cref="EntitlementDefinition"/>: the grant factory copies the definition's stable
/// <see cref="EntitlementKey"/> onto the row as an immutable RECORDED FACT (the definition key is immutable, so it
/// can never drift; mirrors <see cref="SubjectEntitlement.EntitlementKey"/>), so a quota-status lookup is
/// join-free, while the <see cref="EntitlementDefinitionId"/> foreign key still guarantees the measured
/// entitlement exists and is a quota (a referenced definition cannot be hard-deleted out from under a quota
/// definition; it is soft-retired via <see cref="EntitlementDefinition.IsActive"/> instead). A non-quota
/// entitlement can never be measured: the factory rejects a flag entitlement, so a quota definition can never bind
/// to a boolean capability.
///
/// SOFT LIFECYCLE, NOT HARD DELETE. A quota definition is business data that usage rows reference, so it is never
/// hard-deleted; it is retired with <see cref="Deactivate"/> (and brought back with <see cref="Reactivate"/>),
/// keeping existing usage resolvable (docs/10_DATABASE_SCHEMA.md: "avoid hard-delete for business data; use soft
/// delete where needed"). The measured entitlement, the subject type and the unit are immutable for the lifetime
/// of the definition, so a quota can never silently change what it measures or for whom.
/// </summary>
public sealed class QuotaDefinition
{
    private QuotaDefinition(
        Guid id,
        Guid entitlementDefinitionId,
        string entitlementKey,
        EntitlementSubjectType subjectType,
        QuotaUnit unit,
        bool isActive,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Quota definition id must not be empty.", nameof(id));
        }

        if (entitlementDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(entitlementDefinitionId));
        }

        if (!EntitlementDefinition.IsValidKey(entitlementKey))
        {
            throw new ArgumentException("Entitlement key violates the entitlement key invariants.", nameof(entitlementKey));
        }

        if (!Enum.IsDefined(subjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectType),
                subjectType,
                "Subject type is not a defined entitlement subject type.");
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unit is not a defined quota unit.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A quota definition cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        EntitlementDefinitionId = entitlementDefinitionId;
        EntitlementKey = entitlementKey;
        SubjectType = subjectType;
        Unit = unit;
        IsActive = isActive;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private QuotaDefinition()
    {
        // Non-null reference default for the EF materialization path; the real value is set from the stored
        // column.
        EntitlementKey = null!;
    }

    /// <summary>Surrogate key of the quota-definition row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The id of the measured <see cref="EntitlementDefinition"/> (the <c>entitlement_definition_id</c> foreign
    /// key into <c>entitlement_definitions</c>, RESTRICTED on delete so a referenced definition cannot be
    /// hard-deleted out from under a quota definition). Immutable.
    /// </summary>
    public Guid EntitlementDefinitionId { get; }

    /// <summary>
    /// The stable, lower-case dotted <see cref="EntitlementDefinition.Key"/> of the measured quota entitlement,
    /// denormalized onto the quota definition as an immutable RECORDED FACT so the row is self-describing and the
    /// quota-status lookup is join-free. Always equals the referenced definition's key (copied by
    /// <see cref="Define"/>); the definition key is immutable, so it can never drift. Immutable.
    /// </summary>
    public string EntitlementKey { get; }

    /// <summary>
    /// The kind of subject the quota's usage is measured FOR — a <see cref="EntitlementSubjectType.User"/> (its
    /// status is surfaced on <c>GET /api/v1/me/quota-status</c>) or a <see cref="EntitlementSubjectType.Workspace"/>
    /// (surfaced on <c>GET /api/v1/workspaces/{workspaceId}/quota-status</c>). Immutable.
    /// </summary>
    public EntitlementSubjectType SubjectType { get; }

    /// <summary>The unit the quota's usage and limit are expressed in (a count or bytes). Immutable.</summary>
    public QuotaUnit Unit { get; }

    /// <summary>
    /// Whether the quota definition is currently in force. A retired (<see cref="Deactivate"/>d) definition stays
    /// in the catalog so existing usage resolves, but is no longer reported in quota status (soft delete;
    /// docs/10_DATABASE_SCHEMA.md).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When this quota definition was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this quota definition was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Defines a new, active quota measured against the given quota entitlement, for the given subject kind and
    /// unit. The definition must be an active <see cref="EntitlementValueKind.Quota"/> entitlement; its stable
    /// key and id are copied onto the quota definition. A flag entitlement is rejected: a boolean capability has
    /// no numeric usage to measure.
    /// </summary>
    /// <param name="entitlement">The active quota entitlement definition this quota measures.</param>
    /// <param name="subjectType">The kind of subject the quota's usage is measured for.</param>
    /// <param name="unit">The unit the quota's usage and limit are expressed in.</param>
    /// <param name="createdAt">When the quota definition is created.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entitlement"/> is null.</exception>
    /// <exception cref="ArgumentException">The entitlement is not an active quota entitlement.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The subject type or unit is not a defined value.</exception>
    public static QuotaDefinition Define(
        EntitlementDefinition entitlement,
        EntitlementSubjectType subjectType,
        QuotaUnit unit,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        // A quota measures a numeric quota entitlement; a flag (boolean capability) has no usage to count, so it
        // can never be the subject of a quota definition (mirrors SubjectEntitlement.ValidateAssignable).
        if (entitlement.ValueKind != EntitlementValueKind.Quota)
        {
            throw new ArgumentException(
                $"Entitlement '{entitlement.Key}' is a {entitlement.ValueKind}, not a Quota; only a quota entitlement can be measured.",
                nameof(entitlement));
        }

        // A retired entitlement is no longer offered, so a quota cannot be newly defined against it (fail-closed).
        if (!entitlement.IsActive)
        {
            throw new ArgumentException(
                $"Entitlement '{entitlement.Key}' is inactive and cannot be measured by a quota.",
                nameof(entitlement));
        }

        return new QuotaDefinition(
            Guid.CreateVersion7(),
            entitlement.Id,
            entitlement.Key,
            subjectType,
            unit,
            isActive: true,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Retires the quota definition (soft delete): it stays in the catalog so existing usage resolves, but is no
    /// longer reported in quota status. Idempotent — deactivating an already-inactive definition only refreshes
    /// the timestamp.
    /// </summary>
    public void Deactivate(DateTimeOffset updatedAt)
    {
        IsActive = false;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Brings a retired quota definition back into the offered catalog. Idempotent — reactivating an
    /// already-active definition only refreshes the timestamp.
    /// </summary>
    public void Reactivate(DateTimeOffset updatedAt)
    {
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Whether this quota definition is measured for exactly the given subject kind.</summary>
    public bool IsForSubjectType(EntitlementSubjectType subjectType) => SubjectType == subjectType;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: definition id, measured entitlement key,
    /// subject type, unit and active flag — identifiers and enums only, never any free-form content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"QuotaDefinition {Id} key={EntitlementKey} subjectType={SubjectType} unit={Unit} active={IsActive}";
}
