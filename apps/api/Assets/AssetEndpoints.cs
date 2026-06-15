using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Assets;

/// <summary>
/// HTTP endpoints of the Assets module's signed URL flows. It realizes the documented request flow
/// (authentication -&gt; tenant/workspace context resolver -&gt; endpoint -&gt; authorization policy -&gt;
/// command, docs/02_ARCHITECTURE.md), mirroring <see cref="LiveCore.Api.Visibility.RevealEndpoints"/>,
/// <see cref="LiveCore.Api.Scenes.SceneEndpoints"/> and <see cref="LiveCore.Api.Content.ContentBlockEndpoints"/>.
///
/// Routes owned by these stories (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST /api/v1/assets/upload-intent</c> — module <b>Assets</b>, roles
///   "Host,CoHost,Owner,Admin", "Creates upload intent" (CORE-AST-003).</item>
///   <item><c>GET /api/v1/assets/{assetId}/download-url</c> — module <b>Assets</b>, "authorized viewers",
///   "Signed URL after permission check" (CORE-AST-004).</item>
///   <item><c>POST /api/v1/assets/{assetId}/links</c> — module <b>Assets</b>, roles
///   "Host,CoHost,Owner,Admin", "Link asset to content block or entity" (CORE-AST-005).</item>
///   <item><c>DELETE /api/v1/assets/{assetId}/links/{linkId}</c> — module <b>Assets</b>, roles
///   "Host,CoHost,Owner,Admin", asset-link removal (CORE-LIFE-007): a host UNLINKS an asset from a content
///   block or entity. The inverse of the link route; it removes only the one link row through
///   <see cref="AssetLinkService.UnlinkAsync"/> — the asset and the linked target are BOTH unaffected. The
///   tenant resolution and object-level authorization mirror the asset link/delete routes; on success it
///   returns <c>204 No Content</c>, and removing a non-existent link is a safe hidden-404. Faithful to the
///   add-link precedent, the removal emits no event and writes no audit record.</item>
///   <item><c>DELETE /api/v1/assets/{assetId}</c> — module <b>Assets</b>, roles "Host,CoHost,Owner,Admin",
///   host-initiated asset deletion (CORE-LIFE-006): removes the asset together with its links and its
///   underlying storage object, atomically and audited, through <see cref="AssetDeletionService"/>. The
///   tenant resolution and object-level authorization mirror the signed download route; on success it returns
///   <c>204 No Content</c>, and deleting a non-existent asset is a safe hidden-404. The storage object is
///   deleted BEFORE the row, so an unconfigured storage backend fails closed with 503 and leaves no dangling
///   row (cascade, not block; docs/adr/0012-resource-deletion-cascades-dependents.md; threat T4).</item>
/// </list>
///
/// UPLOAD-INTENT (CORE-AST-003) — the command registers a new <see cref="AssetStatus.Pending"/>
/// <see cref="Asset"/> with SERVER-MINTED storage coordinates and returns the short-lived, signed upload
/// URL the client uploads the object with (<see cref="AssetUploadIntentService"/>, reusing the CORE-AST-001
/// aggregate and the CORE-AST-002 <see cref="IAssetStorage"/> adapter port). The asset is PRIVATE by
/// default: the only access handed out is one short-lived signed upload URL after the permission check
/// passes (the epic acceptance criterion: "Assets are private by default and accessed only through
/// authorized signed URLs"; threat T4 "Asset leak"). The client never supplies the bucket or object key,
/// so it can never point an upload at another tenant's or workspace's object (threats T5/T1).
///
/// The route has no path parameters, so the target organization is the body's <c>organizationSlug</c>
/// resolved by <see cref="TenantContextResolver"/> (token claim AND persisted membership, threat T5) and
/// the workspace is the body's <c>workspaceId</c>; the caller is authorized by their role in THAT
/// workspace. Fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A malformed body or a missing <c>organizationSlug</c> is 400; an empty/malformed
///   <c>workspaceId</c>, a denied tenant resolution, and a caller who is not a member of the target
///   workspace are ALL hidden as 404 (never distinguishable, never 403 for a non-member; threats
///   T1/T5).</item>
///   <item>A known workspace member who lacks the upload role (Owner/Admin/Host/CoHost — the "Send private
///   content" / asset-write capability of docs/06_AUTHORIZATION_MATRIX.md, same role set as
///   csv/api_routes.csv) is 403. <see cref="MembershipRole"/> is non-linear, so the role check is EXACT,
///   never an ordering comparison.</item>
///   <item>Only AFTER authorization is the rest of the request validated (so an unauthorized caller never
///   receives request-shape feedback): a missing or invalid <c>contentType</c> is 400.</item>
///   <item>503 when no object storage is configured: the fail-closed <see cref="UnconfiguredAssetStorage"/>
///   throws <see cref="AssetStorageNotConfiguredException"/> and NO asset is persisted (private-by-default
///   holds even unconfigured), mirroring how the host denies cleanly without a database or OIDC authority.</item>
/// </list>
///
/// SIGNED DOWNLOAD (CORE-AST-004) — the read flow that hands an authorized viewer a short-lived, signed
/// DOWNLOAD URL for an asset's stored object, the "download URL requires authorization" step of
/// docs/12_STORAGE_ASSETS.md. This is the epic acceptance criterion made observable: an asset is reachable
/// ONLY through an authorized signed URL minted AFTER a server-side permission check (threat T4 "Asset
/// leak"); there is no public or static URL in any status.
///
/// Tenant resolution + object-level authorization mirror the by-scene-id content-block route. The route
/// path carries only <c>{assetId}</c>, so the target organization is a required <c>?organizationSlug=</c>
/// QUERY parameter resolved by <see cref="TenantContextResolver"/>; the asset is then loaded WITHIN that
/// resolved tenant via <see cref="IAssetRepository.FindByIdInOrganizationAsync"/> (the predicate leads with
/// the organization id, so a foreign-tenant asset is never found), the asset's own workspace id is
/// discovered from the loaded row AFTER the tenant boundary has been enforced, and the caller is authorized
/// by their WORKSPACE role in the ASSET'S own workspace. Load-then-authorize, fail-closed at every step and
/// never leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A missing <c>organizationSlug</c> is 400; a malformed/empty asset id, a denied tenant
///   resolution, an asset not present in the resolved tenant, and a caller who is not a member of the
///   asset's workspace are ALL hidden as 404 (never distinguishable, never 403 for a non-member; threats
///   T1/T5).</item>
///   <item>A known member of the asset's workspace who is not an authorized VIEWER is 403. In this story
///   the authorized viewers are the host-content roles (Owner/Admin/Host/CoHost — the "View host-only
///   content" capability of docs/06_AUTHORIZATION_MATRIX.md, reused through the central Visibility module's
///   <see cref="VisibilityRoles.ViewsHostOnlyContent"/> so visibility logic is not duplicated). Audience
///   roles (Participant/Observer) and the audit role are DENIED fail-closed: an asset becomes visible to
///   the audience only once it is linked to a visible content block/entity (CORE-AST-005), which does not
///   exist yet, so there is no rule that can grant them access — until then only host-content roles may
///   download.</item>
///   <item>An authorized viewer requesting a still-<see cref="AssetStatus.Pending"/> asset is 409: the
///   object's upload is not yet confirmed, so it is not downloadable (mirrors the session lifecycle's 409
///   for an out-of-state command). Only an authorized viewer ever learns this, so no asset state leaks to a
///   non-member or an unauthorized role.</item>
///   <item>503 when no object storage is configured: the fail-closed <see cref="UnconfiguredAssetStorage"/>
///   throws <see cref="AssetStorageNotConfiguredException"/> and NO URL is produced (private-by-default
///   holds even unconfigured).</item>
/// </list>
///
/// SESSION-SCOPED AUDIENCE DOWNLOAD (CORE-SVIS-003, completed by CORE-SVIS-004). Once an asset can be linked
/// to a visible resource (CORE-AST-005), an audience caller may download it. A reveal is SESSION-scoped (ADR
/// 0013), so EVERY audience download is authorized against the SESSION-scoped visibility of the linked
/// resource, never the workspace-wide one. The audience caller supplies the session in a <c>?sessionId=</c>
/// QUERY parameter (mirroring the participant-visible feed):
/// <list type="bullet">
///   <item>A <c>Participant</c>'s links are gated by the SAME per-participant primitive the feed uses
///   (<see cref="AssetDownloadPolicy.CanParticipantDownloadAsync"/> over
///   <see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>),
///   so a participant cannot obtain a download URL for an asset tied to a resource revealed only in a SIBLING
///   session, nor for one revealed only to ANOTHER participant. A participant-role member with no active
///   participant record in the asset's workspace is DENIED fail-closed (403).</item>
///   <item>The non-participant audience role <c>Observer</c> is gated by the session-scoped ROLE-level
///   decision (<see cref="AssetDownloadPolicy.CanDownloadAsync"/> over
///   <see cref="VisibilityPolicy.CanViewResourceAsync(System.Guid, System.Guid, System.Guid, MembershipRole, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>),
///   so an Observer can download only when a linked target is audience-wide visible IN THE SUPPLIED session —
///   never one revealed only in a sibling session (the cross-session leak CORE-SVIS-004 closes; threat
///   T5/T3).</item>
/// </list>
/// The <c>sessionId</c> is required for any audience caller (a known member of the asset's workspace, so its
/// absence is a request-shape 400 surfaced only after the membership gate). Host-content roles
/// (Owner/Admin/Host/CoHost) need no <c>sessionId</c> — their content access is session-agnostic — and the
/// audit role and any undefined role are DENIED fail-closed.
///
/// Persistence dependency: like the reveal/scene endpoints, these use the repositories, the tenant context
/// resolver, the upload-intent service and the storage adapter, which are registered only when a database
/// connection string is configured (see <c>Program.cs</c>); when persistence is off the endpoints fail
/// closed with 503.
/// </summary>
internal static class AssetEndpoints
{
    /// <summary>Required query parameter naming the target organization on the by-asset-id route.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    /// <summary>
    /// Query parameter naming the session a PARTICIPANT's download is scoped to (CORE-SVIS-003). A reveal is
    /// session-scoped (ADR 0013), so a participant's download is "may I see the linked resource IN this
    /// session?"; a reveal in a sibling session of the same workspace is never honoured. Required only for a
    /// participant caller; host/role-level (non-participant) access ignores it.
    /// </summary>
    private const string _sessionIdQuery = "sessionId";

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/assets")
            .RequireAuthorization();

        group.MapPost("/upload-intent", CreateUploadIntentAsync);
        group.MapGet("/{assetId}/download-url", CreateDownloadUrlAsync);
        group.MapPost("/{assetId}/links", CreateAssetLinkAsync);
        group.MapDelete("/{assetId}/links/{linkId}", DeleteAssetLinkAsync);
        group.MapDelete("/{assetId}", DeleteAssetAsync);

        return endpoints;
    }

    // POST /api/v1/assets/upload-intent
    private static async Task<IResult> CreateUploadIntentAsync(
        HttpContext httpContext,
        [FromBody] CreateUploadIntentRequest? request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // A missing/unparseable body cannot carry the target organization or workspace; 400. (Malformed
        // JSON is rejected as 400 by the framework before the handler.)
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // The target organization is required to resolve the tenant; it is supplied in the body (this
        // route has no path parameters).
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty workspace id can never address a stored workspace; hidden as 404.
        if (request.WorkspaceId == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of the TARGET workspace. A caller who is
        // a member of the tenant but NOT of the workspace must not learn the workspace exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the workspace read-one route (threats
        // T1/T5). The member's workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, request.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The caller is a known member of the workspace, so an insufficient role is 403. The upload roles
        // are Owner/Admin/Host/CoHost (csv/api_routes.csv "Host,CoHost,Owner,Admin"; the asset-write
        // capability of docs/06_AUTHORIZATION_MATRIX.md). MembershipRole is non-linear, so this is an EXACT
        // set membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never receives
        // request-shape feedback.
        if (!Asset.IsValidContentType(request.ContentType?.Trim()))
        {
            return ValidationError("A valid content type is required.");
        }

        // The declared object size must be strictly positive: it is reserved against the workspace's storage
        // quota (CORE-MON-006), so a missing/non-positive size can never name a real upload. Validated after
        // the content type so an authorized caller still gets ordered request-shape feedback.
        if (request.SizeBytes <= 0)
        {
            return ValidationError("A positive 'sizeBytes' value is required.");
        }

        var now = timeProvider.GetUtcNow();

        AssetUploadIntentResult result;
        try
        {
            result = await deps.UploadIntents
                .CreateAsync(
                    context.OrganizationId,
                    request.WorkspaceId,
                    context.UserProfileId,
                    request.ContentType!.Trim(),
                    request.SizeBytes,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssetStorageNotConfiguredException)
        {
            // No object storage is configured for this deployment: fail closed. The command minted no URL
            // and persisted no asset, so the private-by-default posture holds (threat T4). The response
            // never leaks any storage coordinate (the exception message carries only the operation name).
            return StorageUnavailable();
        }

        // The upload would take the workspace over its asset.storage.bytes.max storage quota (CORE-MON-006):
        // 409, nothing consumed and nothing persisted. The caller is authorized by role; the limit, not the
        // caller, is the reason, so this is a 409 rather than a 403 — the same mapping the workspace-create and
        // session-start quota gates use.
        if (result.Outcome == AssetUploadIntentOutcome.QuotaExceeded)
        {
            // Record the denial as a real audit fact (CORE-SPEC-002: AuditAction.QuotaExceeded). A tenant-scoped
            // fact: the caller (the audited actor) is denied for the workspace's asset.storage.bytes.max quota
            // subject. The intent minted no URL and persisted no asset, so this stands alone (no transaction).
            await deps.AuditLog
                .AppendAsync(
                    AuditLogEntry.ForQuotaExceeded(
                        context.OrganizationId,
                        request.WorkspaceId,
                        context.UserProfileId,
                        nameof(EntitlementSubjectType.Workspace),
                        request.WorkspaceId,
                        now),
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaExceeded(result.QuotaDenial!);
        }

        // The created resource is reached through its signed download route; point the Location there
        // (csv/api_routes.csv GET /api/v1/assets/{assetId}/download-url, CORE-AST-004).
        var intent = result.Intent!;
        var response = UploadIntentResponse.From(intent.Asset, intent.UploadUrl);
        return Results.Created($"/api/v1/assets/{intent.Asset.Id}/download-url", response);
    }

    // GET /api/v1/assets/{assetId}/download-url?organizationSlug={slug}&sessionId={sessionId}
    private static async Task<IResult> CreateDownloadUrlAsync(
        HttpContext httpContext,
        string assetId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = _sessionIdQuery)] string? sessionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // The target organization is required to resolve the tenant; the route path carries only the asset
        // id, so it is a query parameter exactly like the by-scene-id content-block route.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty asset id can never address a stored asset; hidden as 404, never echoing why.
        if (!Guid.TryParse(assetId, out var assetGuid) || assetGuid == Guid.Empty)
        {
            return HiddenAsset();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the asset as 404 (threat T5).
            return HiddenAsset();
        }

        var context = resolution.Context;

        // Load the asset WITHIN the resolved tenant. The lookup leads with the organization id, so an asset
        // in another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown
        // asset is hidden as 404 (threats T1/T5). The asset's own workspace id is then discovered from the
        // loaded row, AFTER the tenant boundary has been enforced.
        var asset = await deps.Assets
            .FindByIdInOrganizationAsync(context.OrganizationId, assetGuid, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return HiddenAsset();
        }

        // Object-level authorization: the caller must be a member of the ASSET'S workspace. A caller who is
        // a member of the tenant but NOT of the asset's workspace must not learn the asset exists, so a
        // missing membership is hidden as 404 (not 403) — the same rule as the content-block route (threats
        // T1/T5). The lookup is scoped by organization id then the asset's own workspace id, so a role held
        // in a DIFFERENT workspace never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, asset.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenAsset();
        }

        // The caller is a known member of the asset's workspace, so an insufficient role is 403.
        // Authorization is the central Assets download policy (CORE-AST-005), which reuses the Visibility
        // engine so asset access never diverges from content visibility (docs/05_MODULE_CONTRACTS.md). A
        // reveal is session-scoped (ADR 0013), so EVERY audience download is now session-scoped
        // (CORE-SVIS-004 removed the workspace-wide carve-out). The decision is split by the caller's
        // workspace role:
        //   * A PARTICIPANT's download is SESSION-scoped per-PARTICIPANT (CORE-SVIS-003): they may download
        //     only when the asset is linked to a content block/entity VISIBLE to THEM IN THE SUPPLIED
        //     session, gated by the same per-participant primitive the participant feed uses, so an asset
        //     tied to a resource revealed only in a SIBLING session is never downloadable (threat T5/T3).
        //   * The non-participant AUDIENCE role (Observer) is SESSION-scoped at the ROLE level: it must name
        //     the session and may download only when a linked target is audience-wide visible IN THAT
        //     session, so an Observer cannot download a resource revealed only in a sibling session either.
        //   * Host-content roles (Owner/Admin/Host/CoHost) may always download and need no session (host
        //     content access is session-agnostic); the audit role and any undefined role are DENIED
        //     fail-closed (threat T4 "Asset leak"; threat T2 visibility leak).
        // MembershipRole is non-linear, so the role check is EXACT, never an ordering comparison.
        if (member.Role == MembershipRole.Participant)
        {
            var participantDenial = await AuthorizeParticipantDownloadAsync(
                deps, context, asset, sessionId, cancellationToken).ConfigureAwait(false);
            if (participantDenial is not null)
            {
                return participantDenial;
            }
        }
        else
        {
            var roleLevelDenial = await AuthorizeRoleLevelDownloadAsync(
                deps, asset, sessionId, member.Role, cancellationToken).ConfigureAwait(false);
            if (roleLevelDenial is not null)
            {
                return roleLevelDenial;
            }
        }

        // Downloadable only once the upload is confirmed (Available): a still-pending asset's object may not
        // exist in storage, so it is not downloadable. This is reported as 409 (an out-of-state request),
        // AFTER authorization, so only an authorized viewer ever learns the asset is pending.
        if (!asset.IsAvailable)
        {
            return AssetNotDownloadable();
        }

        SignedAssetUrl downloadUrl;
        try
        {
            downloadUrl = await deps.Storage
                .CreateDownloadUrlAsync(asset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssetStorageNotConfiguredException)
        {
            // No object storage is configured for this deployment: fail closed. No URL is produced, so the
            // private-by-default posture holds (threat T4). The response never leaks any storage coordinate
            // (the exception message carries only the operation name).
            return StorageUnavailable();
        }

        return Results.Ok(DownloadUrlResponse.From(asset, downloadUrl));
    }

    /// <summary>
    /// The SESSION-scoped per-participant download authorization (CORE-SVIS-003) for a
    /// <see cref="MembershipRole.Participant"/> caller who is already a known member of the asset's
    /// workspace. Returns a non-null <see cref="IResult"/> to short-circuit the request (the denial/validation
    /// response), or <see langword="null"/> when the participant is authorized and the caller should continue
    /// to mint the URL. Fail-closed at every step:
    /// <list type="bullet">
    ///   <item>A reveal is session-scoped (ADR 0013), so the participant must name the session. The caller is
    ///   a known member, so a missing/blank or malformed <c>sessionId</c> is surfaced as a request-shape 400
    ///   (mirrors the participant-visible feed) — not a 404, because the asset's existence is already
    ///   disclosed to the member.</item>
    ///   <item>The caller's participant identity is resolved in the asset's OWN workspace
    ///   (<see cref="IParticipantRepository.FindByUserAsync"/>). A participant-role member with no participant
    ///   record there — or a soft-REMOVED one — holds no per-participant visibility, so it is DENIED 403
    ///   (a known member who is not an authorized viewer, exactly as the role-level path returns 403).</item>
    ///   <item>The asset's links are gated by <see cref="AssetDownloadPolicy.CanParticipantDownloadAsync"/>,
    ///   the same SESSION-scoped per-participant primitive the feed uses (reused, not duplicated;
    ///   docs/05_MODULE_CONTRACTS.md). A resource visible only in a SIBLING session, or only to ANOTHER
    ///   participant, yields no visible rule here, so the download is DENIED 403 (threat T5/T3). A foreign or
    ///   unknown session id matches no rule in the asset's workspace, so it too is a fail-closed 403 — no
    ///   session existence is probed or leaked.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult?> AuthorizeParticipantDownloadAsync(
        AssetEndpointDependencies deps,
        TenantContext context,
        Asset asset,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        // A participant download is session-scoped, so the participant must name the session. The caller is a
        // known member of the asset's workspace, so the request-shape error is surfaced to them (404-hide
        // already passed). A missing/blank value is 400; a malformed/empty id is 400.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return MissingSession();
        }

        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return ValidationError($"The '{_sessionIdQuery}' value is not a valid session id.");
        }

        // Resolve the caller's participant identity in the asset's OWN workspace. A participant-role member
        // with no participant record there has no per-participant visibility to evaluate, and a soft-removed
        // participant holds no standing (ParticipantStatus.Removed), so either is DENIED fail-closed (403).
        var participant = await deps.Participants
            .FindByUserAsync(context.OrganizationId, asset.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null || participant.Status != ParticipantStatus.Active)
        {
            return Forbidden();
        }

        // Gate on the SESSION-scoped per-participant visibility of the asset's linked resource(s) — the same
        // primitive the participant feed uses, so asset download never diverges from feed visibility
        // (docs/05_MODULE_CONTRACTS.md: do not duplicate visibility logic). A resource revealed only in a
        // sibling session, or only to another participant, grants no download here (threats T5/T3/T2).
        if (!await deps.DownloadPolicy
            .CanParticipantDownloadAsync(asset, sessionGuid, participant.Id, cancellationToken)
            .ConfigureAwait(false))
        {
            return Forbidden();
        }

        return null;
    }

    /// <summary>
    /// The ROLE-level download authorization for a non-<see cref="MembershipRole.Participant"/> caller who is
    /// already a known member of the asset's workspace. Returns a non-null <see cref="IResult"/> to
    /// short-circuit the request (the denial/validation response), or <see langword="null"/> when the caller
    /// is authorized and the caller should continue to mint the URL. A reveal is session-scoped (ADR 0013), so
    /// the NON-participant audience role (Observer) is now session-scoped too (CORE-SVIS-004): it must name the
    /// session, and may download only when a linked target is audience-wide visible in THAT session — an asset
    /// tied to a resource revealed only in a SIBLING session of the same workspace is never downloadable
    /// (threat T5/T3). Fail-closed at every step:
    /// <list type="bullet">
    ///   <item>Host-content roles (Owner/Admin/Host/CoHost) need no session — their content access is
    ///   session-agnostic — so no <c>sessionId</c> is required of them and the policy allows them
    ///   regardless.</item>
    ///   <item>The audience role (Observer) MUST name the session. The caller is a known member, so a
    ///   missing/blank or malformed <c>sessionId</c> is surfaced as a request-shape 400 (mirroring the
    ///   participant path) — not a 404, because the asset's existence is already disclosed to the
    ///   member.</item>
    ///   <item>The asset's links are gated by <see cref="AssetDownloadPolicy.CanDownloadAsync"/> over the
    ///   session-scoped role-level visibility decision; the audit role and any undefined role are DENIED 403,
    ///   and a foreign or unknown session id matches no rule so it too is a fail-closed 403.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult?> AuthorizeRoleLevelDownloadAsync(
        AssetEndpointDependencies deps,
        Asset asset,
        string? sessionId,
        MembershipRole viewerRole,
        CancellationToken cancellationToken)
    {
        // The non-participant audience role (Observer) is session-scoped (a reveal is session-scoped, ADR
        // 0013 / CORE-SVIS-004), so it must name the session, validated as a request-shape 400 (the caller is
        // a known member). Host-content roles need no session (their access is session-agnostic) and the
        // audit/undefined roles are denied below regardless of the session.
        var sessionScope = Guid.Empty;
        if (VisibilityRoles.IsAudienceRole(viewerRole))
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return MissingSession();
            }

            if (!Guid.TryParse(sessionId, out sessionScope) || sessionScope == Guid.Empty)
            {
                return ValidationError($"The '{_sessionIdQuery}' value is not a valid session id.");
            }
        }

        return await deps.DownloadPolicy
            .CanDownloadAsync(asset, viewerRole, sessionScope, cancellationToken)
            .ConfigureAwait(false)
            ? null
            : Forbidden();
    }

    // POST /api/v1/assets/{assetId}/links
    private static async Task<IResult> CreateAssetLinkAsync(
        HttpContext httpContext,
        string assetId,
        [FromBody] CreateAssetLinkRequest? request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // A missing/unparseable body cannot carry the target organization or the linked resource; 400.
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // The target organization is required to resolve the tenant; it is supplied in the body (the route
        // path carries only the asset id), exactly like the reveal command.
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty asset id can never address a stored asset; hidden as 404, never echoing why.
        if (!Guid.TryParse(assetId, out var assetGuid) || assetGuid == Guid.Empty)
        {
            return HiddenAsset();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the asset as 404 (threat T5).
            return HiddenAsset();
        }

        var context = resolution.Context;

        // Load the asset WITHIN the resolved tenant (org boundary leads); a cross-tenant or unknown asset is
        // hidden as 404. The asset's own workspace is then discovered from the loaded row, AFTER the tenant
        // boundary has been enforced (mirrors the signed download route).
        var asset = await deps.Assets
            .FindByIdInOrganizationAsync(context.OrganizationId, assetGuid, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return HiddenAsset();
        }

        // Object-level authorization: the caller must be a member of the ASSET'S workspace. A caller who is
        // a member of the tenant but NOT of the asset's workspace must not learn the asset exists, so a
        // missing membership is hidden as 404 (not 403) — the same rule as the download route (threats
        // T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, asset.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenAsset();
        }

        // The caller is a known member of the asset's workspace, so an insufficient role is 403. The link
        // (host-preparation) roles are Owner/Admin/Host/CoHost (csv/api_routes.csv "Host,CoHost,Owner,Admin";
        // the content-control capability of docs/06_AUTHORIZATION_MATRIX.md, the same set as the
        // upload-intent and reveal commands). MembershipRole is non-linear, so this is an EXACT set
        // membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never receives
        // request-shape feedback. The target kind is parsed by NAME; a numeric or unknown value is rejected.
        if (!TryParseTargetType(request.TargetType, out var targetType))
        {
            return ValidationError("A valid target type is required (ContentBlock or Entity).");
        }

        if (request.TargetId == Guid.Empty)
        {
            return ValidationError("The 'targetId' value is required.");
        }

        var now = timeProvider.GetUtcNow();

        // The command verifies the target exists in the asset's OWN workspace (the same-workspace coupling
        // for the polymorphic target reference; threats T5/T1) and then persists the link. A target not in
        // the workspace is hidden as 404; a repeat of the same link is 409 (no duplicate is created).
        var result = await deps.AssetLinks
            .LinkAsync(asset, targetType, request.TargetId, context.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            AssetLinkOutcome.Linked => Results.Created(
                $"/api/v1/assets/{asset.Id}/links/{result.Link!.Id}",
                AssetLinkResponse.From(result.Link)),
            AssetLinkOutcome.AlreadyLinked => AssetAlreadyLinked(),
            // The target content block / entity is not in the asset's workspace: hidden as 404, reported
            // only to an authorized host so no cross-workspace resource existence leaks (threats T1/T5).
            _ => HiddenAsset(),
        };
    }

    // DELETE /api/v1/assets/{assetId}/links/{linkId}?organizationSlug={slug}
    //
    // Removes ONE asset link (CORE-LIFE-007, the "Resource Lifecycle and Deletion" epic): a host UNLINKS an
    // asset from a content block or entity. The asset and the linked target are BOTH unaffected — only the
    // link row is removed (the inverse of POST /api/v1/assets/{assetId}/links). The tenant resolution and
    // object-level authorization mirror the asset link/delete routes exactly — the route path carries the
    // asset id, so the target organization is a required ?organizationSlug= QUERY parameter resolved by the
    // TenantContextResolver; the asset is loaded WITHIN that resolved tenant via FindByIdInOrganizationAsync
    // (the predicate leads with the organization id, so a foreign-tenant asset is never found), the asset's
    // own workspace id is discovered from the loaded row AFTER the tenant boundary has been enforced, and the
    // caller is authorized by their WORKSPACE role in the ASSET'S own workspace. Load-then-authorize,
    // fail-closed at every step and never leaking why:
    //   * 503 when persistence is off; 401 when the principal cannot be mapped.
    //   * A missing organizationSlug is 400; a malformed/empty asset id, a malformed/empty link id, a denied
    //     tenant resolution, an asset not present in the resolved tenant, a caller who is not a member of the
    //     asset's workspace, an unknown link, and a link that attaches a DIFFERENT asset are ALL hidden as 404
    //     (never distinguishable, never 403 for a non-member; threats T1/T5).
    //   * A known member of the asset's workspace who lacks the unlink role is 403. Asset links are
    //     host-prepared content, so the unlink role set is the host-capable Owner/Admin/Host/CoHost (the same
    //     set that creates upload intents and links, and that deletes scenes/entities/content blocks/assets;
    //     docs/06_AUTHORIZATION_MATRIX.md). MembershipRole is non-linear, so the role check is EXACT, never an
    //     ordering comparison.
    // On success the one link row is removed and the route returns 204 No Content; removing a non-existent link
    // is a safe hidden-404 that changes nothing. Faithful to the add-link precedent (CORE-AST-005) and the
    // entity-relationship removal (CORE-LIFE-002), the removal emits no event and writes no audit record.
    private static async Task<IResult> DeleteAssetLinkAsync(
        HttpContext httpContext,
        string assetId,
        string linkId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // The target organization is required to resolve the tenant; the route path carries only the asset
        // and link ids, so it is a query parameter exactly like the signed download and asset delete routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty asset id can never address a stored asset; hidden as 404, never echoing why.
        if (!Guid.TryParse(assetId, out var assetGuid) || assetGuid == Guid.Empty)
        {
            return HiddenAsset();
        }

        // A malformed/empty link id can never address a stored link; hidden as 404, never echoing why.
        if (!Guid.TryParse(linkId, out var linkGuid) || linkGuid == Guid.Empty)
        {
            return HiddenAssetLink();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the asset as 404 (threat T5).
            return HiddenAsset();
        }

        var context = resolution.Context;

        // Load the asset WITHIN the resolved tenant (org boundary leads); a cross-tenant or unknown asset is
        // hidden as 404. The asset's own workspace is then discovered from the loaded row, AFTER the tenant
        // boundary has been enforced (mirrors the signed download, link and delete routes).
        var asset = await deps.Assets
            .FindByIdInOrganizationAsync(context.OrganizationId, assetGuid, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return HiddenAsset();
        }

        // Object-level authorization: the caller must be a member of the ASSET'S workspace. A caller who is a
        // member of the tenant but NOT of the asset's workspace must not learn the asset exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the link/download routes (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, asset.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenAsset();
        }

        // The caller is a known member of the asset's workspace, so an insufficient role is 403. The unlink
        // (host-preparation) roles are Owner/Admin/Host/CoHost (the same host-capable set that links assets
        // and deletes scenes/entities/content blocks/assets; docs/06_AUTHORIZATION_MATRIX.md). MembershipRole
        // is non-linear, so this is an EXACT set membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized: remove the one link that attaches this asset to its target. The command re-resolves the
        // link through the tenant- AND workspace-scoped FindByIdAsync and confirms it attaches the addressed
        // asset, so a link in another workspace/tenant, or one attaching a different asset, is never removed
        // even when its id is known; an unknown id is a SAFE 404 that changes nothing (threats T1/T5). Only
        // the link row is removed — the asset and the linked target are both untouched.
        var result = await deps.AssetLinks
            .UnlinkAsync(asset, linkGuid, cancellationToken)
            .ConfigureAwait(false);

        return result == AssetUnlinkResult.Removed
            ? Results.NoContent()
            : HiddenAssetLink();
    }

    // DELETE /api/v1/assets/{assetId}?organizationSlug={slug}
    //
    // Deletes ONE asset (CORE-LIFE-006, the "Resource Lifecycle and Deletion" epic): a host removes an asset
    // together with its asset links and its underlying storage object. The tenant resolution and object-level
    // authorization mirror the signed download route exactly — the route path carries only {assetId}, so the
    // target organization is a required ?organizationSlug= QUERY parameter resolved by the TenantContextResolver;
    // the asset is loaded WITHIN that resolved tenant via FindByIdInOrganizationAsync (the predicate leads with
    // the organization id, so a foreign-tenant asset is never found), the asset's own workspace id is discovered
    // from the loaded row AFTER the tenant boundary has been enforced, and the caller is authorized by their
    // WORKSPACE role in the ASSET'S own workspace. Load-then-authorize, fail-closed at every step and never
    // leaking why:
    //   * 503 when persistence is off; 401 when the principal cannot be mapped.
    //   * A missing organizationSlug is 400; a malformed/empty asset id, a denied tenant resolution, an asset
    //     not present in the resolved tenant, and a caller who is not a member of the asset's workspace are ALL
    //     hidden as 404 (never distinguishable, never 403 for a non-member; threats T1/T5).
    //   * A known member of the asset's workspace who lacks the delete role is 403. Assets are host-prepared
    //     content, so the delete role set is the host-capable Owner/Admin/Host/CoHost (the same set that creates
    //     upload intents and links assets, and that deletes scenes/entities/content blocks;
    //     docs/06_AUTHORIZATION_MATRIX.md). MembershipRole is non-linear, so the role check is EXACT, never an
    //     ordering comparison.
    //   * 503 when no object storage is configured: the deletion service deletes the storage object BEFORE the
    //     metadata row, so the fail-closed UnconfiguredAssetStorage throws and the transaction rolls back having
    //     deleted NOTHING (no dangling row, private-by-default holds even unconfigured; threat T4), exactly as
    //     the upload-intent flow fails closed.
    // On success the deletion (its link cascade, storage object delete and audit append) runs atomically through
    // the tenant- and workspace-scoped deletion service and returns 204 No Content; deleting a non-existent
    // asset is a safe hidden-404.
    private static async Task<IResult> DeleteAssetAsync(
        HttpContext httpContext,
        string assetId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // The target organization is required to resolve the tenant; the route path carries only the asset
        // id, so it is a query parameter exactly like the signed download route.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty asset id can never address a stored asset; hidden as 404, never echoing why.
        if (!Guid.TryParse(assetId, out var assetGuid) || assetGuid == Guid.Empty)
        {
            return HiddenAsset();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the asset as 404 (threat T5).
            return HiddenAsset();
        }

        var context = resolution.Context;

        // Load the asset WITHIN the resolved tenant (org boundary leads); a cross-tenant or unknown asset is
        // hidden as 404. The asset's own workspace is then discovered from the loaded row, AFTER the tenant
        // boundary has been enforced (mirrors the signed download and link routes).
        var asset = await deps.Assets
            .FindByIdInOrganizationAsync(context.OrganizationId, assetGuid, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return HiddenAsset();
        }

        // Object-level authorization: the caller must be a member of the ASSET'S workspace. A caller who is a
        // member of the tenant but NOT of the asset's workspace must not learn the asset exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the download route (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, asset.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenAsset();
        }

        // The caller is a known member of the asset's workspace, so an insufficient role is 403. The delete
        // (host-preparation) roles are Owner/Admin/Host/CoHost (the same host-capable set that creates upload
        // intents and links, and that deletes scenes/entities/content blocks;
        // docs/06_AUTHORIZATION_MATRIX.md; docs/adr/0012-resource-deletion-cascades-dependents.md).
        // MembershipRole is non-linear, so this is an EXACT set membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized: delete the asset (cascade its links, delete the storage object, then the row, append the
        // audit record) atomically. The service re-loads the asset through the tenant- AND workspace-scoped
        // FindByIdAsync, so an asset in another workspace or tenant is never deleted even when its id is known;
        // an unknown id is a SAFE 404 that changes nothing (threats T1/T5).
        var now = timeProvider.GetUtcNow();
        AssetDeletionResult result;
        try
        {
            result = await deps.AssetDeletion
                .DeleteAsync(context.OrganizationId, asset.WorkspaceId, assetGuid, context.UserProfileId, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssetStorageNotConfiguredException)
        {
            // No object storage is configured for this deployment: the deletion service deletes the storage
            // object before the row, so the whole transaction rolled back having removed NOTHING (no dangling
            // row, private-by-default holds; threat T4). The response never leaks any storage coordinate (the
            // exception message carries only the operation name).
            return StorageUnavailable();
        }

        return result == AssetDeletionResult.Deleted
            ? Results.NoContent()
            : HiddenAsset();
    }

    /// <summary>
    /// Parses an <see cref="AssetLinkTargetType"/> from its NAME only (case-insensitive), rejecting
    /// null/blank, numeric values and unknown names — so a client cannot smuggle in an undefined enum value
    /// by number (mirrors the reveal command's resource-type parsing and the content-block type parsing).
    /// </summary>
    private static bool TryParseTargetType(string? value, out AssetLinkTargetType targetType)
    {
        targetType = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // A numeric string must not bind to an enum member by value.
        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out targetType)
            && AssetLink.IsValidTargetType(targetType);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out AssetEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var participants = services.GetService<IParticipantRepository>();
        var uploadIntents = services.GetService<AssetUploadIntentService>();
        var assets = services.GetService<IAssetRepository>();
        var storage = services.GetService<IAssetStorage>();
        var assetLinks = services.GetService<AssetLinkService>();
        var downloadPolicy = services.GetService<AssetDownloadPolicy>();
        var assetDeletion = services.GetService<AssetDeletionService>();
        var auditLog = services.GetService<IAuditLogRepository>();

        if (resolver is null
            || workspaceMembers is null
            || participants is null
            || uploadIntents is null
            || assets is null
            || storage is null
            || assetLinks is null
            || downloadPolicy is null
            || assetDeletion is null
            || auditLog is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new AssetEndpointDependencies(
            resolver, workspaceMembers, participants, uploadIntents, assets, storage, assetLinks, downloadPolicy, assetDeletion, auditLog);
        return true;
    }

    private static bool TryMapPrincipal(HttpContext httpContext, out OidcPrincipal principal)
    {
        var result = OidcPrincipalMapper.Map(httpContext.User);
        if (!result.Succeeded)
        {
            principal = null!;
            return false;
        }

        principal = result.Principal;
        return true;
    }

    private static IResult ServiceUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Asset operations require persistence, which is not configured.");

    private static IResult StorageUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Asset storage is not configured.");

    private static IResult Unauthorized()
        => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private static IResult MissingOrganization()
        => ValidationError("The 'organizationSlug' value is required.");

    // A participant download is session-scoped (CORE-SVIS-003): the participant must name the session in a
    // sessionId query parameter. Reported as 400 only to a known member of the asset's workspace, after the
    // membership 404-hide gate, so an unauthorized caller never learns the parameter is required.
    private static IResult MissingSession()
        => ValidationError($"The '{_sessionIdQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // An upload-intent was refused because it would exceed the workspace's server-enforced
    // asset.storage.bytes.max storage quota (CORE-MON-006; docs/08: 409 conflict). The detail names only the
    // generic quota key (the same key the workspace quota-status read returns, so a vertical can map it to
    // paywall copy) and never leaks an internal id or rationale (threat T7). The caller is authorized by role;
    // the limit, not the caller, is the reason, so this is a 409 rather than a 403 — the same mapping as the
    // workspace-create and session-start quota gates.
    private static IResult QuotaExceeded(QuotaEnforcementDecision decision)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: $"This action would exceed the '{decision.EntitlementKey}' quota.");

    // Workspace existence is hidden: an empty/malformed workspace id, a workspace in a foreign or
    // non-entitled tenant, and a workspace the caller does not belong to are ALL reported as 404, never
    // distinguishable and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenWorkspace() => NotFound();

    // Asset existence is hidden: a malformed asset id, an asset in a foreign or non-entitled tenant, an
    // unknown asset, and an asset in a workspace the caller does not belong to are ALL reported as 404,
    // never distinguishable from each other and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenAsset() => NotFound();

    // Asset-link existence is hidden: a malformed link id, an unknown link, a link in a foreign or
    // non-entitled tenant/workspace, and a link that attaches a DIFFERENT asset than the one addressed are
    // ALL reported as 404, never distinguishable from each other and never echoing the reason (docs/08;
    // threats T1/T5).
    private static IResult HiddenAssetLink() => NotFound();

    // An authorized viewer asked to download an asset whose upload is not yet confirmed: the object is not
    // downloadable in its current state (mirrors the session lifecycle's 409 for an out-of-state command).
    private static IResult AssetNotDownloadable()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The asset is not available for download.");

    // An authorized host asked to link an asset to a target it is already linked to (the per-workspace
    // unique key). Reported as 409 only to an authorized host, after authorization, so no link existence
    // leaks to a non-member or an unauthorized role.
    private static IResult AssetAlreadyLinked()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The asset is already linked to the target.");

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct AssetEndpointDependencies(
        TenantContextResolver Resolver,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        AssetUploadIntentService UploadIntents,
        IAssetRepository Assets,
        IAssetStorage Storage,
        AssetLinkService AssetLinks,
        AssetDownloadPolicy DownloadPolicy,
        AssetDeletionService AssetDeletion,
        IAuditLogRepository AuditLog);
}
