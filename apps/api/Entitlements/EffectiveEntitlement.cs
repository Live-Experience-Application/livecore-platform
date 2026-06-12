namespace LiveCore.Api.Entitlements;

/// <summary>
/// One resolved, subject-facing entitlement (CORE-ENTL-002) — the safe, product-neutral view of a single piece
/// of a subject's server-authoritative premium state. It is produced by <see cref="SubjectEntitlementResolver"/>
/// from an ACTIVE <see cref="SubjectEntitlement"/> and is what a later <c>GET /v1/me/entitlements</c> endpoint
/// hands a client (csv/mobile_store_api_routes.csv: "Return effective entitlements for current user", "Generic
/// no vertical language").
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server entitlements". This view
/// carries only what the server recorded: the generic entitlement <see cref="Key"/>, its
/// <see cref="ValueKind"/> and the granted value (<see cref="FlagValue"/> for a flag, <see cref="QuotaLimit"/>
/// for a quota — null meaning an unlimited/fair-use grant). A vertical maps the key to its own paywall copy in
/// its UI (docs/21 "ArcanOS may display these as ..."); Core hands out only the generic key and value. It
/// carries NO subject id, NO internal surrogate ids, NO source-plan provenance and NO authorization rationale —
/// only the generic, client-safe premium fact.
/// </summary>
/// <param name="Key">The generic, lower-case dotted entitlement key (e.g. <c>ads.disabled</c>, <c>workspace.active.max</c>).</param>
/// <param name="ValueKind">Whether the entitlement is a boolean flag or a numeric quota.</param>
/// <param name="FlagValue">The granted boolean value for a flag entitlement; <see langword="null"/> for a quota.</param>
/// <param name="QuotaLimit">The granted numeric cap for a quota entitlement — <see langword="null"/> meaning unlimited (fair-use); <see langword="null"/> for a flag.</param>
public sealed record EffectiveEntitlement(
    string Key,
    EntitlementValueKind ValueKind,
    bool? FlagValue,
    long? QuotaLimit)
{
    /// <summary>Whether this resolved entitlement is a boolean flag.</summary>
    public bool IsFlag => ValueKind == EntitlementValueKind.Flag;

    /// <summary>Whether this resolved entitlement is a numeric quota.</summary>
    public bool IsQuota => ValueKind == EntitlementValueKind.Quota;

    /// <summary>
    /// Whether this is an enabled flag — a flag entitlement granted <see langword="true"/>. A quota entitlement,
    /// or a flag granted <see langword="false"/>, is not "enabled". This is the server-authoritative answer to
    /// "does the subject have this capability?".
    /// </summary>
    public bool IsEnabledFlag => IsFlag && FlagValue == true;

    /// <summary>
    /// Whether this is an UNLIMITED (fair-use) quota — a quota entitlement with no numeric cap. Always false for
    /// a flag.
    /// </summary>
    public bool IsUnlimitedQuota => IsQuota && QuotaLimit is null;

    /// <summary>
    /// Projects an active <see cref="SubjectEntitlement"/> into its subject-facing effective view, copying the
    /// generic key and granted value only. The caller resolves only ACTIVE assignments, so a revoked assignment
    /// never reaches this projection.
    /// </summary>
    public static EffectiveEntitlement From(SubjectEntitlement assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        return new EffectiveEntitlement(
            assignment.EntitlementKey,
            assignment.ValueKind,
            assignment.FlagValue,
            assignment.QuotaLimit);
    }
}
