namespace LiveCore.Api.Entitlements;

/// <summary>
/// Quota USAGE aggregate of the Entitlements module (CORE-ENTL-003, the quota definition and quota status story
/// of the "Entitlements and Quotas" epic).
///
/// A <see cref="QuotaUsage"/> is the server-side record of how much of a <see cref="QuotaDefinition"/> ONE generic
/// subject — a <see cref="EntitlementSubjectType.User"/> or a <see cref="EntitlementSubjectType.Workspace"/> —
/// currently consumes (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions": <c>quota_usage</c>).
/// It is the "used" half of the server-side <see cref="QuotaStatus"/> calculation: the status compares this
/// recorded amount against the subject's granted limit (its <see cref="SubjectEntitlement"/> quota value) to
/// answer "how much is left?" — the epic acceptance criterion "Quota status is calculated server-side for
/// subjects and workspaces".
///
/// SERVER-AUTHORITATIVE, FAIL-CLOSED. The amount is whatever the server has RECORDED here; there is no client
/// input (docs/21 "Never trust client-side premium flags"). A subject with no usage row for a quota consumes
/// NOTHING (the absence is read as zero by the calculator), so a status can never under-report usage from a
/// missing row. INCREMENTING this amount as protected workspace/session commands run, and ENFORCING the limit at
/// those commands, are the next story (CORE-ENTL-004); this story records the usage and computes the status from
/// it.
///
/// PER-SUBJECT, NOT TENANT CONTENT. The usage is keyed by the pair (<see cref="SubjectType"/>,
/// <see cref="SubjectId"/>); the subject id is a generic polymorphic reference (no database foreign key, mirroring
/// <see cref="SubjectEntitlement.SubjectId"/>, <c>asset_links.target_id</c> and
/// <c>visibility_rules.resource_id</c>), so isolation is by the subject pair: one subject's usage can never be
/// read through another subject's id, and a user subject and a workspace subject that share a guid never collide.
/// A subject records each quota AT MOST ONCE (the unique <c>quota_usage(subject_type, subject_id,
/// quota_definition_id)</c> index). The measured quota's stable <see cref="EntitlementKey"/> is denormalized onto
/// the row as an immutable RECORDED FACT so the row is self-describing for logs; the <see cref="QuotaDefinitionId"/>
/// foreign key still guarantees the quota exists (RESTRICTED on delete — a quota definition is soft-retired, not
/// hard-deleted, so usage can never dangle).
/// </summary>
public sealed class QuotaUsage
{
    private QuotaUsage(
        Guid id,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid quotaDefinitionId,
        string entitlementKey,
        long usedAmount,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Quota usage id must not be empty.", nameof(id));
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

        if (quotaDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Quota definition id must not be empty.", nameof(quotaDefinitionId));
        }

        if (!EntitlementDefinition.IsValidKey(entitlementKey))
        {
            throw new ArgumentException("Entitlement key violates the entitlement key invariants.", nameof(entitlementKey));
        }

        if (usedAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usedAmount),
                usedAmount,
                "A used amount must not be negative.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A quota usage cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        SubjectType = subjectType;
        SubjectId = subjectId;
        QuotaDefinitionId = quotaDefinitionId;
        EntitlementKey = entitlementKey;
        UsedAmount = usedAmount;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private QuotaUsage()
    {
        // Non-null reference default for the EF materialization path; the real value is set from the stored
        // column.
        EntitlementKey = null!;
    }

    /// <summary>Surrogate key of the usage row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>The kind of subject this usage is recorded for (a user or a workspace).</summary>
    public EntitlementSubjectType SubjectType { get; }

    /// <summary>
    /// The id of the subject (a <c>users(id)</c> id for a user subject, a <c>workspaces(id)</c> id for a
    /// workspace subject). A generic POLYMORPHIC reference (no database foreign key), so isolation is by the
    /// (<see cref="SubjectType"/>, <see cref="SubjectId"/>) pair. Immutable.
    /// </summary>
    public Guid SubjectId { get; }

    /// <summary>
    /// The id of the measured <see cref="QuotaDefinition"/> (the <c>quota_definition_id</c> foreign key into
    /// <c>quota_definitions</c>, RESTRICTED on delete). Immutable.
    /// </summary>
    public Guid QuotaDefinitionId { get; }

    /// <summary>
    /// The stable, lower-case dotted <see cref="EntitlementDefinition.Key"/> of the measured quota entitlement,
    /// denormalized onto the usage row as an immutable RECORDED FACT so the row is self-describing for logs.
    /// Always equals the quota definition's key (copied by <see cref="Start"/>). Immutable.
    /// </summary>
    public string EntitlementKey { get; }

    /// <summary>The current recorded usage amount (non-negative), in the quota definition's unit.</summary>
    public long UsedAmount { get; private set; }

    /// <summary>When this usage row was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this usage amount was last recorded (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Starts tracking usage of the given quota for a subject at zero. The quota definition's stable key and id
    /// are copied onto the row. A subject records each quota at most once (the unique per-subject index), so the
    /// repository rejects a second start for the same (subject, quota).
    /// </summary>
    /// <param name="subjectType">The kind of subject the usage is recorded for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="definition">The quota definition whose usage is tracked.</param>
    /// <param name="createdAt">When tracking starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public static QuotaUsage Start(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new QuotaUsage(
            Guid.CreateVersion7(),
            subjectType,
            subjectId,
            definition.Id,
            definition.EntitlementKey,
            usedAmount: 0,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Records the subject's current usage of this quota as an ABSOLUTE amount (the server-measured count). The
    /// amount must be non-negative; the limit is not consulted here — recording the usage and DECIDING whether it
    /// exceeds a limit are separated, so the status calculation stays the single place a limit is interpreted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    public void Record(long usedAmount, DateTimeOffset updatedAt)
    {
        if (usedAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usedAmount),
                usedAmount,
                "A used amount must not be negative.");
        }

        UsedAmount = usedAmount;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether this usage belongs to exactly the given subject. Matching is exact on the
    /// (<see cref="SubjectType"/>, <see cref="SubjectId"/>) pair; an empty id matches nothing.
    /// </summary>
    public bool IsForSubject(EntitlementSubjectType subjectType, Guid subjectId)
        => subjectId != Guid.Empty && SubjectType == subjectType && SubjectId == subjectId;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: usage id, subject, measured entitlement
    /// key and the recorded amount — identifiers, enums and a numeric count only, never any free-form content
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"QuotaUsage {Id} subject={SubjectType}:{SubjectId} key={EntitlementKey} used={UsedAmount}";
}
