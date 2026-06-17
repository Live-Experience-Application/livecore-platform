// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Client-safe response envelope of the current-user effective-entitlements endpoint
/// (CORE-API-007; csv/mobile_store_api_routes.csv: <c>GET /v1/me/entitlements</c>,
/// "Return effective entitlements for current user", "Generic no vertical language"). It is
/// the read half of the entitlements story (CORE-ENTL-002) made reachable over HTTP: the
/// subject's server-authoritative <see cref="EffectiveEntitlements"/> projected into a flat,
/// product-neutral wire shape.
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server
/// entitlements" (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The response carries
/// only what the server recorded and resolved (the generic entitlement key + value), so a
/// client can never obtain premium state the server did not grant; a vertical maps each
/// generic key to its own paywall copy in its UI (docs/21 "Generic entitlement keys"). It
/// carries NO subject id, NO internal surrogate id, NO source-plan provenance and NO
/// authorization rationale — only the generic, client-safe premium facts
/// (docs/08_API_CONTRACTS.md DTO rules; threat T7).
/// </summary>
/// <param name="Entitlements">
/// The subject's resolved effective entitlements, ordered by the generic entitlement key for
/// a deterministic projection (<see cref="EffectiveEntitlements.All"/>). Empty when the
/// subject holds no active entitlements (the fail-closed default — an unentitled subject is
/// entitled to nothing).
/// </param>
public sealed record MeEntitlementsResponse(IReadOnlyList<EntitlementItem> Entitlements)
{
    /// <summary>
    /// Projects a subject's resolved <see cref="EffectiveEntitlements"/> into the client-safe
    /// response envelope, preserving the deterministic key order.
    /// </summary>
    public static MeEntitlementsResponse From(EffectiveEntitlements entitlements)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        return new MeEntitlementsResponse(entitlements.All.Select(EntitlementItem.From).ToArray());
    }
}

/// <summary>
/// One resolved entitlement in the response (CORE-API-007) — the client-safe projection of a
/// single <see cref="EffectiveEntitlement"/>. It states the generic entitlement key, its value
/// kind (by stable name) and the granted value: a boolean for a flag, a numeric cap for a
/// quota (null meaning an unlimited/fair-use grant). A paywall or feature guard consumes it
/// directly (docs/21: the user-visible premium state comes only from the server).
/// </summary>
/// <param name="Key">The generic, lower-case dotted entitlement key (e.g. <c>ads.disabled</c>, <c>workspace.active.max</c>).</param>
/// <param name="ValueKind">Whether the entitlement is a boolean flag or a numeric quota, by stable name (<c>Flag</c> or <c>Quota</c>).</param>
/// <param name="FlagValue">The granted boolean value for a flag entitlement; <see langword="null"/> for a quota.</param>
/// <param name="QuotaLimit">The granted numeric cap for a quota entitlement — <see langword="null"/> meaning unlimited (fair-use); <see langword="null"/> for a flag.</param>
public sealed record EntitlementItem(
    string Key,
    string ValueKind,
    bool? FlagValue,
    long? QuotaLimit)
{
    /// <summary>
    /// Projects a resolved <see cref="EffectiveEntitlement"/> into the client-safe item (the
    /// value kind as its stable name, mirroring how <see cref="QuotaStatusItem"/> projects its
    /// unit). Only the generic key and granted value are copied — never a subject id, a source
    /// plan or any authorization rationale (threat T7).
    /// </summary>
    public static EntitlementItem From(EffectiveEntitlement entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        return new EntitlementItem(
            entitlement.Key,
            entitlement.ValueKind.ToString(),
            entitlement.FlagValue,
            entitlement.QuotaLimit);
    }
}
