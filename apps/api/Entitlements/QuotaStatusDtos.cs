// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Client-safe response envelope of the quota-status endpoints (CORE-ENTL-003;
/// csv/mobile_store_api_routes.csv: <c>GET /v1/me/quota-status</c>,
/// <c>GET /v1/workspaces/{workspaceId}/quota-status</c>). It carries only the generic, server-calculated quota
/// facts — never a subject id, an internal surrogate id, a tenant/workspace id or any authorization rationale
/// (docs/08_API_CONTRACTS.md DTO rules; threat T7). A vertical maps each generic quota key to its own paywall copy
/// in its UI (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
/// </summary>
/// <param name="Quotas">The subject's server-calculated quota statuses, ordered by the generic quota key.</param>
public sealed record QuotaStatusResponse(IReadOnlyList<QuotaStatusItem> Quotas)
{
    /// <summary>Projects a subject's calculated quota status into the client-safe response envelope.</summary>
    public static QuotaStatusResponse From(SubjectQuotaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new QuotaStatusResponse(status.Quotas.Select(QuotaStatusItem.From).ToArray());
    }
}

/// <summary>
/// One quota's server-calculated status in the response (CORE-ENTL-003) — the client-safe projection of a
/// <see cref="QuotaStatus"/>. It states the generic quota key, the unit, the recorded usage and limit, and the
/// computed headroom; a paywall or feature guard consumes it directly (docs/21: the user-visible premium state
/// comes only from the server).
/// </summary>
/// <param name="Key">The generic, lower-case dotted quota entitlement key (e.g. <c>workspace.active.max</c>).</param>
/// <param name="Unit">The unit the usage and limit are expressed in, by stable name (<c>Count</c> or <c>Bytes</c>).</param>
/// <param name="Used">The subject's recorded usage (non-negative), in <paramref name="Unit"/>.</param>
/// <param name="Limit">The granted numeric cap, or <see langword="null"/> for an unlimited (fair-use) grant.</param>
/// <param name="Unlimited">Whether the subject holds an unlimited (fair-use) quota (no numeric cap).</param>
/// <param name="Entitled">Whether the subject holds an active quota entitlement for the key at all.</param>
/// <param name="Remaining">The headroom left (never below zero), or <see langword="null"/> for an unlimited grant.</param>
/// <param name="Exceeded">Whether the recorded usage is over the limit (always false for an unlimited grant).</param>
public sealed record QuotaStatusItem(
    string Key,
    string Unit,
    long Used,
    long? Limit,
    bool Unlimited,
    bool Entitled,
    long? Remaining,
    bool Exceeded)
{
    /// <summary>Projects a calculated <see cref="QuotaStatus"/> into the client-safe item (the unit as its stable name).</summary>
    public static QuotaStatusItem From(QuotaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new QuotaStatusItem(
            status.Key,
            status.Unit.ToString(),
            status.Used,
            status.Limit,
            status.IsUnlimited,
            status.IsEntitled,
            status.Remaining,
            status.IsExceeded);
    }
}
