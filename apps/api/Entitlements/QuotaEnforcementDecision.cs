// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// The server-side ENFORCEMENT decision for one protected command (CORE-ENTL-004) — the fail-closed answer to
/// "may this command consume the requested amount of this subject's quota?". It is produced by
/// <see cref="QuotaEnforcementService.CheckAsync"/> from the same inputs the quota-status read uses (the active
/// <see cref="QuotaDefinition"/>, the subject's granted limit and its recorded <see cref="QuotaUsage"/>), so a
/// command's allow/deny can never diverge from what <c>GET /api/v1/.../quota-status</c> reports.
///
/// THE EPIC ACCEPTANCE CRITERION — "Free limits cannot be bypassed by clients". The decision is computed entirely
/// server-side; a client supplies no part of it. It is FAIL-CLOSED: a subject that is not entitled to a defined
/// quota has no allowance, so any consumption is <see cref="IsAllowed"/> = <see langword="false"/>
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Never unlock limits before server verification succeeds").
///
/// The decision carries only the generic, client-safe quota facts (the generic key and the granted limit) so the
/// endpoint can phrase a denial without leaking internal state (threat T7). It carries NO subject id and NO
/// authorization rationale.
/// </summary>
public sealed record QuotaEnforcementDecision
{
    private QuotaEnforcementDecision(bool isAllowed, bool isGoverned, string entitlementKey, long? limit, long used)
    {
        IsAllowed = isAllowed;
        IsGoverned = isGoverned;
        EntitlementKey = entitlementKey;
        Limit = limit;
        Used = used;
    }

    /// <summary>
    /// Whether the command MAY proceed. A command may proceed when the subject has enough headroom, when it holds an
    /// unlimited (fair-use) grant, or when no active quota governs the command in this deployment
    /// (<see cref="IsGoverned"/> = <see langword="false"/>). It may NOT proceed when consuming the requested amount
    /// would exceed the granted limit, or when the subject is not entitled to a defined quota (fail-closed).
    /// </summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Whether an active <see cref="QuotaDefinition"/> governs this command for the subject kind. When
    /// <see langword="false"/> there is nothing to enforce in this deployment (Core enforces only quotas that
    /// exist), the command is allowed, and no usage is recorded.
    /// </summary>
    public bool IsGoverned { get; }

    /// <summary>The generic, lower-case dotted quota entitlement key the decision is about (e.g. <c>workspace.active.max</c>).</summary>
    public string EntitlementKey { get; }

    /// <summary>
    /// The subject's granted numeric cap, or <see langword="null"/> for an unlimited (fair-use) grant or an
    /// ungoverned command. Zero when the subject is not entitled to a defined quota (fail-closed).
    /// </summary>
    public long? Limit { get; }

    /// <summary>The subject's recorded usage at the moment the decision was computed (zero for an ungoverned command).</summary>
    public long Used { get; }

    /// <summary>
    /// The command is allowed because no active quota governs it for the subject kind. The command proceeds and
    /// nothing is recorded.
    /// </summary>
    public static QuotaEnforcementDecision NotGoverned(string entitlementKey)
    {
        ArgumentNullException.ThrowIfNull(entitlementKey);
        return new QuotaEnforcementDecision(isAllowed: true, isGoverned: false, entitlementKey, limit: null, used: 0);
    }

    /// <summary>The command is allowed: the subject has enough headroom (or an unlimited grant) for the requested amount.</summary>
    public static QuotaEnforcementDecision Allowed(QuotaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new QuotaEnforcementDecision(isAllowed: true, isGoverned: true, status.Key, status.Limit, status.Used);
    }

    /// <summary>The command is denied: consuming the requested amount would exceed the subject's granted limit (or it has none).</summary>
    public static QuotaEnforcementDecision Denied(QuotaStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new QuotaEnforcementDecision(isAllowed: false, isGoverned: true, status.Key, status.Limit, status.Used);
    }
}
