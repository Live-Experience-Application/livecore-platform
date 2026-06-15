using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// THE END-TO-END COMPOSITION test (CORE-E2E-001, the "End-to-End Scenarios" epic). The 65 per-ENDPOINT
/// integration tests are strong slices, but they ARRANGE state by direct DB seeding (TestData.cs writes
/// through the aggregate factories + DbContext) and then exercise ONE endpoint, so the produce-&gt;consume
/// chain ACROSS modules over the public API — a create endpoint's output feeding a downstream
/// read/visibility/event/audit module — is never proven end-to-end. This test closes that gap: it builds a
/// realistic live-session flow through the WRITE endpoints over real HTTP (<see cref="WorkspaceApiFactory"/>:
/// test authentication scheme + EF Core SQLite, foreign keys ON), feeding each create endpoint's RESPONSE id
/// into the next call, and then asserts the READ surfaces (the participant visible feed CORE-API-005, the
/// reconnect replay CORE-RT-005 and the view-audit-log read CORE-SEC-002) are mutually consistent.
///
/// WHAT IS BUILT THROUGH THE API (every write endpoint the journey touches): organization create
/// (CORE-API-001) → workspace create (CORE-WS-003) → member invite (CORE-WS-004) → session create + start
/// (CORE-API-003 / CORE-SES-004) → scene create (CORE-SCENE-003) → content-block create (CORE-SCENE-003) →
/// participant join (CORE-PRS-001) → audience reveal, selected-participant private reveal (CORE-VIS-004/005)
/// → hide (CORE-REV-001) → session end (CORE-SES-004). Each step asserts its real HTTP status, so the whole
/// journey "succeeds over real HTTP" by construction.
///
/// WHAT IS SEEDED DIRECTLY (the minimal bootstrap, and ONLY because the public API has no write route for
/// it): the OIDC user identities; the participants' ORGANIZATION memberships (the only "add org member"
/// path is the org-create founding-Owner grant — the workspace invite is a placeholder with no redeem
/// route, CORE-WS-004); the host's WORKSPACE membership (workspace create does not grant the creator a
/// workspace membership and there is no redeem route); and the PARTICIPANT records (there is no
/// participant-create endpoint — only join/leave presence over an existing participant, CORE-PRS-001). The
/// host's organization Owner membership is NOT seeded — it is produced by the org-create endpoint itself.
/// All fixtures are generic Core vocabulary (AGENTS.md).
///
/// THE SCENARIO. One workspace runs TWO concurrent live sessions. In session 1 the host reveals a SCENE to
/// the whole audience (it stays visible), privately reveals a CONTENT BLOCK to participant A only, and
/// reveals then HIDES a second content block. Two participants (A, B) join session 1; a third (C) joins the
/// concurrent session 2. The assertions then prove the modules compose:
/// <list type="bullet">
///   <item>EACH PARTICIPANT'S FEED reflects exactly the per-participant visibility — A sees the audience
///   scene AND its private content block; B sees only the audience scene (never A's private reveal, never
///   the hidden block); C in the concurrent session sees nothing from session 1.</item>
///   <item>THE EVENT STREAM is ordered and complete — the host replay is gap-free and strictly
///   sequence-ordered from SessionStarted to SessionEnded with every emitted event present; A's replay
///   carries the private ContentRevealed and B's does not.</item>
///   <item>THE AUDIT TRAIL records the causal chain — every reveal/hide is a VisibilityRuleChanged fact with
///   the right resource, target and before/after state, attributed to the host who acted, and the audit read
///   matches the actions the replay shows.</item>
/// </list>
/// </summary>
public sealed class GoldenPathSessionJourneyEndpointTests
{
    private const string _issuer = TestAuthenticationHandler.DefaultIssuer;
    private const string _orgSlug = "northwind-labs";
    private const string _foreignOrgSlug = "acme-co";

    private const string _hostSubject = "host-user";
    private const string _participantASubject = "participant-a";
    private const string _participantBSubject = "participant-b";
    private const string _concurrentParticipantSubject = "participant-c";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // The single end-to-end test: the journey succeeds and each feed reflects
    // exactly the per-participant visibility.
    // =====================================================================

    [Fact]
    public async Task The_full_journey_succeeds_over_real_http_and_each_feed_reflects_per_participant_visibility()
    {
        await using var factory = new WorkspaceApiFactory();

        // Building the journey asserts the real HTTP status of every write step, so reaching here proves the
        // full produce->consume chain succeeded over real HTTP through the public API.
        var journey = await RunGoldenPathJourneyAsync(factory);

        // Participant A (a session-1 participant) sees EXACTLY the audience scene and the content block
        // revealed privately to them — and not the block that was hidden.
        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgSlug);
        var feedA = await GetFeedAsync(participantAClient, journey.ParticipantAId, journey.SessionId);
        Assert.Contains(feedA, item => item.ResourceType == "Scene" && item.ResourceId == journey.SceneId);
        Assert.Contains(feedA, item => item.ResourceType == "ContentBlock" && item.ResourceId == journey.PrivateContentBlockId);
        Assert.DoesNotContain(feedA, item => item.ResourceId == journey.HiddenContentBlockId);
        Assert.Equal(2, feedA.Count);

        // Participant B (the other session-1 participant) sees ONLY the audience scene — never A's private
        // reveal, never the hidden block.
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgSlug);
        var feedB = await GetFeedAsync(participantBClient, journey.ParticipantBId, journey.SessionId);
        var onlyItemB = Assert.Single(feedB);
        Assert.Equal("Scene", onlyItemB.ResourceType);
        Assert.Equal(journey.SceneId, onlyItemB.ResourceId);

        // Participant C, connected to the concurrent session 2, sees nothing from session 1: a reveal is
        // session-scoped and never leaks into a concurrent session of the same workspace.
        using var participantCClient = factory.CreateClientFor(_concurrentParticipantSubject, _issuer, _orgSlug);
        var feedC = await GetFeedAsync(participantCClient, journey.ConcurrentParticipantId, journey.ConcurrentSessionId);
        Assert.Empty(feedC);
    }

    // =====================================================================
    // A private reveal is seen only by its target participant.
    // =====================================================================

    [Fact]
    public async Task A_private_reveal_is_seen_only_by_its_target_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunGoldenPathJourneyAsync(factory);

        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgSlug);
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgSlug);

        // FEED: the privately-revealed content block is in A's feed and absent from B's.
        var feedA = await GetFeedAsync(participantAClient, journey.ParticipantAId, journey.SessionId);
        var feedB = await GetFeedAsync(participantBClient, journey.ParticipantBId, journey.SessionId);
        Assert.Contains(feedA, item => item.ResourceId == journey.PrivateContentBlockId);
        Assert.DoesNotContain(feedB, item => item.ResourceId == journey.PrivateContentBlockId);

        // REPLAY (the realtime half): A's session replay carries the private ContentRevealed for the block;
        // B's replay never references it. Replay re-runs the live recipient gate, so this proves the realtime
        // delivery recipient set is bounded to the target participant (threats T2/T3).
        var replayA = await ReplayAsParticipantAsync(participantAClient, journey.SessionId, journey.ParticipantAId);
        var replayB = await ReplayAsParticipantAsync(participantBClient, journey.SessionId, journey.ParticipantBId);

        Assert.Contains(
            replayA.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, journey.PrivateContentBlockId));
        Assert.DoesNotContain(replayB.Events, e => ReferencesResource(e, journey.PrivateContentBlockId));
    }

    // =====================================================================
    // An audience reveal is seen by all of the session's participants but not a
    // concurrent session.
    // =====================================================================

    [Fact]
    public async Task An_audience_reveal_is_seen_by_all_participants_but_not_a_concurrent_session()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunGoldenPathJourneyAsync(factory);

        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgSlug);
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgSlug);
        using var participantCClient = factory.CreateClientFor(_concurrentParticipantSubject, _issuer, _orgSlug);

        // Both session-1 participants see the audience-revealed scene.
        var feedA = await GetFeedAsync(participantAClient, journey.ParticipantAId, journey.SessionId);
        var feedB = await GetFeedAsync(participantBClient, journey.ParticipantBId, journey.SessionId);
        Assert.Contains(feedA, item => item.ResourceType == "Scene" && item.ResourceId == journey.SceneId);
        Assert.Contains(feedB, item => item.ResourceType == "Scene" && item.ResourceId == journey.SceneId);

        // The participant in the concurrent session 2 does NOT see the session-1 audience reveal — not in the
        // feed and not in the replay (the reveal was appended only to session 1's stream).
        var feedC = await GetFeedAsync(participantCClient, journey.ConcurrentParticipantId, journey.ConcurrentSessionId);
        Assert.DoesNotContain(feedC, item => item.ResourceId == journey.SceneId);

        var replayC = await ReplayAsParticipantAsync(
            participantCClient, journey.ConcurrentSessionId, journey.ConcurrentParticipantId);
        Assert.DoesNotContain(replayC.Events, e => ReferencesResource(e, journey.SceneId));
    }

    // =====================================================================
    // The event replay and audit-log read match the actions taken.
    // =====================================================================

    [Fact]
    public async Task The_event_replay_and_audit_log_read_match_the_actions_taken()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunGoldenPathJourneyAsync(factory);

        // ---- The host replay is ORDERED and COMPLETE ----------------------------------------------------
        // The host is the org Owner and a workspace Host, so the host replay (no participant viewpoint) sees
        // the WHOLE session-1 stream.
        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgSlug);
        var hostReplay = await ReplayAsHostAsync(hostClient, journey.SessionId);

        // Ordered: the per-session sequence numbers are strictly increasing (gap-free, monotonic ordering).
        for (var i = 1; i < hostReplay.Events.Count; i++)
        {
            Assert.True(
                hostReplay.Events[i].Sequence > hostReplay.Events[i - 1].Sequence,
                "Replay events must be in strictly increasing sequence order.");
        }

        // Complete: the stream opens with SessionStarted and closes with SessionEnded, and every event the
        // journey emitted is present.
        Assert.Equal(SessionEventTypes.SessionStarted, hostReplay.Events[0].EventType);
        Assert.Equal(SessionEventTypes.SessionEnded, hostReplay.Events[^1].EventType);

        var hostEventTypes = hostReplay.Events.Select(e => e.EventType).ToArray();
        Assert.Equal(2, hostEventTypes.Count(t => t == SessionEventTypes.ParticipantJoined)); // A and B joined
        Assert.Equal(3, hostEventTypes.Count(t => t == SessionEventTypes.ContentRevealed)); // scene + private + hidden block
        Assert.Single(hostEventTypes, t => t == SessionEventTypes.SceneActivated); // revealing the scene IS the scene switch
        Assert.Single(hostEventTypes, t => t == SessionEventTypes.ContentHidden); // the hidden block
        // VisibilityRuleChanged fires on every real rule change: the 3 reveals + the 1 hide.
        Assert.Equal(4, hostEventTypes.Count(t => t == SessionEventTypes.VisibilityRuleChanged));

        // Each revealed/hidden resource has a corresponding host replay event.
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, journey.SceneId));
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, journey.PrivateContentBlockId));
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.ContentHidden && ReferencesResource(e, journey.HiddenContentBlockId));

        // ---- The audit-log read RECORDS THE CAUSAL CHAIN and matches the actions --------------------------
        var auditPage = await ReadAuditLogAsync(hostClient);

        // Every audit fact in this journey is attributed to the host who acted, in the host's tenant.
        Assert.NotEmpty(auditPage.Entries);
        Assert.All(auditPage.Entries, e => Assert.Equal(journey.OrganizationId, e.OrganizationId));
        Assert.All(auditPage.Entries, e => Assert.Equal(journey.HostUserProfileId, e.ActorUserProfileId));

        // The session start/end transitions are audited for session 1.
        Assert.Contains(
            auditPage.Entries,
            e => e.Action == nameof(AuditAction.SessionStarted) && e.ResourceId == journey.SessionId
                && e.PreviousState == "Prepared" && e.NewState == "Live");
        Assert.Contains(
            auditPage.Entries,
            e => e.Action == nameof(AuditAction.SessionEnded) && e.ResourceId == journey.SessionId
                && e.PreviousState == "Live" && e.NewState == "Ended");

        // The visibility changes are audited as the exact causal chain: an audience scene reveal, a
        // private content-block reveal to A, an audience content-block reveal, then its hide.
        var visibilityChanges = auditPage.Entries
            .Where(e => e.Action == nameof(AuditAction.VisibilityRuleChanged))
            .ToArray();
        Assert.Equal(4, visibilityChanges.Length);

        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "Scene" && e.ResourceId == journey.SceneId
                && e.TargetParticipantId is null && e.NewState == "Visible");
        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "ContentBlock" && e.ResourceId == journey.PrivateContentBlockId
                && e.TargetParticipantId == journey.ParticipantAId && e.NewState == "Visible");
        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "ContentBlock" && e.ResourceId == journey.HiddenContentBlockId
                && e.TargetParticipantId is null && e.NewState == "Visible");
        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "ContentBlock" && e.ResourceId == journey.HiddenContentBlockId
                && e.TargetParticipantId is null && e.PreviousState == "Visible" && e.NewState == "Hidden");

        // Replay and audit agree: every audited reveal/hide of a resource has a matching realtime event in
        // the host replay (and vice versa), so the two security surfaces describe the same actions.
        foreach (var resourceId in new[] { journey.SceneId, journey.PrivateContentBlockId, journey.HiddenContentBlockId })
        {
            Assert.Contains(visibilityChanges, e => e.ResourceId == resourceId && e.NewState == "Visible");
            Assert.Contains(
                hostReplay.Events,
                e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, resourceId));
        }
    }

    // =====================================================================
    // NEGATIVE authorization: foreign-tenant and unauthorized-role access are
    // denied, fail-closed, across the read surfaces (threats T1/T5).
    // =====================================================================

    [Fact]
    public async Task Foreign_tenant_and_unauthorized_role_are_denied_across_the_read_surfaces()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunGoldenPathJourneyAsync(factory);

        // A user who belongs ONLY to another organization addresses the journey's tenant.
        await factory.SeedAsync(async db =>
        {
            var foreignUser = await db.AddUserAsync(_issuer, "foreign-user");
            var foreignOrg = await db.AddOrganizationAsync(_foreignOrgSlug);
            await db.AddOrganizationMemberAsync(foreignOrg.Id, foreignUser.Id, MembershipRole.Owner);
        });

        using var foreignClient = factory.CreateClientFor("foreign-user", _issuer, _foreignOrgSlug);

        // Every read surface is hidden as 404 at the tenant boundary — never distinguishable from a missing
        // resource (the caller cannot even learn the participant/session/tenant exists).
        var foreignFeed = await foreignClient.GetAsync(
            $"/api/v1/participants/{journey.ParticipantAId}/visible-feed?organizationSlug={_orgSlug}&sessionId={journey.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignFeed.StatusCode);

        var foreignReplay = await foreignClient.GetAsync(
            $"/api/v1/sessions/{journey.SessionId}/events?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.NotFound, foreignReplay.StatusCode);

        var foreignAudit = await foreignClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.NotFound, foreignAudit.StatusCode);

        // A known, in-tenant member with the wrong role is denied too.
        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgSlug);
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgSlug);

        // Participant A is a Participant org member: reading the audit log is an Owner/Admin/Auditor grant, so
        // a known member without the grant is 403 (authorized to see the tenant, not the audit log).
        var participantAudit = await participantAClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.Forbidden, participantAudit.StatusCode);

        // Participant B is neither the owner of A's feed nor a Host/CoHost of the workspace, so reading A's
        // private feed is hidden as 404 (the feed is private; even an authorization refusal is a 404).
        var crossFeed = await participantBClient.GetAsync(
            $"/api/v1/participants/{journey.ParticipantAId}/visible-feed?organizationSlug={_orgSlug}&sessionId={journey.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, crossFeed.StatusCode);
    }

    // =====================================================================
    // The API-driven journey helper.
    // =====================================================================

    /// <summary>
    /// Builds the whole live-session flow THROUGH THE PUBLIC API (asserting the real HTTP status of every
    /// write step), seeding only the minimal bootstrap that has no public write route, and returns the ids
    /// the read-surface assertions consume. Each create endpoint's response id feeds the next call, so this
    /// exercises the produce-&gt;consume chain across the Organizations, Workspaces, Sessions, Scenes,
    /// Content, Participants and Visibility modules end-to-end.
    /// </summary>
    private static async Task<Journey> RunGoldenPathJourneyAsync(WorkspaceApiFactory factory)
    {
        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgSlug);

        // --- Organization + workspace, through the API ---------------------------------------------------
        // The host creates the tenant (becoming its founding Owner) and a workspace in it.
        var organization = await CreateOrganizationAsync(hostClient, _orgSlug, "Northwind Labs");
        var workspaceId = await CreateWorkspaceAsync(hostClient, _orgSlug, "summer-show", "Summer Show");

        // --- Minimal bootstrap that has NO public write route ---------------------------------------------
        // The host's WORKSPACE membership (workspace create does not grant one and there is no redeem route),
        // the participants' ORGANIZATION memberships (the only add-org-member path is the org-create
        // founding-Owner grant), and the PARTICIPANT records (there is no participant-create endpoint, only
        // join/leave over an existing participant). Everything else is built through the API above and below.
        Guid hostUserProfileId = Guid.Empty;
        Guid participantAId = Guid.Empty;
        Guid participantBId = Guid.Empty;
        Guid concurrentParticipantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // The org-create endpoint already provisioned the host's user profile (idempotent on first
            // sight); look it up so its id can anchor the host's workspace membership and the audit-actor
            // assertions.
            var hostProfile = await db.UserProfiles.SingleAsync(u => u.Issuer == _issuer && u.SubjectId == _hostSubject);
            hostUserProfileId = hostProfile.Id;
            await db.AddWorkspaceMemberAsync(organization.Id, workspaceId, hostProfile.Id, MembershipRole.Host);

            var participantAUser = await db.AddUserAsync(_issuer, _participantASubject);
            var participantBUser = await db.AddUserAsync(_issuer, _participantBSubject);
            var concurrentParticipantUser = await db.AddUserAsync(_issuer, _concurrentParticipantSubject);

            // Each participant's owner is an org member so the tenant resolves when they read their own feed.
            await db.AddOrganizationMemberAsync(organization.Id, participantAUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(organization.Id, participantBUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(organization.Id, concurrentParticipantUser.Id, MembershipRole.Participant);

            var participantA = await db.AddParticipantAsync(organization.Id, workspaceId, participantAUser.Id, "A");
            var participantB = await db.AddParticipantAsync(organization.Id, workspaceId, participantBUser.Id, "B");
            var concurrentParticipant = await db.AddParticipantAsync(
                organization.Id, workspaceId, concurrentParticipantUser.Id, "C");
            participantAId = participantA.Id;
            participantBId = participantB.Id;
            concurrentParticipantId = concurrentParticipant.Id;
        });

        // --- Member invite, through the API (the "member" write step) -------------------------------------
        // Exercises the invite endpoint and proves the one-time scoped token is returned exactly once. There
        // is no redeem route, so this does not grant a usable membership (that is why the memberships above
        // are seeded); it proves the member-invite produce step composes over real HTTP.
        await InviteMemberAsync(hostClient, workspaceId, _orgSlug, "newcomer@example.test", "Participant");

        // --- Sessions, through the API --------------------------------------------------------------------
        var sessionId = await CreateSessionAsync(hostClient, workspaceId, _orgSlug, "Opening Run");
        await StartSessionAsync(hostClient, sessionId, _orgSlug);

        // A second, CONCURRENT live session of the same workspace, with a disjoint participant.
        var concurrentSessionId = await CreateSessionAsync(hostClient, workspaceId, _orgSlug, "Parallel Run");
        await StartSessionAsync(hostClient, concurrentSessionId, _orgSlug);

        // --- Scene + content blocks, through the API ------------------------------------------------------
        var sceneId = await CreateSceneAsync(hostClient, workspaceId, _orgSlug, "Opening Scene");
        var privateContentBlockId = await CreateContentBlockAsync(
            hostClient, sceneId, _orgSlug, "Text", "A note meant for one viewer.");
        var hiddenContentBlockId = await CreateContentBlockAsync(
            hostClient, sceneId, _orgSlug, "Text", "A note shown then withdrawn.");

        // --- Participant presence, through the API --------------------------------------------------------
        await JoinAsync(hostClient, sessionId, participantAId, _orgSlug);
        await JoinAsync(hostClient, sessionId, participantBId, _orgSlug);
        await JoinAsync(hostClient, concurrentSessionId, concurrentParticipantId, _orgSlug);

        // --- Reveals / hide, through the API --------------------------------------------------------------
        // Audience reveal of the scene (stays visible -> seen by every session-1 participant).
        await RevealAsync(hostClient, sessionId, _orgSlug, "Scene", sceneId, targetParticipantId: null, "reveal-scene");
        // Private reveal of one content block to participant A only.
        await RevealAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", privateContentBlockId, participantAId, "reveal-private");
        // Audience reveal of the second content block, then hide it again.
        await RevealAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null, "reveal-hidden");
        await HideAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null, "hide-hidden");

        // --- End the session, through the API -------------------------------------------------------------
        await EndSessionAsync(hostClient, sessionId, _orgSlug);

        return new Journey(
            organization.Id,
            workspaceId,
            hostUserProfileId,
            sessionId,
            concurrentSessionId,
            sceneId,
            privateContentBlockId,
            hiddenContentBlockId,
            participantAId,
            participantBId,
            concurrentParticipantId);
    }

    // =====================================================================
    // Write-endpoint helpers (each asserts the real HTTP status).
    // =====================================================================

    private static async Task<OrganizationDto> CreateOrganizationAsync(HttpClient client, string slug, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { slug, name }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create organization");
        var body = await response.Content.ReadFromJsonAsync<OrganizationDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body;
    }

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string organizationSlug, string slug, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new { organizationSlug, slug, name }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create workspace");
        var body = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task InviteMemberAsync(
        HttpClient client, Guid workspaceId, string organizationSlug, string email, string role)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members", new { organizationSlug, email, role }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "invite member");
        var body = await response.Content.ReadFromJsonAsync<InvitationDto>(_json);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client, Guid workspaceId, string organizationSlug, string title)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions", new { organizationSlug, title }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Prepared", body.Status);
        return body.Id;
    }

    private static async Task StartSessionAsync(HttpClient client, Guid sessionId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={organizationSlug}", content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "start session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Live", body.Status);
    }

    private static async Task EndSessionAsync(HttpClient client, Guid sessionId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={organizationSlug}", content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "end session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Ended", body.Status);
    }

    private static async Task<Guid> CreateSceneAsync(HttpClient client, Guid workspaceId, string organizationSlug, string title)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes", new { organizationSlug, title }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create scene");
        var body = await response.Content.ReadFromJsonAsync<SceneDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task<Guid> CreateContentBlockAsync(
        HttpClient client, Guid sceneId, string organizationSlug, string type, string blockBody)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={organizationSlug}",
            new { type, body = blockBody },
            _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create content block");
        var body = await response.Content.ReadFromJsonAsync<ContentBlockDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task JoinAsync(HttpClient client, Guid sessionId, Guid participantId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/participants/{participantId}/join?organizationSlug={organizationSlug}",
            content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "join participant");
        var body = await response.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Joined", body.Outcome);
    }

    private static async Task RevealAsync(
        HttpClient client,
        Guid sessionId,
        string organizationSlug,
        string resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        string idempotencyKey)
    {
        // A single anonymous shape with a nullable participantId: null serializes as JSON null (an
        // audience-wide reveal), a value as the selected-participant target — so the body type is concrete
        // and unambiguous either way.
        var payload = new { organizationSlug, resourceType, resourceId, participantId = targetParticipantId };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(payload, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        await EnsureStatusAsync(response, HttpStatusCode.OK, $"reveal {resourceType}");
        var body = await response.Content.ReadFromJsonAsync<VisibilityCommandDto>(_json);
        Assert.NotNull(body);
        Assert.True(body.Visible);
    }

    private static async Task HideAsync(
        HttpClient client,
        Guid sessionId,
        string organizationSlug,
        string resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        string idempotencyKey)
    {
        var payload = new { organizationSlug, resourceType, resourceId, participantId = targetParticipantId };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/hide")
        {
            Content = JsonContent.Create(payload, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        await EnsureStatusAsync(response, HttpStatusCode.OK, $"hide {resourceType}");
        var body = await response.Content.ReadFromJsonAsync<VisibilityCommandDto>(_json);
        Assert.NotNull(body);
        Assert.False(body.Visible);
    }

    // =====================================================================
    // Read-surface helpers.
    // =====================================================================

    private static async Task<IReadOnlyList<FeedItemDto>> GetFeedAsync(HttpClient client, Guid participantId, Guid sessionId)
    {
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgSlug}&sessionId={sessionId}");
        await EnsureStatusAsync(response, HttpStatusCode.OK, "read visible feed");
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);
        return feed.Items;
    }

    private static async Task<ReplayDto> ReplayAsParticipantAsync(HttpClient client, Guid sessionId, Guid participantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgSlug}&participantId={participantId}");
        await EnsureStatusAsync(response, HttpStatusCode.OK, "replay (participant)");
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<ReplayDto> ReplayAsHostAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgSlug}");
        await EnsureStatusAsync(response, HttpStatusCode.OK, "replay (host)");
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<AuditPageDto> ReadAuditLogAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgSlug}&limit=200");
        await EnsureStatusAsync(response, HttpStatusCode.OK, "read audit log");
        var body = await response.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// Whether a replay event's server-composed payload references the given resource id. The payload carries
    /// resource IDENTIFIERS only (never content; threat T7), so a substring match on the id is sufficient and
    /// independent of the payload's property casing.
    /// </summary>
    private static bool ReferencesResource(ReplayItemDto item, Guid resourceId)
        => item.Payload.Contains(resourceId.ToString(), StringComparison.Ordinal);

    private static async Task EnsureStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Journey step '{step}' expected {expected} but got {(int)response.StatusCode}. Body: {body}");
        }
    }

    // =====================================================================
    // Captured journey context + response DTOs (only the fields the assertions read).
    // =====================================================================

    private readonly record struct Journey(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid HostUserProfileId,
        Guid SessionId,
        Guid ConcurrentSessionId,
        Guid SceneId,
        Guid PrivateContentBlockId,
        Guid HiddenContentBlockId,
        Guid ParticipantAId,
        Guid ParticipantBId,
        Guid ConcurrentParticipantId);

    private sealed record OrganizationDto(Guid Id, string Slug, string Name);

    private sealed record WorkspaceDto(Guid Id);

    private sealed record InvitationDto(Guid Id, string Token);

    private sealed record SessionDto(Guid Id, string Status);

    private sealed record SceneDto(Guid Id);

    private sealed record ContentBlockDto(Guid Id);

    private sealed record PresenceDto(Guid SessionId, Guid ParticipantId, string Outcome);

    private sealed record VisibilityCommandDto(string ResourceType, Guid ResourceId, bool Visible, string Outcome);

    private sealed record FeedDto(
        Guid ParticipantId,
        Guid WorkspaceId,
        IReadOnlyList<FeedItemDto> Items,
        DateTimeOffset GeneratedAt);

    private sealed record FeedItemDto(string ResourceType, Guid ResourceId);

    private sealed record ReplayDto(
        Guid SessionId,
        IReadOnlyList<ReplayItemDto> Events,
        DateTimeOffset GeneratedAt);

    private sealed record ReplayItemDto(
        Guid EventId,
        long Sequence,
        string EventType,
        Guid SessionId,
        string Payload,
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid? TargetParticipantId);

    private sealed record AuditPageDto(
        Guid OrganizationId,
        int Offset,
        int Limit,
        bool HasMore,
        IReadOnlyList<AuditEntryDto> Entries);

    private sealed record AuditEntryDto(
        Guid Id,
        Guid? OrganizationId,
        Guid? WorkspaceId,
        string Action,
        Guid? ActorUserProfileId,
        string? ResourceType,
        Guid? ResourceId,
        Guid? TargetParticipantId,
        string? PreviousState,
        string? NewState,
        DateTimeOffset CreatedAt);
}
