namespace LiveCore.Api.Exports;

/// <summary>
/// The explicit, generic SCOPE of an <see cref="ExportJob"/> — what data set the export covers
/// (CORE-AUD-002, the first story of the "Audit, Export and Recap" epic). The Exports module owns
/// "export jobs", "user data export" and "workspace export" (docs/05_MODULE_CONTRACTS.md), and this enum
/// is the Core-level catalog of those two generic, product-neutral export scopes. The names carry no
/// vertical product language — a generic workspace or a user's own data only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
///
/// An export scope is a SECURITY boundary, not a cosmetic label. docs/07_SECURITY_THREAT_MODEL.md lists
/// "explicit host/admin export scopes" among the controls for threat T8 ("Export leak — participant
/// export includes hidden content"): an export job carries the explicit scope it was authorized for, so a
/// later export-request command and its role-based projection (the manifest story CORE-AUD-003) can never
/// silently widen a user-data export into a workspace-wide one. The scope bounds what the export may ever
/// include; it is decided at creation by the requester's authorization and is then immutable for the job.
///
/// The scope is persisted by its stable NAME (not its numeric value), so the integers below are only
/// in-memory storage discriminators (persisted by name, like every other enum in the model —
/// <c>AssetStatus</c>, <c>VisibilityState</c>, <c>SessionStatus</c>), carry no ordering meaning and must
/// not be compared with &gt;/&lt;.
/// </summary>
public enum ExportScope
{
    /// <summary>
    /// A full WORKSPACE export — the workspace-wide data set (docs/05_MODULE_CONTRACTS.md: the Exports
    /// module owns "workspace export"). This is the host/admin-privileged scope of the authorization
    /// matrix's "Export workspace" row (docs/06_AUTHORIZATION_MATRIX.md: Owner/Admin yes, Host optional),
    /// so it is the broader of the two scopes and the one the later export-request policy guards most
    /// tightly (threat T8 "explicit host/admin export scopes").
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// A USER-DATA export — a single user's own data within the workspace (docs/05_MODULE_CONTRACTS.md:
    /// the Exports module owns "user data export"). This is the narrower, self-service scope: it covers
    /// only the requesting user's own data, never the workspace-wide host content, so it never widens a
    /// participant's export into hidden content (threat T8).
    /// </summary>
    UserData = 2,
}
