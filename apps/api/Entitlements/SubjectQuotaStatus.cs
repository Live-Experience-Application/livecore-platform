namespace LiveCore.Api.Entitlements;

/// <summary>
/// A subject's complete, server-calculated quota status (CORE-ENTL-003) — the immutable set of
/// <see cref="QuotaStatus"/> entries a subject currently has, produced by <see cref="QuotaStatusCalculator"/> from
/// the active <see cref="QuotaDefinition"/>s for the subject kind, the subject's granted limits and its recorded
/// <see cref="QuotaUsage"/>. It is the single answer to "what is this subject's quota status?" and the fail-closed
/// surface that makes "Quota status is calculated server-side for subjects and workspaces" concrete.
///
/// The set is keyed by the (<see cref="SubjectType"/>, <see cref="SubjectId"/>) pair the statuses were computed
/// for; the subject id is retained for internal use and is NEVER projected to a client (the response carries only
/// the generic quota facts). A subject holds each quota at most once, so the entries have distinct keys and are
/// ordered by the generic key for a deterministic projection.
/// </summary>
public sealed class SubjectQuotaStatus
{
    private SubjectQuotaStatus(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        IReadOnlyList<QuotaStatus> quotas)
    {
        SubjectType = subjectType;
        SubjectId = subjectId;
        Quotas = quotas;
    }

    /// <summary>The kind of subject these statuses were computed for.</summary>
    public EntitlementSubjectType SubjectType { get; }

    /// <summary>The id of the subject these statuses were computed for. Internal only; never projected to a client.</summary>
    public Guid SubjectId { get; }

    /// <summary>The computed quota statuses, ordered by the generic quota key for a deterministic projection.</summary>
    public IReadOnlyList<QuotaStatus> Quotas { get; }

    /// <summary>
    /// Builds a subject's quota status from the computed entries. Entries are ordered by the generic key; a
    /// duplicate key is a data-invariant violation (a subject holds each quota at most once — the unique
    /// per-subject usage index) and is rejected rather than silently collapsed.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id is empty, or two entries share a quota key.</exception>
    public static SubjectQuotaStatus Create(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        IEnumerable<QuotaStatus> quotas)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        ArgumentNullException.ThrowIfNull(quotas);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<QuotaStatus>();
        foreach (var quota in quotas.OrderBy(status => status.Key, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(quota);
            if (!seen.Add(quota.Key))
            {
                throw new ArgumentException(
                    $"Duplicate quota status key '{quota.Key}' for the subject; a subject holds each quota at most once.",
                    nameof(quotas));
            }

            ordered.Add(quota);
        }

        return new SubjectQuotaStatus(subjectType, subjectId, ordered);
    }
}
