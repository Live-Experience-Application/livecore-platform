// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// THE FULL-HOUSE CORRECTNESS-AT-SCALE test (CORE-E2E-004, the "End-to-End Scenarios" epic). The existing
/// realtime/feed/replay tests are thorough but each uses a small fixed cast (one or two participants) and
/// exercises a single facet; none proves that the per-recipient visibility projection + the catch-up cursor
/// hold TOGETHER for a realistically-sized mixed-role cast. This test closes that gap: it drives ONE live
/// session with a host, a co-host, an observer, an auditor and many participants (<see cref="_participantCount"/>)
/// entirely through the public API (<see cref="WorkspaceApiFactory"/>: test authentication scheme + EF Core
/// SQLite, foreign keys ON, the REAL Visibility engine and recipient resolver in the loop), runs a sequence of
/// audience-wide, selected-participant (private) and hidden reveals, has one participant JOIN after the reveals
/// and catch up via replay (CORE-PRS-001 + the acknowledged cursor, CORE-RTC-001), and REMOVES one participant
/// mid-session (CORE-RTC-002) — then asserts that every role and every participant ends with EXACTLY the
/// correct visible feed and replayed event stream. It is correctness-at-scale, asserted deterministically as
/// EXACT visible sets, NOT a load/perf test.
///
/// WHAT IS BUILT THROUGH THE API: organization create (CORE-API-001) → workspace create (CORE-WS-003) →
/// session create + start (CORE-API-003 / CORE-SES-004) → scene create + two content-block creates
/// (CORE-SCENE-003) → participant join (CORE-PRS-001) → a long sequence of audience and private reveals plus a
/// hide (CORE-VIS-004/005, CORE-REV-001) → a late join → a mid-session removal (CORE-RTC-002). Each write step
/// asserts its real HTTP status, so the whole many-participant journey "succeeds over real HTTP" by
/// construction. The READ surfaces (the participant visible feed CORE-API-005, the reconnect replay
/// CORE-RT-005 and the view-audit-log read CORE-SEC-002) are then asserted to be mutually consistent and
/// exact.
///
/// WHAT IS SEEDED DIRECTLY (the minimal bootstrap, exactly as CORE-E2E-001 does, and ONLY because the public
/// API has no write route for it): the OIDC user identities; the cast's ORGANIZATION memberships (the only
/// add-org-member path is the org-create founding-Owner grant); the host/co-host/observer WORKSPACE
/// memberships (workspace create grants none and there is no redeem route); and the PARTICIPANT records (there
/// is no participant-create endpoint — only join/leave presence over an existing participant). Everything else
/// is built through the API. All fixtures are generic Core vocabulary (AGENTS.md).
///
/// THE CAST AND ITS EXACT-CORRECT SETS:
/// <list type="bullet">
///   <item>Every participant's feed/replay carries EXACTLY the audience reveals (the scene + the audience
///   content block) plus ONLY their own private reveal, and NEVER another participant's private reveal nor the
///   hidden content block — the per-recipient projection holds for the whole cast, not just two participants
///   (the crown jewel at scale, threat T3).</item>
///   <item>The LATE joiner, who joins after every reveal, catches up via replay to a visible set IDENTICAL to
///   an equivalent participant present from the start — and the acknowledged cursor returns only the
///   unacknowledged tail (CORE-RTC-001).</item>
///   <item>The REMOVED participant sees nothing after removal — its feed and its replay are both hidden as 404
///   (a removed participant holds no standing; CORE-RTC-002).</item>
///   <item>The HOST and the host-capable CO-HOST replay the WHOLE stream (every reveal, every private target,
///   the hide); the OBSERVER replays only the audience-visible reveals (never a private reveal, never the
///   hidden resource); the AUDITOR has no realtime feed (replay 404) but reads the audit log's full causal
///   chain — each role's role-correct set.</item>
/// </list>
/// </summary>
public sealed class FullHouseManyParticipantSessionEndpointTests
{
    private const string _issuer = TestAuthenticationHandler.DefaultIssuer;
    private const string _orgSlug = "northwind-labs";
    private const string _foreignOrgSlug = "acme-co";

    private const string _hostSubject = "host-user";
    private const string _coHostSubject = "cohost-user";
    private const string _observerSubject = "observer-user";
    private const string _auditorSubject = "auditor-user";

    // A realistically-sized participant cast. The story's example is ~25; the assertions are exact and
    // deterministic regardless of the count, so this is the single knob for the cast size.
    private const int _participantCount = 25;

    // Distinguished participants within the cast (the rest are ordinary private-reveal recipients):
    //   - the ANCHOR is present from the start and receives only the audience reveals (no private reveal), the
    //     reference the late joiner's catch-up must match;
    //   - the LATE joiner also receives only the audience reveals and joins AFTER every reveal;
    //   - the REMOVED participant receives a private reveal, is present from the start, and is removed
    //     mid-session.
    private const int _anchorIndex = 0;
    private const int _lateJoinerIndex = 1;
    private const int _removedIndex = 2;

    // Every participant from this index on (and the removed one at index 2) receives its own unique private
    // reveal; the anchor and the late joiner (indices 0 and 1) receive only the audience reveals.
    private const int _firstPrivateRevealIndex = 2;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // Required test #1 + #3: every participant ends with EXACTLY the correct visible feed and replay, and the
    // removed participant sees nothing after removal.
    // =====================================================================

    [Fact]
    public async Task Every_participant_ends_with_exactly_their_own_visible_set_and_the_removed_one_sees_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        var house = await RunFullHouseAsync(factory);

        var audienceSet = new[]
        {
            ("Scene", house.SceneId),
            ("ContentBlock", house.AudienceContentBlockId),
        };

        for (var index = 0; index < _participantCount; index++)
        {
            var participantId = house.ParticipantIds[index];
            using var client = factory.CreateClientFor(ParticipantSubject(index), _issuer, _orgSlug);

            // The removed participant holds no standing: its feed AND its replay are both hidden as 404, so it
            // sees nothing after removal (CORE-RTC-002).
            if (index == _removedIndex)
            {
                var removedFeed = await client.GetAsync(FeedUrl(participantId, house.SessionId));
                Assert.Equal(HttpStatusCode.NotFound, removedFeed.StatusCode);

                var removedReplay = await client.GetAsync(ParticipantReplayUrl(house.SessionId, participantId));
                Assert.Equal(HttpStatusCode.NotFound, removedReplay.StatusCode);
                continue;
            }

            // The participant's own private reveal (if any). The anchor and the late joiner have none.
            var ownPrivate = house.PrivateResourceIds[index];

            // FEED — exactly the audience reveals plus the participant's own private reveal, never the hidden
            // block, never anyone else's private reveal.
            var expectedFeed = ownPrivate is { } mine
                ? audienceSet.Append(("Entity", mine)).ToArray()
                : audienceSet;
            var feed = await GetFeedAsync(client, participantId, house.SessionId);
            AssertFeedEquals(feed, expectedFeed);

            // REPLAY — the same per-recipient projection through the realtime stream: the participant replays
            // the audience reveals and (if any) their own private reveal, and NEVER another participant's
            // private reveal nor the hidden block. The presence (join/leave) events are subjectless audience
            // events and are orthogonal to the resource projection, so the resource set is asserted exactly.
            var replay = await ReplayAsParticipantAsync(client, house.SessionId, participantId);
            AssertReplayResourceSet(replay, house, expectedFeed.Select(item => item.Item2));
        }
    }

    // =====================================================================
    // Required test #2: the late joiner catches up via replay to match a participant present from the start,
    // and the acknowledged cursor returns only the unacknowledged tail.
    // =====================================================================

    [Fact]
    public async Task The_late_joiner_catches_up_via_replay_to_match_a_participant_present_from_the_start()
    {
        await using var factory = new WorkspaceApiFactory();
        var house = await RunFullHouseAsync(factory);

        var anchorId = house.ParticipantIds[_anchorIndex];
        var lateId = house.ParticipantIds[_lateJoinerIndex];

        using var anchor = factory.CreateClientFor(ParticipantSubject(_anchorIndex), _issuer, _orgSlug);
        using var late = factory.CreateClientFor(ParticipantSubject(_lateJoinerIndex), _issuer, _orgSlug);

        // CATCH-UP. Both the anchor (present from the start) and the late joiner (joined after every reveal)
        // hold the SAME audience-only visibility, so their full replays — re-filtered at replay time against
        // the current rules — are IDENTICAL event-for-event. This is the proof that catch-up reconstructs the
        // correct current visible state for someone who arrived late, not a from-the-start-only guarantee.
        var anchorReplay = await ReplayAsParticipantAsync(anchor, house.SessionId, anchorId);
        var lateReplay = await ReplayAsParticipantAsync(late, house.SessionId, lateId);
        Assert.Equal(Comparable(anchorReplay), Comparable(lateReplay));

        // Their feeds are equal too, and are exactly the audience reveals.
        var anchorFeed = await GetFeedAsync(anchor, anchorId, house.SessionId);
        var lateFeed = await GetFeedAsync(late, lateId, house.SessionId);
        var audienceSet = new[]
        {
            ("Scene", house.SceneId),
            ("ContentBlock", house.AudienceContentBlockId),
        };
        AssertFeedEquals(anchorFeed, audienceSet);
        AssertFeedEquals(lateFeed, audienceSet);

        // THE ACKNOWLEDGED CURSOR (CORE-RTC-001). Acknowledging the last replayed sequence and replaying again
        // returns nothing — the late joiner is fully caught up with no missed events.
        Assert.NotEmpty(lateReplay.Events);
        var lastSequence = lateReplay.Events[^1].Sequence;
        var caughtUp = await ReplayAsParticipantAsync(late, house.SessionId, lateId, afterSequence: lastSequence);
        Assert.Empty(caughtUp.Events);

        // Acknowledging only the first replayed event returns exactly the remaining tail, in order, no skips.
        var firstSequence = lateReplay.Events[0].Sequence;
        var tail = await ReplayAsParticipantAsync(late, house.SessionId, lateId, afterSequence: firstSequence);
        Assert.Equal(
            lateReplay.Events.Skip(1).Select(item => item.EventId),
            tail.Events.Select(item => item.EventId));
    }

    // =====================================================================
    // Required test #4: host / co-host / observer / auditor each see their role-correct set.
    // =====================================================================

    [Fact]
    public async Task Host_cohost_observer_and_auditor_each_see_their_role_correct_set()
    {
        await using var factory = new WorkspaceApiFactory();
        var house = await RunFullHouseAsync(factory);

        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgSlug);
        using var coHostClient = factory.CreateClientFor(_coHostSubject, _issuer, _orgSlug);
        using var observerClient = factory.CreateClientFor(_observerSubject, _issuer, _orgSlug);
        using var auditorClient = factory.CreateClientFor(_auditorSubject, _issuer, _orgSlug);

        // ---- HOST: replays the WHOLE stream -------------------------------------------------------------
        // The host (host-capable workspace role) sees every reveal — the audience reveals, EVERY participant's
        // private reveal, and the hidden block's reveal AND hide — because the host group always receives the
        // event (host-content roles may see everything).
        var hostReplay = await ReplayAsMemberAsync(hostClient, house.SessionId);
        Assert.Contains(hostReplay.Events, e => ReferencesResource(e, house.SceneId));
        Assert.Contains(hostReplay.Events, e => ReferencesResource(e, house.AudienceContentBlockId));
        Assert.Contains(hostReplay.Events, e => ReferencesResource(e, house.HiddenContentBlockId));
        for (var index = _firstPrivateRevealIndex; index < _participantCount; index++)
        {
            var privateId = house.PrivateResourceIds[index]!.Value;
            Assert.Contains(hostReplay.Events, e => ReferencesResource(e, privateId));
        }

        // The hidden block was revealed then hidden, so the host replays BOTH its reveal and its hide.
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, house.HiddenContentBlockId));
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.ContentHidden && ReferencesResource(e, house.HiddenContentBlockId));

        // The host projection carries the "to whom" routing target on a private reveal, so a host can audit who
        // a private reveal was for (docs/09 "host receives audit/confirmation event").
        var sampleIndex = _firstPrivateRevealIndex + 1;
        var samplePrivateId = house.PrivateResourceIds[sampleIndex]!.Value;
        var samplePrivateReveal = hostReplay.Events.Single(
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, samplePrivateId));
        Assert.Equal(house.ParticipantIds[sampleIndex], samplePrivateReveal.TargetParticipantId);

        // ---- CO-HOST: host-capable, so it replays the IDENTICAL host view ------------------------------
        var coHostReplay = await ReplayAsMemberAsync(coHostClient, house.SessionId);
        Assert.Equal(Comparable(hostReplay), Comparable(coHostReplay));

        // ---- OBSERVER: only the audience-visible reveals, never a private reveal nor the hidden block -----
        var observerReplay = await ReplayAsMemberAsync(observerClient, house.SessionId);
        AssertReplayResourceSet(observerReplay, house, new[] { house.SceneId, house.AudienceContentBlockId });

        // The observer is never SHOWN the hidden block (no ContentRevealed for it on replay), but DOES receive
        // the subjectless ContentHidden "remove it" announcement — identifiers only, never content (threat T7).
        Assert.DoesNotContain(
            observerReplay.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, house.HiddenContentBlockId));
        Assert.Contains(
            observerReplay.Events,
            e => e.EventType == SessionEventTypes.ContentHidden && ReferencesResource(e, house.HiddenContentBlockId));

        // ---- AUDITOR: no realtime feed, but the audit log's full causal chain ---------------------------
        // The auditor has no realtime relationship to the session, so the reconnect replay is hidden as 404 —
        // its role-correct realtime set is empty.
        var auditorReplay = await auditorClient.GetAsync(MemberReplayUrl(house.SessionId));
        Assert.Equal(HttpStatusCode.NotFound, auditorReplay.StatusCode);

        // The auditor's role-correct set is the audit log: it reads the exact causal chain of visibility
        // changes — every reveal and the one hide — attributed to the actor in the tenant (CORE-SEC-002).
        var auditPage = await ReadAuditLogAsync(auditorClient);
        var visibilityChanges = auditPage.Entries
            .Where(e => e.Action == nameof(AuditAction.VisibilityRuleChanged))
            .ToArray();

        // 3 audience reveals (scene + audience block + hidden block) + one private reveal per recipient + the
        // single hide.
        var expectedPrivateReveals = _participantCount - _firstPrivateRevealIndex;
        Assert.Equal(3 + expectedPrivateReveals + 1, visibilityChanges.Length);

        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "Scene" && e.ResourceId == house.SceneId
                && e.TargetParticipantId is null && e.NewState == "Visible");
        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "ContentBlock" && e.ResourceId == house.HiddenContentBlockId
                && e.TargetParticipantId is null && e.PreviousState == "Visible" && e.NewState == "Hidden");
        Assert.Contains(
            visibilityChanges,
            e => e.ResourceType == "Entity" && e.ResourceId == samplePrivateId
                && e.TargetParticipantId == house.ParticipantIds[sampleIndex] && e.NewState == "Visible");
    }

    // =====================================================================
    // NEGATIVE authorization: foreign-tenant and unauthorized-role access are denied, fail-closed, across the
    // read surfaces (threats T1/T5).
    // =====================================================================

    [Fact]
    public async Task Foreign_tenant_and_unauthorized_role_are_denied_across_the_read_surfaces()
    {
        await using var factory = new WorkspaceApiFactory();
        var house = await RunFullHouseAsync(factory);

        // A user who belongs ONLY to another organization addresses this tenant's session and feeds.
        await factory.SeedAsync(async db =>
        {
            var foreignUser = await db.AddUserAsync(_issuer, "foreign-user");
            var foreignOrg = await db.AddOrganizationAsync(_foreignOrgSlug);
            await db.AddOrganizationMemberAsync(foreignOrg.Id, foreignUser.Id, MembershipRole.Owner);
        });

        using var foreignClient = factory.CreateClientFor("foreign-user", _issuer, _foreignOrgSlug);

        // Every read surface is hidden as 404 at the tenant boundary — never distinguishable from a missing
        // resource (the caller cannot even learn the participant/session/tenant exists).
        var anchorId = house.ParticipantIds[_anchorIndex];
        var foreignFeed = await foreignClient.GetAsync(
            $"/api/v1/participants/{anchorId}/visible-feed?organizationSlug={_orgSlug}&sessionId={house.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignFeed.StatusCode);

        var foreignReplay = await foreignClient.GetAsync(
            $"/api/v1/sessions/{house.SessionId}/events?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.NotFound, foreignReplay.StatusCode);

        var foreignAudit = await foreignClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.NotFound, foreignAudit.StatusCode);

        // A participant cannot read ANOTHER participant's private feed — the crown jewel as an authorization
        // refusal (hidden as 404, never 403). Index 3 reads index 4's feed.
        using var participantClient = factory.CreateClientFor(ParticipantSubject(3), _issuer, _orgSlug);
        var otherFeed = await participantClient.GetAsync(
            FeedUrl(house.ParticipantIds[4], house.SessionId));
        Assert.Equal(HttpStatusCode.NotFound, otherFeed.StatusCode);

        // A participant (audience role) is not entitled to the audit log: it is a known member of the tenant,
        // so the refusal is a 403 (authorized to see the tenant, not the audit log).
        var participantAudit = await participantClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgSlug}");
        Assert.Equal(HttpStatusCode.Forbidden, participantAudit.StatusCode);

        // An observer is neither the owner of a participant's feed nor a Host/CoHost, so reading a
        // participant's private feed is hidden as 404.
        using var observerClient = factory.CreateClientFor(_observerSubject, _issuer, _orgSlug);
        var observerFeed = await observerClient.GetAsync(FeedUrl(anchorId, house.SessionId));
        Assert.Equal(HttpStatusCode.NotFound, observerFeed.StatusCode);
    }

    // =====================================================================
    // The API-driven full-house builder.
    // =====================================================================

    /// <summary>
    /// Builds the whole many-participant live session THROUGH THE PUBLIC API (asserting the real HTTP status of
    /// every write step), seeding only the minimal bootstrap that has no public write route, and returns the
    /// ids the read-surface assertions consume. The sequence is: build the cast → start the session → create
    /// the scene and content blocks → join the present-from-start participants → run the audience, private and
    /// hidden reveals → late-join one participant → remove one participant mid-session.
    /// </summary>
    private static async Task<FullHouse> RunFullHouseAsync(WorkspaceApiFactory factory)
    {
        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgSlug);

        // --- Organization + workspace, through the API ---------------------------------------------------
        var organization = await CreateOrganizationAsync(hostClient, _orgSlug, "Northwind Labs");
        var workspaceId = await CreateWorkspaceAsync(hostClient, _orgSlug, "summer-show", "Summer Show");

        // --- Minimal bootstrap that has NO public write route --------------------------------------------
        Guid hostUserProfileId = Guid.Empty;
        var participantIds = new Guid[_participantCount];
        await factory.SeedAsync(async db =>
        {
            // The org-create endpoint already provisioned the host's user profile, and the workspace-create
            // endpoint already enrolled the host as the workspace's founding Owner (CORE-WS-009), so the host's
            // workspace membership is not seeded here; look the profile up so its id can anchor the audit-actor
            // assertions.
            var hostProfile = await db.UserProfiles.SingleAsync(u => u.Issuer == _issuer && u.SubjectId == _hostSubject);
            hostUserProfileId = hostProfile.Id;

            // The co-host and observer are workspace members (so the realtime resolver classifies them); the
            // auditor needs only an organization Auditor role (the audit log is org-scoped) and deliberately
            // holds no realtime relationship to the session.
            var coHostUser = await db.AddUserAsync(_issuer, _coHostSubject);
            await db.AddOrganizationMemberAsync(organization.Id, coHostUser.Id, MembershipRole.CoHost);
            await db.AddWorkspaceMemberAsync(organization.Id, workspaceId, coHostUser.Id, MembershipRole.CoHost);

            var observerUser = await db.AddUserAsync(_issuer, _observerSubject);
            await db.AddOrganizationMemberAsync(organization.Id, observerUser.Id, MembershipRole.Observer);
            await db.AddWorkspaceMemberAsync(organization.Id, workspaceId, observerUser.Id, MembershipRole.Observer);

            var auditorUser = await db.AddUserAsync(_issuer, _auditorSubject);
            await db.AddOrganizationMemberAsync(organization.Id, auditorUser.Id, MembershipRole.Auditor);

            // The participant cast: each is an org member (so the tenant resolves for them) who owns one active
            // participant record in the workspace.
            for (var index = 0; index < _participantCount; index++)
            {
                var participantUser = await db.AddUserAsync(_issuer, ParticipantSubject(index));
                await db.AddOrganizationMemberAsync(organization.Id, participantUser.Id, MembershipRole.Participant);
                var participant = await db.AddParticipantAsync(
                    organization.Id, workspaceId, participantUser.Id, $"Seat {index:00}");
                participantIds[index] = participant.Id;
            }
        });

        // --- Session + scene + content blocks, through the API -------------------------------------------
        var sessionId = await CreateSessionAsync(hostClient, workspaceId, _orgSlug, "Full House Run");
        await StartSessionAsync(hostClient, sessionId, _orgSlug);

        var sceneId = await CreateSceneAsync(hostClient, workspaceId, _orgSlug, "Opening Scene");
        var audienceContentBlockId = await CreateContentBlockAsync(
            hostClient, sceneId, _orgSlug, "Text", "A note for the whole audience.");
        var hiddenContentBlockId = await CreateContentBlockAsync(
            hostClient, sceneId, _orgSlug, "Text", "A note shown then withdrawn.");

        // --- Presence: the participants present from the start join first --------------------------------
        await JoinAsync(hostClient, sessionId, participantIds[_anchorIndex], _orgSlug);
        await JoinAsync(hostClient, sessionId, participantIds[_removedIndex], _orgSlug);

        // --- Reveals: audience-wide, then a private reveal per recipient, then a hide --------------------
        await RevealAsync(hostClient, sessionId, _orgSlug, "Scene", sceneId, targetParticipantId: null, "reveal-scene");
        await RevealAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", audienceContentBlockId, targetParticipantId: null,
            "reveal-audience-block");
        await RevealAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null,
            "reveal-hidden-block");

        var privateResourceIds = new Guid?[_participantCount];
        for (var index = _firstPrivateRevealIndex; index < _participantCount; index++)
        {
            // Each private reveal targets a unique generic Entity resource visible ONLY to that participant.
            var privateResourceId = Guid.CreateVersion7();
            privateResourceIds[index] = privateResourceId;
            await RevealAsync(
                hostClient, sessionId, _orgSlug, "Entity", privateResourceId, participantIds[index],
                $"reveal-private-{index}");
        }

        // Hide the previously audience-revealed block: its end state is Hidden, so no audience member or
        // participant may see it.
        await HideAsync(
            hostClient, sessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null,
            "hide-hidden-block");

        // --- Late join (after every reveal) + mid-session removal ----------------------------------------
        await JoinAsync(hostClient, sessionId, participantIds[_lateJoinerIndex], _orgSlug);
        await LeaveAsync(hostClient, sessionId, participantIds[_removedIndex], _orgSlug);

        return new FullHouse(
            organization.Id,
            workspaceId,
            sessionId,
            hostUserProfileId,
            sceneId,
            audienceContentBlockId,
            hiddenContentBlockId,
            participantIds,
            privateResourceIds);
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

    private static async Task LeaveAsync(HttpClient client, Guid sessionId, Guid participantId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/participants/{participantId}/leave?organizationSlug={organizationSlug}",
            content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "leave participant");
        var body = await response.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Left", body.Outcome);
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

    private static string FeedUrl(Guid participantId, Guid sessionId)
        => $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgSlug}&sessionId={sessionId}";

    private static string ParticipantReplayUrl(Guid sessionId, Guid participantId)
        => $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgSlug}&participantId={participantId}";

    private static string MemberReplayUrl(Guid sessionId)
        => $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgSlug}";

    private static async Task<IReadOnlyList<FeedItemDto>> GetFeedAsync(HttpClient client, Guid participantId, Guid sessionId)
    {
        var response = await client.GetAsync(FeedUrl(participantId, sessionId));
        await EnsureStatusAsync(response, HttpStatusCode.OK, "read visible feed");
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);
        return feed.Items;
    }

    private static async Task<ReplayDto> ReplayAsParticipantAsync(
        HttpClient client, Guid sessionId, Guid participantId, long? afterSequence = null)
    {
        var url = ParticipantReplayUrl(sessionId, participantId);
        if (afterSequence is { } cursor)
        {
            url += $"&afterSequence={cursor}";
        }

        var response = await client.GetAsync(url);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "replay (participant)");
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<ReplayDto> ReplayAsMemberAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync(MemberReplayUrl(sessionId));
        await EnsureStatusAsync(response, HttpStatusCode.OK, "replay (member)");
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

    // =====================================================================
    // Assertion + projection helpers.
    // =====================================================================

    private static string ParticipantSubject(int index) => $"participant-{index:00}";

    /// <summary>Asserts the feed's items are EXACTLY the expected (resourceType, resourceId) set, order-independent.</summary>
    private static void AssertFeedEquals(
        IReadOnlyList<FeedItemDto> feed,
        IReadOnlyList<(string ResourceType, Guid ResourceId)> expected)
    {
        var actual = feed.Select(item => (item.ResourceType, item.ResourceId)).ToHashSet();
        var expectedSet = expected.ToHashSet();
        Assert.True(
            actual.SetEquals(expectedSet),
            $"feed items [{string.Join(", ", actual)}] did not match expected [{string.Join(", ", expectedSet)}]");
    }

    /// <summary>
    /// Asserts the resources REVEALED TO the recipient through the replay are EXACTLY the expected set: every
    /// expected resource has a <c>ContentRevealed</c> event for it, and no other known resource (another
    /// participant's private reveal, or the hidden block) does — proving the per-recipient projection on the
    /// realtime stream. The "revealed-to-me" set is the recipient's <c>ContentRevealed</c> events: a resource
    /// that was revealed then hidden is no longer revealed to the audience on replay (its <c>ContentRevealed</c>
    /// is re-gated out), so it is correctly absent here even though the audience still receives the subjectless
    /// <c>ContentHidden</c> "remove it" announcement (the announcement carries identifiers only, never content,
    /// and is asserted separately).
    /// </summary>
    private static void AssertReplayResourceSet(ReplayDto replay, FullHouse house, IEnumerable<Guid> expectedResourceIds)
    {
        var expected = expectedResourceIds.ToHashSet();
        foreach (var resourceId in house.AllResourceIds())
        {
            var revealed = replay.Events.Any(
                e => e.EventType == SessionEventTypes.ContentRevealed && ReferencesResource(e, resourceId));
            if (expected.Contains(resourceId))
            {
                Assert.True(revealed, $"replay was expected to reveal resource {resourceId} but did not");
            }
            else
            {
                Assert.False(revealed, $"replay leaked a reveal of resource {resourceId} the recipient may not see");
            }
        }
    }

    /// <summary>
    /// Whether a replay event's server-composed payload references the given resource id. The payload carries
    /// resource IDENTIFIERS only (never content; threat T7), so a substring match on the id is sufficient and
    /// independent of the payload's property casing.
    /// </summary>
    private static bool ReferencesResource(ReplayItemDto item, Guid resourceId)
        => item.Payload.Contains(resourceId.ToString(), StringComparison.Ordinal);

    /// <summary>Projects a replay to the order-sensitive, recipient-independent shape two equal views must share.</summary>
    private static IReadOnlyList<(long Sequence, Guid EventId, string EventType, string Payload, Guid? Target)> Comparable(
        ReplayDto replay)
        => replay.Events
            .Select(item => (item.Sequence, item.EventId, item.EventType, item.Payload, item.TargetParticipantId))
            .ToList();

    private static async Task EnsureStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Full-house step '{step}' expected {expected} but got {(int)response.StatusCode}. Body: {body}");
        }
    }

    // =====================================================================
    // Captured full-house context + response DTOs (only the fields the assertions read).
    // =====================================================================

    private readonly record struct FullHouse(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid HostUserProfileId,
        Guid SceneId,
        Guid AudienceContentBlockId,
        Guid HiddenContentBlockId,
        IReadOnlyList<Guid> ParticipantIds,
        IReadOnlyList<Guid?> PrivateResourceIds)
    {
        /// <summary>Every governed resource in the scenario: the two audience reveals, the hidden block, and every private reveal.</summary>
        public IEnumerable<Guid> AllResourceIds()
        {
            yield return SceneId;
            yield return AudienceContentBlockId;
            yield return HiddenContentBlockId;
            foreach (var privateId in PrivateResourceIds)
            {
                if (privateId is { } id)
                {
                    yield return id;
                }
            }
        }
    }

    private sealed record OrganizationDto(Guid Id, string Slug, string Name);

    private sealed record WorkspaceDto(Guid Id);

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
