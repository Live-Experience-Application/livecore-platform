namespace LiveCore.Api.Entitlements;

/// <summary>
/// The generic VALUE KIND of an <see cref="EntitlementDefinition"/> — whether the entitlement is a boolean
/// capability or a numeric limit (CORE-ENTL-001, the first story of the "Entitlements and Quotas" epic). It is
/// the Core-level, product-neutral classification documented in the entitlement catalog's <c>type</c> column
/// (csv/mobile_entitlement_catalog.csv distinguishes <c>flag</c> from <c>quota</c>;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The names carry no vertical product language — a
/// generic flag or quota only (AGENTS.md, csv/forbidden_core_terms.csv); a vertical maps an entitlement to its
/// own paywall vocabulary in its UI (docs/21 "Generic entitlement keys"), while Core stores only the generic
/// definition.
///
/// The kind is a CONTRACT, not a cosmetic label: it fixes the shape of the value a plan may grant for the
/// entitlement (a <see cref="Flag"/> grants a boolean, a <see cref="Quota"/> grants a numeric limit), so a
/// plan can never bind a numeric limit to a flag or a boolean to a quota — the value is enforced
/// server-side, never trusted from a client (docs/21 "Never trust client-side premium flags";
/// docs/07_SECURITY_THREAT_MODEL.md). The numeric quota's per-subject usage and the quota-status calculation
/// are a later story (the quota definition, CORE-ENTL-003); this story only classifies the definition.
///
/// The kind is persisted by its stable NAME (not its numeric value), so the integers below are only in-memory
/// storage discriminators (persisted by name, like <c>ExportScope</c>, <c>AssetStatus</c> and every other enum
/// in the model), carry no ordering meaning and must not be compared with &gt;/&lt;.
/// </summary>
public enum EntitlementValueKind
{
    /// <summary>
    /// A boolean CAPABILITY flag — the entitlement is either granted or not (for example a generic ad-free,
    /// export-enabled or native-access capability; docs/21 generic keys such as <c>ads.disabled</c>,
    /// <c>export.enabled</c>, <c>mobile.native.access</c>). A plan grants it a <see langword="true"/>/
    /// <see langword="false"/> value.
    /// </summary>
    Flag = 1,

    /// <summary>
    /// A numeric LIMIT — the entitlement caps how many of something a subject may have (for example a generic
    /// active-workspace, active-session or participant limit; docs/21 generic keys such as
    /// <c>workspace.active.max</c>, <c>session.participant.max</c>). A plan grants it a non-negative numeric
    /// limit, or an UNLIMITED (fair-use) grant with no numeric cap. The per-subject usage tracking and the
    /// server-side quota-status calculation are the later quota story (CORE-ENTL-003).
    /// </summary>
    Quota = 2,
}
