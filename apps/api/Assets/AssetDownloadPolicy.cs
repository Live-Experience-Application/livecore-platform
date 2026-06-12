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
///   block or entity that is VISIBLE to the audience. The policy lists the asset's links
///   (<see cref="IAssetLinkRepository.ListByAssetAsync"/>, tenant- and workspace-scoped) and DELEGATES
///   each target to <see cref="VisibilityPolicy.CanViewResourceAsync"/> (the SAME central decision the
///   REST/realtime paths use, so an asset's audience access can never diverge from its target's visibility
///   — docs/05_MODULE_CONTRACTS.md: do not duplicate visibility logic). It ALLOWS as soon as ANY linked
///   target is visible to the audience; otherwise DENIES.</item>
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
/// PARTICIPANT-LEVEL vs ROLE-LEVEL. This is a ROLE-level decision (the download route authorizes by the
/// caller's workspace role, not a specific participant record), so it uses the audience-WIDE
/// <see cref="VisibilityPolicy.CanViewResourceAsync"/>: a selected-participant reveal (a rule scoped to one
/// participant) does NOT grant a role-level audience download, exactly as the role-level visibility policy
/// treats it. Per-participant asset access (a participant downloading an asset linked to a resource
/// revealed only to them) would build on <c>CanParticipantViewResource</c> and a participant-identified
/// download route; that route does not exist, so it is deferred and the role-level decision stays
/// fail-closed.
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
    /// Decides whether a viewer with the given workspace role may download the given asset, applying the
    /// host-content vs audience-visible rules over the asset's links and the central Visibility engine.
    /// </summary>
    /// <param name="asset">The already-resolved, tenant- and workspace-scoped asset.</param>
    /// <param name="viewerRole">The caller's role in the asset's own workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the viewer may download; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    public async Task<bool> CanDownloadAsync(
        Asset asset,
        MembershipRole viewerRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // HOST-CONTENT roles may always download — short-circuit to ALLOW before any database read.
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

        // AUDIENCE role: ALLOW iff the asset is linked to a content block or entity the audience may see.
        // The links are tenant- and workspace-scoped to the asset's own boundary, and each target's
        // visibility is the central Visibility engine's decision (reused, never duplicated). The asset
        // becomes audience-accessible the moment ANY linked target is audience-visible.
        var links = await _links
            .ListByAssetAsync(asset.OrganizationId, asset.WorkspaceId, asset.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var link in links)
        {
            var decision = await _visibility
                .CanViewResourceAsync(
                    asset.OrganizationId,
                    asset.WorkspaceId,
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
}
