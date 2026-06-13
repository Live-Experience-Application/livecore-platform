using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the participant-visible feed endpoint
/// (route skeleton CORE-SES-005, real projection CORE-API-005,
/// <c>GET /api/v1/participants/{participantId}/visible-feed</c>).
/// They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -> tenant
/// context resolver -> endpoint -> inline object-level authorization -> visibility
/// projection) is exercised end-to-end exactly as in production.
///
/// Three families of tests, per the story's required coverage:
/// <list type="bullet">
///   <item>VISIBLE SET (CORE-API-005): an authorized own-feed read returns exactly the
///   participant's actually-visible resources — an audience-wide reveal AND a private
///   reveal scoped to them, but never a Hidden or unruled resource nor a foreign
///   workspace's resource; the CROWN-JEWEL is that a participant does NOT see a
///   resource revealed only to ANOTHER participant, while the selected participant
///   DOES; a Host preview returns the previewed participant's own (private) visible
///   set.</item>
///   <item>LIFECYCLE: a participant who OWNS the feed (user-linked to the caller)
///   gets 200 with the participant-safe envelope (ParticipantId/WorkspaceId present,
///   Items the visible set, a GeneratedAt timestamp, and no hidden/content fields);
///   a Host and a CoHost of the participant's workspace get 200 (preview); a
///   REMOVED participant is hidden as 404 (a removed participant holds no
///   standing).</item>
///   <item>AUTHORIZATION + ISOLATION (every denial 404-hidden, never 403, because
///   the feed is private): 401 unauthenticated; 400 missing organizationSlug; 404
///   malformed/empty participant id; an Owner/Admin who is neither the
///   participant-owner nor a Host/CoHost of the participant's workspace -> 404; an
///   Observer/Auditor -> 404; a DIFFERENT participant -> 404; a Host of a sibling
///   workspace in the SAME org -> 404 (cross-workspace within tenant); a
///   participant in another tenant addressed with this org's slug -> 404
///   (cross-tenant); a token org-claim mismatch -> 404; an unknown participant ->
///   404.</item>
/// </list>
///
/// The matrix row "View own visible feed" (csv/authorization_matrix.csv) is
/// non-linear — host:preview, cohost:preview, participant:yes, everyone else no —
/// so the allow/deny cases are explicit, never an ordering comparison. Every
/// assertion checks the SPECIFIC status code, and every 200 asserts the empty-feed
/// shape, so a wrong-status pass that also leaked content is caught.
/// </summary>
public sealed class ParticipantVisibleFeedEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // 401 / 400 / 404-on-malformed — the request-shape gates before any lookup.
    // =====================================================================

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/participants/{Guid.CreateVersion7()}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_or_empty_participant_id_is_404(string participantId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // OWN FEED (200) — the participant user-linked to the caller reads its own
    // feed; the body is the participant-safe EMPTY envelope.
    // =====================================================================

    [Fact]
    public async Task Participant_can_read_own_visible_feed()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            // The caller is an org member (a participant-audience role) of the
            // tenant; own-feed access is ownership-based, not role-based.
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertEmptyFeedAsync(response, participantId, workspaceId);
    }

    [Fact]
    public async Task Own_feed_is_authorized_even_when_caller_holds_no_workspace_membership()
    {
        // Own-feed is purely ownership-based: the caller owns the participant but is
        // NOT a member of its workspace at all (only an org member). 200, because
        // the matrix grants the participant "yes" on its OWN feed independent of any
        // workspace role.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // Deliberately NO workspace membership for the caller.
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertEmptyFeedAsync(response, participantId, workspaceId);
    }

    // =====================================================================
    // PREVIEW (200) — a Host and a CoHost of the participant's workspace may
    // preview the feed of a DIFFERENT (or anonymous) participant.
    // =====================================================================

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_or_cohost_can_preview_a_participants_feed(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"previewer-{role}";
        Guid participantId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var previewer = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-user");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, previewer.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is a Host/CoHost of the PARTICIPANT'S workspace.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, previewer.Id, role);
            // The participant belongs to a DIFFERENT user, not the previewer.
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, other.Id);
            participantId = participant.Id;
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertEmptyFeedAsync(response, participantId, workspaceId);
    }

    [Fact]
    public async Task Host_can_preview_an_anonymous_participants_feed()
    {
        // An anonymous participant (no user link) has no own-feed, but a Host of its
        // workspace may still preview it. 200 with the empty envelope.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid participantId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            // Anonymous participant: no user link.
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: null);
            participantId = participant.Id;
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertEmptyFeedAsync(response, participantId, workspaceId);
    }

    // =====================================================================
    // DENIED 404-hide — genuine negative authorization tests. The feed is private,
    // so every refusal is a hidden 404, never 403.
    // =====================================================================

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Owner_or_admin_who_is_not_the_participant_owner_or_host_is_404(MembershipRole role)
    {
        // The matrix grants Owner/Admin "no" on the participant feed. The caller is
        // an org Owner/Admin AND a workspace member of the participant's workspace,
        // but is neither the participant-owner nor a Host/CoHost: hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"admin-{role}";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-user");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is even a member of the workspace in the Owner/Admin role.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, caller.Id, role);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, other.Id);
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Observer_or_auditor_is_404(MembershipRole role)
    {
        // The matrix grants Observer/Auditor "no" on the participant feed. Even as a
        // workspace member, they may not read another participant's feed: 404.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"audience-{role}";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-user");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, caller.Id, role);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, other.Id);
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_different_participant_cannot_read_another_participants_feed()
    {
        // The caller owns participant Q and requests participant P's feed. The caller
        // is NOT P's owner and not a Host/CoHost of P's workspace: hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-q-owner";
        Guid participantPId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var callerQ = await db.AddUserAsync(_issuer, subject);
            var ownerP = await db.AddUserAsync(_issuer, "participant-p-owner");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, callerQ.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller has their OWN participant Q in the same workspace.
            await db.AddParticipantAsync(org.Id, workspace.Id, callerQ.Id, displayName: "Q");
            // Participant P belongs to a different user.
            var participantP = await db.AddParticipantAsync(org.Id, workspace.Id, ownerP.Id, displayName: "P");
            participantPId = participantP.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantPId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Host_of_a_different_workspace_in_the_same_org_is_404()
    {
        // T1/T5 cross-workspace WITHIN one tenant: the caller is a Host of workspace
        // X in org A, but the participant lives in a sibling workspace Y of the SAME
        // org. Host/CoHost preview is checked against the PARTICIPANT'S workspace, so
        // a Host role in X never confers preview standing in Y: hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid participantInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-user");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, host.Id, MembershipRole.Host);
            // A sibling workspace; the caller is NOT a member of it.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var participant = await db.AddParticipantAsync(org.Id, workspaceY.Id, other.Id);
            participantInYId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantInYId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Participant_in_another_tenant_addressed_with_this_orgs_slug_is_404()
    {
        // T5 cross-tenant: a real participant in org B (owned by a caller who is a
        // Host of its workspace), addressed with organizationSlug = A (the caller's
        // own org). The cross-tenant id is hidden as 404 at the org-scoped load.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid participantInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            // The participant is owned by the caller, in org B's workspace.
            var participant = await db.AddParticipantAsync(orgB.Id, workspaceInB.Id, user.Id);
            participantInBId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantInBId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Caller_not_entitled_to_the_target_org_is_404()
    {
        // T5: the caller is a member of org B only; addressing a participant under
        // org A is hidden as 404 at the org boundary, before any participant lookup.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/participants/{Guid.CreateVersion7()}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Token_org_claim_mismatch_is_404()
    {
        // T5: the caller IS the participant-owner in org A, but the token only
        // asserts org B. The claim mismatch denies before any membership/participant
        // is consulted; the feed is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
        });

        // Token asserts only org B, not the targeted org A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_participant_in_the_callers_org_is_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{Guid.CreateVersion7()}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_participant_feed_is_404_even_for_its_owner()
    {
        // A removed participant holds no standing (ParticipantStatus.Removed), so its
        // feed is not served even to the user who owns it: hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, removed: true);
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_removed_participant_feed_is_404_for_a_host_preview()
    {
        // The Host-preview twin of the removed-participant case: a Host of the
        // participant's workspace cannot preview a removed participant's feed either.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-user");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, other.Id, removed: true);
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // THE REAL VISIBLE SET (CORE-API-005) — the feed returns the participant's
    // actually-visible resources, computed through the participant-aware
    // VisibilityPreviewService / VisibilityPolicy. Includes the crown-jewel:
    // a participant does NOT see another participant's private reveal.
    // =====================================================================

    [Fact]
    public async Task Own_feed_returns_audience_wide_and_own_private_reveals_but_not_hidden_or_unruled()
    {
        // A participant's own feed is exactly what is visible to THEM: an
        // audience-wide visible resource AND a resource revealed privately to them,
        // but NOT a Hidden resource and NOT a resource that carries no rule at all.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid audienceWideSceneId = Guid.Empty;
        Guid privateToMeContentId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;

            // Visible to the whole audience -> in the feed.
            audienceWideSceneId = Guid.CreateVersion7();
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Scene, audienceWideSceneId, VisibilityState.Visible);

            // Revealed privately to THIS participant -> in the feed.
            privateToMeContentId = Guid.CreateVersion7();
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.ContentBlock, privateToMeContentId,
                participant.Id, VisibilityState.Visible);

            // Hidden from the audience -> NOT in the feed (carries a rule, but Hidden).
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Entity, Guid.CreateVersion7(), VisibilityState.Hidden);

            // A second workspace with an audience-wide visible scene -> NOT in this
            // participant's feed (a foreign workspace never contributes; threat T5).
            var otherWorkspace = await db.AddWorkspaceAsync(org.Id, "other-show", "Other Show");
            await db.AddVisibilityRuleAsync(
                org.Id, otherWorkspace.Id, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertFeedContainsExactlyAsync(
            response,
            participantId,
            workspaceId,
            ("Scene", audienceWideSceneId),
            ("ContentBlock", privateToMeContentId));
    }

    [Fact]
    public async Task Participant_does_not_see_another_participants_private_reveal()
    {
        // CROWN-JEWEL. A resource is revealed privately to participant Q. Participant
        // P (a different participant in the same workspace) requests their OWN feed:
        // they are authorized to read it, but the privately-revealed resource is NOT
        // in it — a participant never sees content revealed only to another
        // participant (the selected-participant guarantee; threat T5).
        await using var factory = new WorkspaceApiFactory();
        const string subjectP = "participant-p-owner";
        Guid participantPId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid privateToQResourceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var ownerP = await db.AddUserAsync(_issuer, subjectP);
            var ownerQ = await db.AddUserAsync(_issuer, "participant-q-owner");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, ownerP.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var participantP = await db.AddParticipantAsync(org.Id, workspace.Id, ownerP.Id, displayName: "P");
            participantPId = participantP.Id;
            var participantQ = await db.AddParticipantAsync(org.Id, workspace.Id, ownerQ.Id, displayName: "Q");

            // The resource is revealed ONLY to Q.
            privateToQResourceId = Guid.CreateVersion7();
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Scene, privateToQResourceId,
                participantQ.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subjectP, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantPId}/visible-feed?organizationSlug={_orgA}");

        // P is authorized (own feed) but sees NOTHING — the reveal belongs to Q.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertFeedContainsExactlyAsync(response, participantPId, workspaceId);
    }

    [Fact]
    public async Task The_selected_participant_does_see_their_own_private_reveal()
    {
        // The other half of the crown-jewel: the SELECTED participant Q DOES see the
        // resource revealed privately to them, so the exclusion above is a real
        // visibility decision and not a blanket "private reveals are never shown".
        await using var factory = new WorkspaceApiFactory();
        const string subjectQ = "participant-q-owner";
        Guid participantQId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid privateToQResourceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var ownerQ = await db.AddUserAsync(_issuer, subjectQ);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, ownerQ.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var participantQ = await db.AddParticipantAsync(org.Id, workspace.Id, ownerQ.Id, displayName: "Q");
            participantQId = participantQ.Id;

            privateToQResourceId = Guid.CreateVersion7();
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Scene, privateToQResourceId,
                participantQ.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subjectQ, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantQId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertFeedContainsExactlyAsync(
            response, participantQId, workspaceId, ("Scene", privateToQResourceId));
    }

    [Fact]
    public async Task Host_preview_returns_the_previewed_participants_private_visible_set()
    {
        // A Host previewing participant Q's feed sees exactly what Q sees — including
        // the resource revealed privately to Q. Preview-as-participant is computed for
        // the TARGET participant, not the caller, so the same selected-participant
        // visibility applies.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid participantQId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid privateToQResourceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, subject);
            var ownerQ = await db.AddUserAsync(_issuer, "participant-q-owner");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            var participantQ = await db.AddParticipantAsync(org.Id, workspace.Id, ownerQ.Id, displayName: "Q");
            participantQId = participantQ.Id;

            privateToQResourceId = Guid.CreateVersion7();
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Entity, privateToQResourceId,
                participantQ.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantQId}/visible-feed?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertFeedContainsExactlyAsync(
            response, participantQId, workspaceId, ("Entity", privateToQResourceId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Asserts a 200 body is the participant-safe EMPTY feed envelope: the addressed
    /// participant id and workspace id are echoed, the item list is present and
    /// empty, a server GeneratedAt timestamp is set, and the raw JSON carries ONLY
    /// the documented envelope fields (no hidden/content/rationale fields, threats
    /// T2/T7).
    /// </summary>
    private static async Task AssertEmptyFeedAsync(
        HttpResponseMessage response,
        Guid expectedParticipantId,
        Guid expectedWorkspaceId)
    {
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);
        Assert.Equal(expectedParticipantId, feed.ParticipantId);
        Assert.Equal(expectedWorkspaceId, feed.WorkspaceId);
        Assert.NotNull(feed.Items);
        Assert.Empty(feed.Items);
        Assert.NotEqual(default, feed.GeneratedAt);

        // The envelope must expose ONLY participant-safe fields. Parse the raw JSON
        // and assert its top-level property set is exactly the documented envelope,
        // so a future hidden/content field cannot slip in unnoticed (docs/08 DTO
        // rules; threats T2/T7).
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                nameof(FeedDto.ParticipantId),
                nameof(FeedDto.WorkspaceId),
                nameof(FeedDto.Items),
                nameof(FeedDto.GeneratedAt),
            },
            properties);
    }

    /// <summary>
    /// Asserts a 200 body is the participant-safe feed envelope whose items are
    /// EXACTLY the expected set of visible resources (by resource type name + id),
    /// order-independent. The envelope-level checks of <see cref="AssertEmptyFeedAsync"/>
    /// still hold (only the documented top-level fields), and EACH item is asserted to
    /// carry ONLY the participant-safe identity fields (resourceType + resourceId) — no
    /// content, payload or host-only field can slip in (docs/08 DTO rules; threats
    /// T2/T7).
    /// </summary>
    private static async Task AssertFeedContainsExactlyAsync(
        HttpResponseMessage response,
        Guid expectedParticipantId,
        Guid expectedWorkspaceId,
        params (string ResourceType, Guid ResourceId)[] expectedItems)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // Top-level envelope: only the documented participant-safe fields.
        var properties = root.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                nameof(FeedDto.ParticipantId),
                nameof(FeedDto.WorkspaceId),
                nameof(FeedDto.Items),
                nameof(FeedDto.GeneratedAt),
            },
            properties);

        var feed = JsonSerializer.Deserialize<FeedDto>(payload, _json);
        Assert.NotNull(feed);
        Assert.Equal(expectedParticipantId, feed.ParticipantId);
        Assert.Equal(expectedWorkspaceId, feed.WorkspaceId);
        Assert.NotEqual(default, feed.GeneratedAt);
        Assert.NotNull(feed.Items);

        var actual = new HashSet<(string, Guid)>();
        foreach (var item in feed.Items)
        {
            // Each item carries ONLY the participant-safe identity fields.
            var itemProperties = item.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "resourceType", "resourceId" },
                itemProperties);

            var resourceType = item.GetProperty("resourceType").GetString();
            Assert.NotNull(resourceType);
            var resourceId = item.GetProperty("resourceId").GetGuid();
            Assert.True(actual.Add((resourceType, resourceId)), "the feed must not contain duplicate items");
        }

        var expected = expectedItems
            .Select(item => (item.ResourceType, item.ResourceId))
            .ToHashSet();
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(
            actual.SetEquals(expected),
            $"feed items [{string.Join(", ", actual)}] did not match expected [{string.Join(", ", expected)}]");
    }

    private sealed record FeedDto(
        Guid ParticipantId,
        Guid WorkspaceId,
        IReadOnlyList<JsonElement> Items,
        DateTimeOffset GeneratedAt);
}
