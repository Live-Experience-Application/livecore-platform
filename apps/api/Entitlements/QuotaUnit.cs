// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// The generic UNIT a <see cref="QuotaDefinition"/> measures usage in (CORE-ENTL-003, the quota definition and
/// quota status story of the "Entitlements and Quotas" epic). A numeric <see cref="EntitlementValueKind.Quota"/>
/// entitlement caps "how many of something" a subject may have; this enum says WHAT that something is counted in,
/// so the server-side <see cref="QuotaStatus"/> calculation compares a recorded usage against a granted limit in
/// a consistent unit (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Limits such as active workspace count,
/// active session count, participant count, storage ... must be enforced server-side").
///
/// GENERIC, NOT VERTICAL. The units are product-neutral Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv); a vertical maps a quota to its own paywall copy in its UI (docs/21 "a vertical may
/// display these as ..."), while Core stores only the generic definition and computes the generic status. The
/// unit is purely descriptive of the count's meaning and never an authorization input.
///
/// The unit is persisted by its stable NAME (not its numeric value), so the integers below are only in-memory
/// storage discriminators (persisted by name, like <see cref="EntitlementValueKind"/>,
/// <see cref="EntitlementSubjectType"/> and every other enum in the model), carry no ordering meaning and must
/// not be compared with &gt;/&lt;.
/// </summary>
public enum QuotaUnit
{
    /// <summary>
    /// A plain COUNT of items — the quota caps how many of a discrete thing a subject may have (for example a
    /// generic active-workspace, active-session or participant count; docs/21 generic keys such as
    /// <c>workspace.active.max</c>, <c>session.active.max</c>, <c>session.participant.max</c>). Usage and the
    /// limit are whole, non-negative counts.
    /// </summary>
    Count = 1,

    /// <summary>
    /// A number of BYTES — the quota caps a storage size (for example a generic asset-storage limit; docs/21
    /// generic key <c>asset.storage.bytes.max</c>). Usage and the limit are non-negative byte totals.
    /// </summary>
    Bytes = 2,
}
