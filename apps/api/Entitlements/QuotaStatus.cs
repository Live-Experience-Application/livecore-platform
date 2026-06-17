// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// The server-calculated STATUS of one quota for one subject (CORE-ENTL-003) — the safe, product-neutral answer
/// to "how much of this quota has the subject used, and how much is left?". It is produced by combining a
/// <see cref="QuotaDefinition"/>, the subject's granted limit (its <see cref="EffectiveEntitlement"/> quota value)
/// and the subject's recorded <see cref="QuotaUsage"/>, and is what the quota-status endpoints hand a client
/// (csv/mobile_store_api_routes.csv: <c>GET /v1/me/quota-status</c> "Used by paywalls and feature guards").
///
/// THE EPIC ACCEPTANCE CRITERION — "Quota status is calculated server-side for subjects and workspaces". The
/// status is computed entirely server-side from the recorded limit and usage; a client never supplies any part of
/// it. The calculation is FAIL-CLOSED: a subject that is not entitled to a quota has NO allowance — the limit is
/// treated as zero, so any usage already exceeds it — and a client can never obtain headroom the server did not
/// grant (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Never unlock limits before server verification
/// succeeds"; "Never trust client-side premium flags").
///
/// This view carries only the generic quota <see cref="Key"/>, its <see cref="Unit"/> and the computed numbers; a
/// vertical maps the key to its own paywall copy in its UI (docs/21). It carries NO subject id, NO internal
/// surrogate ids and NO authorization rationale — only the generic, client-safe quota fact.
/// </summary>
/// <param name="Key">The generic, lower-case dotted quota entitlement key (e.g. <c>workspace.active.max</c>).</param>
/// <param name="Unit">The unit the usage and limit are expressed in (a count or bytes).</param>
/// <param name="Used">The subject's recorded usage (non-negative), in <paramref name="Unit"/>.</param>
/// <param name="Limit">The granted numeric cap, or <see langword="null"/> for an unlimited (fair-use) grant. Zero when the subject is not entitled.</param>
/// <param name="IsUnlimited">Whether the subject holds an UNLIMITED (fair-use) quota (no numeric cap).</param>
/// <param name="IsEntitled">Whether the subject holds an active quota entitlement for the key at all (fail-closed false otherwise).</param>
/// <param name="Remaining">The headroom left (<see cref="long"/> not below zero), or <see langword="null"/> for an unlimited grant.</param>
/// <param name="IsExceeded">Whether the recorded usage is over the limit (always false for an unlimited grant).</param>
public sealed record QuotaStatus(
    string Key,
    QuotaUnit Unit,
    long Used,
    long? Limit,
    bool IsUnlimited,
    bool IsEntitled,
    long? Remaining,
    bool IsExceeded)
{
    /// <summary>
    /// Calculates the quota status for a subject from its measured key/unit, whether it is entitled, the granted
    /// limit and the recorded usage. This is the single, pure place the quota math lives, so the boundary
    /// semantics can never diverge across callers:
    /// <list type="bullet">
    ///   <item><b>Not entitled</b> (<paramref name="isEntitled"/> is <see langword="false"/>): the subject has no
    ///   allowance — the limit is zero, the remaining is zero, and any positive usage is already exceeded
    ///   (fail-closed; docs/21). The <paramref name="limit"/> argument is ignored.</item>
    ///   <item><b>Entitled, no cap</b> (<paramref name="limit"/> is <see langword="null"/>): an UNLIMITED
    ///   (fair-use) grant — never exceeded, with no finite remaining.</item>
    ///   <item><b>Entitled, capped at L</b>: the remaining is <c>max(0, L - used)</c> and the quota is exceeded
    ///   only when <c>used &gt; L</c>. Usage exactly AT the limit is the boundary: it is NOT exceeded (the cap is
    ///   the maximum allowed amount) and leaves zero remaining.</item>
    /// </list>
    /// </summary>
    /// <param name="key">The generic, canonical quota entitlement key (validated).</param>
    /// <param name="unit">The quota's unit.</param>
    /// <param name="isEntitled">Whether the subject holds an active quota entitlement for the key.</param>
    /// <param name="limit">The granted cap (non-negative), or <see langword="null"/> for an unlimited grant; ignored when not entitled.</param>
    /// <param name="used">The recorded usage (non-negative).</param>
    /// <exception cref="ArgumentException">The key violates the entitlement key invariants.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The unit is undefined, the usage is negative, or the limit is negative.</exception>
    public static QuotaStatus Calculate(string key, QuotaUnit unit, bool isEntitled, long? limit, long used)
    {
        if (!EntitlementDefinition.IsValidKey(key))
        {
            throw new ArgumentException("Entitlement key violates the entitlement key invariants.", nameof(key));
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unit is not a defined quota unit.");
        }

        if (used < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(used), used, "A used amount must not be negative.");
        }

        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "A quota limit must not be negative; pass null for an unlimited (fair-use) grant.");
        }

        if (!isEntitled)
        {
            // Fail-closed: a subject with no quota entitlement has no allowance. The limit is zero, so any usage
            // already exceeds it (docs/21 "Never unlock limits before server verification succeeds").
            return new QuotaStatus(
                key,
                unit,
                used,
                Limit: 0,
                IsUnlimited: false,
                IsEntitled: false,
                Remaining: 0,
                IsExceeded: used > 0);
        }

        if (limit is null)
        {
            // Entitled with no numeric cap: an unlimited (fair-use) grant is never exceeded and has no finite
            // remaining.
            return new QuotaStatus(
                key,
                unit,
                used,
                Limit: null,
                IsUnlimited: true,
                IsEntitled: true,
                Remaining: null,
                IsExceeded: false);
        }

        var cap = limit.Value;
        var remaining = Math.Max(0, cap - used);
        return new QuotaStatus(
            key,
            unit,
            used,
            Limit: cap,
            IsUnlimited: false,
            IsEntitled: true,
            Remaining: remaining,
            // Usage AT the cap is the boundary and is allowed; only usage strictly over the cap is exceeded.
            IsExceeded: used > cap);
    }
}
