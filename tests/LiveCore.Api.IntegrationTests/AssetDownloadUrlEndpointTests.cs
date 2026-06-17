// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the asset signed download flow (CORE-AST-004,
/// <c>GET /api/v1/assets/{assetId}/download-url</c>). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys
/// ON), so the documented request flow (authentication -&gt; tenant context resolver -&gt; endpoint -&gt;
/// inline object-level authorization -&gt; mint) is exercised end-to-end. The happy-path tests substitute a
/// conforming fake <see cref="IAssetStorage"/> for the production fail-closed default (the deployment
/// supplies the real S3-compatible signer), and one test runs against the unmodified app to prove the
/// storage-unconfigured fail-closed path.
///
/// Coverage, per the story's required tests ("Upload/download auth tests and storage adapter tests") and
/// the threat model's required control "asset download denial" (threat T4 "Asset leak"; threat T1 broken
/// object-level authorization; threat T5 tenant isolation):
/// <list type="bullet">
///   <item>HAPPY PATH: a host (and each host-content viewer role) downloads an AVAILABLE asset -&gt; 200
///   with the asset id, Available status, content type and a short-lived signed download URL.</item>
///   <item>AUTHORIZATION + ISOLATION (fail-closed): 401 unauthenticated; the audience/audit roles
///   {Participant, Observer, Auditor} are members of the asset's workspace but are NOT authorized viewers
///   yet (no asset-to-content visibility linkage exists; CORE-AST-005) -&gt; 403; a non-member of the
///   asset's workspace, an asset in another tenant, an unknown asset and a malformed asset id are ALL
///   hidden as 404.</item>
///   <item>STATE: a still-PENDING asset (upload not yet confirmed) is 409 for an authorized viewer.</item>
///   <item>VALIDATION: a missing organizationSlug is 400.</item>
///   <item>FAIL-CLOSED STORAGE: with no storage adapter configured (the production default), an authorized
///   request for an available asset is 503 (private-by-default holds even unconfigured; threat T4).</item>
/// </list>
/// Every successful download asserts the signed URL is absolute and expires in the future; the asset is
/// never made public. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AssetDownloadUrlEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> ViewerRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    // The non-audience, non-host role the role-level download decision denies fail-closed regardless of any
    // session: Auditor is audit-only, so no sessionId is required of it. The Observer audience role is now
    // session-scoped too (CORE-SVIS-004) and is exercised by the session-scoped tests below, not here.
    public static TheoryData<MembershipRole> NonViewerRoles =>
    [
        MembershipRole.Auditor,
    ];

    [Fact]
    public async Task Download_url_unauthenticated_is_401()
    {
        await using var factory = new FakeStorageApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await GetAsync(client, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_gets_a_short_lived_signed_download_url_for_an_available_asset()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(seed.AssetId, body.AssetId);
        Assert.Equal(nameof(AssetStatus.Available), body.Status);
        Assert.Equal("image/png", body.ContentType);
        // The only access handed out is a single, absolute, short-lived signed URL (private by default).
        Assert.True(Uri.TryCreate(body.DownloadUrl, UriKind.Absolute, out _));
        Assert.True(body.ExpiresAt > TestData.SeedTime);
    }

    [Theory]
    [MemberData(nameof(ViewerRoles))]
    public async Task Download_url_is_200_for_a_host_content_viewer_role(MembershipRole role)
    {
        await using var factory = new FakeStorageApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, role, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NonViewerRoles))]
    public async Task Download_url_is_403_for_a_non_viewer_workspace_role(MembershipRole role)
    {
        // The caller is a member of the asset's workspace but is NOT an authorized viewer: the asset is not
        // linked to any visible content (CORE-AST-005), so the audit role is denied fail-closed. Auditor is
        // not an audience role, so no session is required of it (it is denied before any rule lookup).
        await using var factory = new FakeStorageApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, role, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_downloads_an_asset_revealed_in_their_session()
    {
        // CORE-SVIS-004: the non-participant audience role (Observer) download is SESSION-scoped too. An asset
        // linked to a content block revealed audience-wide IN THE OBSERVER'S session is downloadable: the
        // Observer supplies sessionId = the revealed session and gets a signed download URL (200).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "observer-a";
        var seed = await SeedObserverWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadDto>(_json);
        Assert.NotNull(body);
        Assert.True(Uri.TryCreate(body.DownloadUrl, UriKind.Absolute, out _));
    }

    [Fact]
    public async Task Observer_in_a_sibling_session_is_denied_a_download_revealed_only_in_another_session()
    {
        // CROWN-JEWEL for the role-level path. The linked content block is revealed audience-wide only in
        // session A, but the Observer supplies sessionId = B (a sibling session of the SAME workspace). A
        // reveal is session-scoped, so it is NOT visible in B: the download is denied fail-closed (403). This
        // is the residual the ADR 0013 role-level carve-out left, now closed by CORE-SVIS-004.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "observer-a";
        var seed = await SeedObserverWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.SiblingSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_is_403_for_an_asset_hidden_in_their_session()
    {
        // Linked, but the content block is HIDDEN from the audience in session A: the asset stays host-only,
        // so even in that session the Observer is denied fail-closed (403).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "observer-a";
        var seed = await SeedObserverWithLinkedAssetAsync(factory, subject, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Observer_without_a_session_id_is_400()
    {
        // An Observer's download is session-scoped (CORE-SVIS-004), so the Observer MUST name the session.
        // The caller is a known member of the asset's workspace, so omitting it is a request-shape 400 (not a
        // 404), surfaced only after the membership gate — exactly like the participant path.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "observer-a";
        var seed = await SeedObserverWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Observer_with_a_session_is_403_for_an_unlinked_asset()
    {
        // The Observer names a valid session but the asset is not linked to any visible content: denied
        // fail-closed (403), the role-level analogue of the unlinked-asset denial.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "observer-a";
        var seed = await SeedObserverWithLinkedAssetAsync(factory, subject, VisibilityState.Visible, linkAsset: false);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Auditor_is_403_even_for_an_asset_linked_to_a_visible_content_block()
    {
        // The audit role is audit-only, not a live content grant: even a visible link does not grant a
        // download (threat T2 visibility leak; docs/06_AUTHORIZATION_MATRIX.md).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "member-auditor";
        var seed = await SeedWorkspaceWithLinkedAssetAsync(factory, subject, MembershipRole.Auditor, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // =====================================================================
    // SESSION-SCOPED PARTICIPANT DOWNLOAD (CORE-SVIS-003). A participant's download is authorized against
    // the SESSION-scoped visibility of the linked resource, not the workspace-wide one. A reveal is
    // session-scoped (ADR 0013), so a participant cannot obtain a download URL for an asset tied to a
    // resource revealed only in a sibling session of the same workspace; host/role-level access is
    // unchanged. (threats T5/T3 cross-session leak; T4 asset leak; T2 visibility leak.)
    // =====================================================================

    [Fact]
    public async Task Participant_downloads_an_asset_revealed_in_their_session()
    {
        // The linked content block is revealed (audience-wide visible) in session A. The participant supplies
        // sessionId = A and gets a signed download URL (200) — the session-scoped allow.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadDto>(_json);
        Assert.NotNull(body);
        Assert.True(Uri.TryCreate(body.DownloadUrl, UriKind.Absolute, out _));
    }

    [Fact]
    public async Task Participant_in_a_sibling_session_is_denied_a_download_revealed_only_in_another_session()
    {
        // CROWN-JEWEL. The linked content block is revealed only in session A, but the participant supplies
        // sessionId = B (a sibling session of the SAME workspace). A reveal is session-scoped, so it is NOT
        // visible in B: the download is denied fail-closed (403). This is the residual CORE-SVIS-001 left.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.SiblingSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Participant_is_403_for_an_asset_hidden_in_their_session()
    {
        // Linked, but the content block is HIDDEN from the audience in session A: the asset stays host-only,
        // so even in the revealed session the participant is denied fail-closed (403).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(factory, subject, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Participant_downloads_an_asset_revealed_privately_to_them_in_their_session()
    {
        // The other half of session-scope: a reveal scoped to exactly THIS participant in session A makes the
        // asset downloadable for them — proving the per-participant primitive (not just the audience-wide
        // session check) is what authorizes the download.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithPrivatelyRevealedAssetAsync(factory, subject, revealToCaller: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Participant_is_denied_a_download_revealed_only_to_another_participant()
    {
        // The selected-participant guarantee for assets: the linked resource is revealed privately to a
        // DIFFERENT participant in session A. This participant, even in session A, is denied (403) — they
        // never see (or download) content revealed only to another participant (threat T5).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithPrivatelyRevealedAssetAsync(factory, subject, revealToCaller: false);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Participant_download_without_a_session_id_is_400()
    {
        // A participant download is session-scoped (CORE-SVIS-003), so the participant MUST name the session.
        // The caller is a known member of the asset's workspace, so omitting it is a request-shape 400 (not a
        // 404), surfaced only after the membership gate.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Participant_with_a_malformed_session_id_is_400()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/{seed.AssetId}/download-url?organizationSlug={_orgA}&sessionId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Participant_role_member_with_no_participant_record_is_403()
    {
        // A Participant-role workspace member who has NO participant record in the asset's workspace holds no
        // per-participant visibility to evaluate, so the session-scoped download is denied fail-closed (403)
        // even with a valid session and a visible link.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(
            factory, subject, VisibilityState.Visible, createParticipantRecord: false);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Removed_participant_is_403_even_in_the_revealed_session()
    {
        // A soft-removed participant holds no standing (ParticipantStatus.Removed), so its session-scoped
        // download is denied fail-closed (403) even where the linked resource is revealed.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(
            factory, subject, VisibilityState.Visible, removedParticipant: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA, seed.RevealedSessionId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Host_is_unaffected_by_the_session_scope_and_downloads_without_a_session_id()
    {
        // Host/role-level access is UNCHANGED: a Host downloads the same asset (linked to a resource revealed
        // only in session A) with NO sessionId at all — host content access is session-agnostic (200).
        await using var factory = new FakeStorageApiFactory();
        const string participantSubject = "participant-a";
        const string hostSubject = "host-a";
        var seed = await SeedParticipantWithLinkedAssetAsync(
            factory, participantSubject, VisibilityState.Visible, additionalHostSubject: hostSubject);

        using var client = factory.CreateClientFor(hostSubject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Participant_download_for_an_asset_in_another_tenant_is_404()
    {
        // foreign-tenant 404: the tenant/asset boundary is enforced before the participant/session logic, so
        // a participant addressing an org-B asset with organizationSlug = A is hidden as 404 — never a 403 or
        // a participant-specific code that could leak the asset's existence.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        SeedResult seedB = default;
        Guid sessionInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var creatorInB = await db.AddUserAsync(_issuer, "creator-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Participant);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, MembershipRole.Host);
            var assetInB = await db.AddAssetAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, available: true);
            var sessionB = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B Session", SessionStatus.Live);
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, assetInB.Id);
            sessionInB = sessionB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seedB.AssetId, _orgA, sessionInB);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_404_for_an_org_member_who_is_not_a_member_of_the_assets_workspace()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "outsider-a";
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, insider.Id, available: true);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_404_for_an_asset_in_another_tenant()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "user-a";
        SeedResult seedB = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var creatorInB = await db.AddUserAsync(_issuer, "creator-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, MembershipRole.Host);
            var assetInB = await db.AddAssetAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, available: true);
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, assetInB.Id);
        });

        // Address the org-B asset with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seedB.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_404_for_an_unknown_asset()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_404_for_a_malformed_asset_id()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/assets/not-a-guid/download-url?organizationSlug=" + _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_400_without_the_organization_slug()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, organizationSlug: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_409_for_a_pending_asset()
    {
        // A still-pending asset's object is not yet confirmed in storage, so it is not downloadable. The
        // authorized viewer learns this as a 409 (an out-of-state request); no URL is produced.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: false);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Download_url_is_503_when_storage_is_unconfigured()
    {
        // No fake adapter: the unmodified app keeps the production fail-closed UnconfiguredAssetStorage, so
        // even an authorized request for an available asset yields no URL (private-by-default; threat T4).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceWithAssetAsync(factory, subject, MembershipRole.Host, available: true);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetAsync(client, seed.AssetId, _orgA);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Guid assetId, string? organizationSlug)
    {
        var url = $"/api/v1/assets/{assetId}/download-url";
        if (organizationSlug is not null)
        {
            url += $"?organizationSlug={Uri.EscapeDataString(organizationSlug)}";
        }

        return await client.GetAsync(url);
    }

    /// <summary>Issues the download request with both the organization slug and the session scope (CORE-SVIS-003).</summary>
    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, Guid assetId, string organizationSlug, Guid sessionId)
    {
        var url = $"/api/v1/assets/{assetId}/download-url"
            + $"?organizationSlug={Uri.EscapeDataString(organizationSlug)}"
            + $"&sessionId={sessionId}";
        return await client.GetAsync(url);
    }

    /// <summary>
    /// Seeds an org + a caller with the given role in both the org and a workspace (all in org A) plus an
    /// asset in that workspace (created by the caller; the creator does not affect download authorization).
    /// The asset is Available unless <paramref name="available"/> is <see langword="false"/>.
    /// </summary>
    private static async Task<SeedResult> SeedWorkspaceWithAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role,
        bool available)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: available);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });
        return seed;
    }

    /// <summary>
    /// Seeds an org + a caller with the given (audience) role plus an Available asset that is LINKED to a
    /// content block whose audience visibility is <paramref name="visibility"/>. Used to exercise the
    /// CORE-AST-005 audience-download path: an audience role may download only when the linked content is
    /// visible.
    /// </summary>
    private static async Task<SeedResult> SeedWorkspaceWithLinkedAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role,
        VisibilityState visibility)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            await db.AddVisibilityRuleAsync(org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, block.Id, visibility);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });
        return seed;
    }

    /// <summary>
    /// Seeds (in org A) a caller with the Observer role in the org and a workspace, an AVAILABLE asset
    /// (LINKED to a content block unless <paramref name="linkAsset"/> is false), and TWO sibling sessions of
    /// that workspace. The content block is revealed (visible or hidden, per <paramref name="visibility"/>)
    /// audience-wide in the REVEALED session only; the SIBLING session carries no rule for it. Used to
    /// exercise the CORE-SVIS-004 session-scoped role-level (Observer) download path: an Observer may download
    /// only when a linked target is audience-wide visible IN THE SUPPLIED session.
    /// </summary>
    private static async Task<ParticipantSeedResult> SeedObserverWithLinkedAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        VisibilityState visibility,
        bool linkAsset = true)
    {
        ParticipantSeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Observer);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Observer);

            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            if (linkAsset)
            {
                await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            }

            var revealedSession = await db.AddSessionAsync(org.Id, workspace.Id, "Revealed Session", SessionStatus.Live);
            var siblingSession = await db.AddSessionAsync(org.Id, workspace.Id, "Sibling Session", SessionStatus.Live);
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, revealedSession.Id, VisibilityResourceType.ContentBlock, block.Id, visibility);

            seed = new ParticipantSeedResult(org.Id, workspace.Id, asset.Id, revealedSession.Id, siblingSession.Id);
        });
        return seed;
    }

    /// <summary>
    /// Seeds (in org A) a caller with the Participant role in the org and a workspace, an AVAILABLE asset
    /// LINKED to a content block, and TWO sibling sessions of that workspace. The content block is revealed
    /// (visible or hidden, per <paramref name="visibility"/>) audience-wide in the REVEALED session only; the
    /// SIBLING session carries no rule for it. By default the caller also has an active participant record in
    /// the workspace; set <paramref name="createParticipantRecord"/> false to omit it (a participant-role
    /// member with no participant record), or <paramref name="removedParticipant"/> true to soft-remove it.
    /// An optional <paramref name="additionalHostSubject"/> is also added as a Host of the workspace, to prove
    /// host access is session-agnostic.
    /// </summary>
    private static async Task<ParticipantSeedResult> SeedParticipantWithLinkedAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        VisibilityState visibility,
        bool createParticipantRecord = true,
        bool removedParticipant = false,
        string? additionalHostSubject = null)
    {
        ParticipantSeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);

            if (createParticipantRecord)
            {
                await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, removed: removedParticipant);
            }

            if (additionalHostSubject is not null)
            {
                var host = await db.AddUserAsync(_issuer, additionalHostSubject);
                await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
                await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            }

            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);

            var revealedSession = await db.AddSessionAsync(org.Id, workspace.Id, "Revealed Session", SessionStatus.Live);
            var siblingSession = await db.AddSessionAsync(org.Id, workspace.Id, "Sibling Session", SessionStatus.Live);
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, revealedSession.Id, VisibilityResourceType.ContentBlock, block.Id, visibility);

            seed = new ParticipantSeedResult(org.Id, workspace.Id, asset.Id, revealedSession.Id, siblingSession.Id);
        });
        return seed;
    }

    /// <summary>
    /// Like <see cref="SeedParticipantWithLinkedAssetAsync"/>, but the linked content block is revealed
    /// PRIVATELY (a selected-participant reveal) in the revealed session — to the caller's own participant
    /// when <paramref name="revealToCaller"/> is true, otherwise to a DIFFERENT participant in the same
    /// workspace. Used to prove the per-participant primitive authorizes the download (the selected
    /// participant may, a different one may not).
    /// </summary>
    private static async Task<ParticipantSeedResult> SeedParticipantWithPrivatelyRevealedAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        bool revealToCaller)
    {
        ParticipantSeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, displayName: "P");

            // Another participant the reveal may be scoped to instead.
            var otherUser = await db.AddUserAsync(_issuer, $"{subject}-other");
            var otherParticipant = await db.AddParticipantAsync(org.Id, workspace.Id, otherUser.Id, displayName: "Q");

            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);

            var revealedSession = await db.AddSessionAsync(org.Id, workspace.Id, "Revealed Session", SessionStatus.Live);
            var siblingSession = await db.AddSessionAsync(org.Id, workspace.Id, "Sibling Session", SessionStatus.Live);

            var target = revealToCaller ? participant.Id : otherParticipant.Id;
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, revealedSession.Id, VisibilityResourceType.ContentBlock, block.Id, target, VisibilityState.Visible);

            seed = new ParticipantSeedResult(org.Id, workspace.Id, asset.Id, revealedSession.Id, siblingSession.Id);
        });
        return seed;
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid AssetId);

    private readonly record struct ParticipantSeedResult(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid AssetId,
        Guid RevealedSessionId,
        Guid SiblingSessionId);

    private sealed record DownloadDto(
        Guid AssetId,
        string Status,
        string ContentType,
        string DownloadUrl,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a conforming fake <see cref="IAssetStorage"/>
    /// for the production fail-closed default, so the download happy path can mint a signed URL without a
    /// real, deployment-supplied S3-compatible adapter. Only the storage signer seam is swapped; every other
    /// production behavior (auth, tenant resolution, the asset repository, persistence) runs unchanged.
    /// </summary>
    private sealed class FakeStorageApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAssetStorage>();
                services.AddSingleton<IAssetStorage, FakeSignedUrlAssetStorage>();
            });
        }
    }

    /// <summary>
    /// A minimal conforming <see cref="IAssetStorage"/> standing in for the deployment-supplied
    /// S3-compatible adapter: it mints a short-lived signed URL for the asset's own coordinates and can never
    /// produce a public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that unrepresentable.
    /// Not a production signer.
    /// </summary>
    private sealed class FakeSignedUrlAssetStorage : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(15);

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=put&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Upload, TestData.SeedTime, _lifetime));
        }

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=get&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Download, TestData.SeedTime, _lifetime));
        }

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            // Not exercised by the download flow; the cleanup job (CORE-AST-006) drives deletion.
            ArgumentNullException.ThrowIfNull(asset);
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
