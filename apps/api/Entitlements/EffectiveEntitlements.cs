namespace LiveCore.Api.Entitlements;

/// <summary>
/// A subject's resolved, server-authoritative premium state (CORE-ENTL-002) — the immutable set of
/// <see cref="EffectiveEntitlement"/>s a subject currently holds, produced by
/// <see cref="SubjectEntitlementResolver"/> from the subject's ACTIVE assignments. It is the single answer to
/// "what is this subject entitled to?", and the fail-closed query surface that makes
/// "User-visible premium state comes only from server entitlements" concrete.
///
/// FAIL-CLOSED BY CONSTRUCTION. The set contains exactly the entitlements the server granted and that are still
/// in force; anything not present is simply NOT entitled. So <see cref="IsFlagEnabled"/> defaults to
/// <see langword="false"/> for an absent (or revoked, hence absent) flag, and <see cref="TryGetQuotaLimit"/>
/// reports "no entitlement" for an absent quota — a client can never obtain premium state that the server did
/// not grant (docs/21 "Never trust client-side premium flags"). Lookups are keyed by the generic, canonical
/// entitlement key (exact, ordinal).
/// </summary>
public sealed class EffectiveEntitlements
{
    private readonly IReadOnlyDictionary<string, EffectiveEntitlement> _byKey;

    private EffectiveEntitlements(IReadOnlyDictionary<string, EffectiveEntitlement> byKey)
        => _byKey = byKey;

    /// <summary>An empty premium state — a subject with no active entitlements is entitled to nothing.</summary>
    public static EffectiveEntitlements Empty { get; } =
        new(new Dictionary<string, EffectiveEntitlement>(StringComparer.Ordinal));

    /// <summary>
    /// Builds the resolved set from a subject's active assignments. Keys are exact/ordinal (the stored keys are
    /// canonical). A subject holds each entitlement at most once (the unique per-subject index), so a duplicate
    /// key in the input is a data invariant violation and is rejected rather than silently collapsed.
    /// </summary>
    /// <exception cref="ArgumentException">Two assignments share an entitlement key (a per-subject invariant violation).</exception>
    public static EffectiveEntitlements FromActiveAssignments(IEnumerable<SubjectEntitlement> activeAssignments)
    {
        ArgumentNullException.ThrowIfNull(activeAssignments);

        var byKey = new Dictionary<string, EffectiveEntitlement>(StringComparer.Ordinal);
        foreach (var assignment in activeAssignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            var effective = EffectiveEntitlement.From(assignment);
            if (!byKey.TryAdd(effective.Key, effective))
            {
                throw new ArgumentException(
                    $"Duplicate effective entitlement key '{effective.Key}' for the subject; a subject holds each entitlement at most once.",
                    nameof(activeAssignments));
            }
        }

        return new EffectiveEntitlements(byKey);
    }

    /// <summary>The resolved entitlements, ordered by the generic key for a deterministic projection.</summary>
    public IReadOnlyList<EffectiveEntitlement> All
        => _byKey.Values.OrderBy(entitlement => entitlement.Key, StringComparer.Ordinal).ToList();

    /// <summary>The number of resolved entitlements.</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// Finds the resolved entitlement for exactly the given canonical key, or <see langword="null"/> when the
    /// subject does not hold it. The key is canonicalized (trimmed and lower-cased) first, so a differently-cased
    /// input resolves consistently; a malformed key can never address a stored entitlement, so it resolves to
    /// "not held" rather than throwing — a premium-state lookup is a query, and the fail-closed answer is simply
    /// "no".
    /// </summary>
    public EffectiveEntitlement? Find(string key)
    {
        var canonical = key?.Trim().ToLowerInvariant();
        if (canonical is null || !EntitlementDefinition.IsValidKey(canonical))
        {
            return null;
        }

        return _byKey.GetValueOrDefault(canonical);
    }

    /// <summary>
    /// Whether the subject holds the given entitlement (active), regardless of its value or kind. A revoked or
    /// never-granted entitlement is not held (fail-closed).
    /// </summary>
    public bool Has(string key) => Find(key) is not null;

    /// <summary>
    /// The server-authoritative answer to "is this boolean capability enabled for the subject?". Returns
    /// <see langword="true"/> ONLY when the subject holds an active FLAG entitlement for the key granted
    /// <see langword="true"/>; an absent entitlement, a revoked one, a flag granted <see langword="false"/>, or
    /// a quota entitlement all return <see langword="false"/> (fail-closed default). This is the only premium
    /// signal a feature guard should consult for a flag.
    /// </summary>
    public bool IsFlagEnabled(string key) => Find(key) is { IsEnabledFlag: true };

    /// <summary>
    /// Tries to read the granted quota limit for the given entitlement. Returns <see langword="true"/> when the
    /// subject holds an active QUOTA entitlement for the key — <paramref name="limit"/> is then the numeric cap,
    /// or <see langword="null"/> meaning an UNLIMITED (fair-use) grant. Returns <see langword="false"/> when the
    /// subject does not hold a quota entitlement for the key (absent, revoked, or a flag entitlement), with
    /// <paramref name="limit"/> set to <see langword="null"/>. The caller must distinguish the
    /// <see langword="false"/> "no entitlement" result from the <see langword="true"/>/null "unlimited" result;
    /// the numeric quota usage and status calculation are the later quota story (CORE-ENTL-003).
    /// </summary>
    public bool TryGetQuotaLimit(string key, out long? limit)
    {
        if (Find(key) is { IsQuota: true } quota)
        {
            limit = quota.QuotaLimit;
            return true;
        }

        limit = null;
        return false;
    }
}
