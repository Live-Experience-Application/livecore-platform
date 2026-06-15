using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Assets;

/// <summary>
/// The Assets module's download-access decision (CORE-AST-005) — "may this workspace role download this
/// asset?" — over the asset's <see cref="AssetLink"/>s and the central Visibility engine. It is the
/// server-side authorization the signed-download endpoint (CORE-AST-004) applies before minting a URL, now
/// extended so that linking an asset to a VISIBLE content block or entity grants the audience access (the
/// deferred audience case the CORE-AST-004 notes flagged for this story). It is a plain, fail-closed
/// decision service over <see cref="IAssetLinkRepository"/> and <see cref="VisibilityPolicy"/> taking
/// explicit inputs, exactly like <see cref="VisibilityPolicy"/> itself; the calling endpoint resolves the
/// tenant, loads the asset, discovers its workspace and the caller's role before invoking it.
///
/// THE DECISION, STRICTLY per docs/06_AUTHORIZATION_MATRIX.md and the epic acceptance criterion ("Assets
/// are private by default and accessed only through authorized signed URLs"):
/// <list type="bullet">
///   <item>HOST-CONTENT roles (Owner/Admin/Host/CoHost — "View host-only content" = <c>yes</c>,
///   <see cref="VisibilityRoles.ViewsHostOnlyContent"/>) may always download — they see host-only content
///   whether or not it is linked or visible. Short-circuits to ALLOW before any database read.</item>
///   <item>AUDIENCE roles (Participant/Observer — "View participant-visible content" = "if visible",
///   <see cref="VisibilityRoles.IsAudienceRole"/>) may download ONLY when the asset is linked to a content
///   block or entity that is VISIBLE to the audience IN THE GIVEN SESSION (a reveal is session-scoped, ADR
///   0013 / CORE-SVIS-004). The policy lists the asset's links
///   (<see cref="IAssetLinkRepository.ListByAssetAsync"/>, tenant- and workspace-scoped) and DELEGATES
///   each target to <see cref="VisibilityPolicy.CanViewResourceAsync(System.Guid, System.Guid, System.Guid, MembershipRole, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/> (the SAME central decision the
///   REST/realtime paths use, so an asset's audience access can never diverge from its target's visibility
///   — docs/05_MODULE_CONTRACTS.md: do not duplicate visibility logic). It ALLOWS as soon as ANY linked
///   target is visible to the audience in that session; otherwise DENIES.</item>
///   <item>EVERY OTHER role — the audit role Auditor (audit-only on both content rows, never a live
///   content grant) and any undefined enum value — is DENIED by default WITHOUT reading any link, so no
///   link or visibility existence can leak to a role with no content-view standing (threats T1/T5).</item>
/// </list>
///
/// Boundary order (docs/06 authorization principles): the asset has already been resolved within its
/// tenant and its own workspace by the endpoint; the link lookup and the delegated visibility decision are
/// both scoped by the asset's organization THEN workspace, so a link or rule in another tenant or
/// workspace can never grant access here (threat T5). The policy NEVER makes an asset public: a link only
/// changes WHO may pass this check, never how the bytes are served — the endpoint still mints a single
/// short-lived signed URL only after this allow (threat T4 "Asset leak").
///
/// ROLE-LEVEL vs PER-PARTICIPANT. Both decisions are now SESSION-SCOPED (CORE-SVIS-004 removed the
/// workspace-wide carve-out); they differ only in how the audience case is identified:
/// <list type="bullet">
///   <item><see cref="CanDownloadAsync"/> is the ROLE-level decision the endpoint applies to host-content
///   roles and the NON-participant audience role (Observer). Host-content roles may always download; the
///   audience role (Observer) may download iff an AUDIENCE-WIDE visible rule exists for a linked target IN
///   THE GIVEN SESSION, via the session-scoped <see cref="VisibilityPolicy.CanViewResourceAsync(System.Guid, System.Guid, System.Guid, MembershipRole, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>,
///   so an Observer cannot download an asset tied to a resource revealed only in a SIBLING session of the
///   same workspace (the cross-session leak CORE-SVIS-004 closes; threat T5/T3).</item>
///   <item><see cref="CanParticipantDownloadAsync"/> is the SESSION-scoped, per-PARTICIPANT decision the
///   endpoint applies to a <see cref="MembershipRole.Participant"/> caller (CORE-SVIS-003). It threads the
///   request's session and the caller's participant identity into the SAME per-participant primitive the
///   participant-visible feed uses (<see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>),
///   so a participant cannot obtain a download URL for an asset tied to a resource revealed only in a
///   SIBLING session of the same workspace, and a selected-participant reveal grants a download only to the
///   participant it names (the cross-session and selected-participant guarantees; threat T5/T3).</item>
/// </list>
/// </summary>
internal sealed class AssetDownloadPolicy
{
    private readonly IAssetLinkRepository _links;
    private readonly VisibilityPolicy _visibility;

    public AssetDownloadPolicy(IAssetLinkRepository links, VisibilityPolicy visibility)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(visibility);
        _links = links;
        _visibility = visibility;
    }

    /// <summary>
    /// Decides whether a viewer with the given workspace role may download the given asset IN THE GIVEN
    /// SESSION, applying the host-content vs audience-visible rules over the asset's links and the central
    /// Visibility engine. This is the ROLE-level decision; the endpoint applies it to host-content roles and
    /// the non-participant audience role (Observer). A <see cref="MembershipRole.Participant"/> caller is
    /// routed to the per-PARTICIPANT <see cref="CanParticipantDownloadAsync"/> instead (CORE-SVIS-003).
    /// </summary>
    /// <param name="asset">The already-resolved, tenant- and workspace-scoped asset.</param>
    /// <param name="viewerRole">The caller's role in the asset's own workspace.</param>
    /// <param name="sessionId">
    /// The session the audience decision is bounded by (a reveal is session-scoped, ADR 0013 /
    /// CORE-SVIS-004). It is consulted only for an audience role; a host-content role short-circuits to
    /// ALLOW before any rule lookup and so never depends on it, and an audit/undefined role is denied
    /// before any lookup. For an audience role a missing/empty session matches no rule, so the download is
    /// DENIED fail-closed (the caller — the endpoint — supplies a valid session for an audience caller).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the viewer may download; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    public async Task<bool> CanDownloadAsync(
        Asset asset,
        MembershipRole viewerRole,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // HOST-CONTENT roles may always download — short-circuit to ALLOW before any database read. Host
        // content access is session-agnostic, so no session is consulted.
        if (VisibilityRoles.ViewsHostOnlyContent(viewerRole))
        {
            return true;
        }

        // Roles that are neither host-content nor audience (the audit role, any undefined value) are
        // DENIED by default WITHOUT reading any link, so no link or visibility existence can leak to a
        // role with no content-view standing (threats T1/T5; docs/06 Auditor = audit-only).
        if (!VisibilityRoles.IsAudienceRole(viewerRole))
        {
            return false;
        }

        // AUDIENCE role: a reveal is session-scoped (ADR 0013 / CORE-SVIS-004), so "is the linked resource
        // visible to the audience?" is only meaningful within a session. An empty session can never address
        // a real session, so it grants nothing — fail closed (the session-scoped policy would otherwise
        // reject an empty id; the endpoint supplies a valid session for an audience caller).
        if (sessionId == Guid.Empty)
        {
            return false;
        }

        // ALLOW iff the asset is linked to a content block or entity the audience may see IN THIS SESSION.
        // The links are tenant- and workspace-scoped to the asset's own boundary, and each target's
        // visibility is the central SESSION-SCOPED Visibility decision (reused, never duplicated), so a
        // resource revealed only in a sibling session of the same workspace grants no download here. The
        // asset becomes audience-accessible the moment ANY linked target is audience-visible in this session.
        var links = await _links
            .ListByAssetAsync(asset.OrganizationId, asset.WorkspaceId, asset.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var link in links)
        {
            var decision = await _visibility
                .CanViewResourceAsync(
                    asset.OrganizationId,
                    asset.WorkspaceId,
                    sessionId,
                    viewerRole,
                    link.TargetType.ToVisibilityResourceType(),
                    link.TargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (decision.CanView)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides whether the given participant may download the given asset IN THE GIVEN SESSION
    /// (CORE-SVIS-003) — the SESSION-scoped per-participant download the signed-download endpoint applies
    /// for a <see cref="MembershipRole.Participant"/> caller. The asset is downloadable iff it is linked to
    /// a content block or entity that is VISIBLE to THIS participant IN THIS session: an audience-wide
    /// reveal of this session OR a reveal scoped to exactly this participant in this session. A resource
    /// revealed only in a SIBLING session of the same workspace yields no visible rule here, so it never
    /// grants a download (the cross-session leak this story closes; threat T5/T3 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    ///
    /// It reuses the SAME central per-participant primitive the participant-visible feed and the realtime
    /// recipient gate use (<see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>),
    /// so an asset's per-participant download access can never diverge from its target's session-scoped
    /// visibility (docs/05_MODULE_CONTRACTS.md: do not duplicate visibility logic; ADR 0013). The link
    /// lookup and the delegated decision are both scoped to the asset's own organization THEN workspace THEN
    /// the supplied session, so a link or rule in another tenant, workspace or session can never grant access
    /// here. The policy never makes an asset public: a link only changes WHO may pass this check, never how
    /// the bytes are served — the endpoint still mints a single short-lived signed URL only after this allow
    /// (threat T4 "Asset leak"). The endpoint has resolved the participant from a verified, active
    /// participant record in the asset's workspace and the session from the request.
    /// </summary>
    /// <param name="asset">The already-resolved, tenant- and workspace-scoped asset.</param>
    /// <param name="sessionId">The session the download is scoped to (checked after the tenant/workspace).</param>
    /// <param name="participantId">The downloading participant, resolved in the asset's workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the participant may download the asset in this session; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    public async Task<bool> CanParticipantDownloadAsync(
        Asset asset,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // ALLOW iff the asset is linked to a content block/entity this participant may see IN THIS session.
        // The links are tenant- and workspace-scoped to the asset's own boundary, and each target's
        // visibility is the central SESSION-SCOPED per-participant decision (reused, never duplicated): an
        // audience-wide visible rule of this session, or a visible rule scoped to exactly this participant in
        // this session. A target visible only in another session — or only to another participant — does not
        // grant access. The asset becomes downloadable the moment ANY linked target is visible to this
        // participant here.
        var links = await _links
            .ListByAssetAsync(asset.OrganizationId, asset.WorkspaceId, asset.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var link in links)
        {
            var canView = await _visibility
                .CanParticipantViewResourceAsync(
                    asset.OrganizationId,
                    asset.WorkspaceId,
                    sessionId,
                    participantId,
                    link.TargetType.ToVisibilityResourceType(),
                    link.TargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (canView)
            {
                return true;
            }
        }

        return false;
    }
}
