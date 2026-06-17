// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// THE CROWN-JEWEL integration test for session-scoped visibility (CORE-SVIS-001, the most severe
/// release-readiness finding: cross-session data leak). It drives the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys
/// ON), exercising the full request flow (authentication -> tenant context resolver -> endpoint ->
/// authorization -> session-scoped visibility) end-to-end.
///
/// The scenario is exactly the story's acceptance criterion: ONE workspace W runs TWO concurrent LIVE
/// sessions A and B with DISJOINT participants (participant pA connects to session A, pB to session B). A
/// host makes an audience-wide reveal of a resource in session A. The test then asserts the reveal is
/// bounded to session A across every surface:
/// <list type="bullet">
///   <item>VISIBLE-FEED — pA's feed scoped to session A CONTAINS the revealed resource; pB's feed scoped
///   to session B does NOT (and neither does pA's own feed scoped to session B): a reveal in one session
///   is never in a concurrent session's feed (threat T5).</item>
///   <item>REPLAY — pA replaying session A receives the <c>ContentRevealed</c> event; pB replaying their
///   own session B receives NOTHING for it. Replay re-runs the EXACT live recipient computation
///   (<see cref="SessionReplayService"/> over <see cref="ISessionEventRecipientResolver"/>), so this also
///   proves the realtime DELIVERY recipient set is bounded by the session (threat T3).</item>
///   <item>SAME-SESSION — a participant in session A still sees the reveal (the fix is a real visibility
///   decision, not a blanket denial).</item>
///   <item>FOREIGN-TENANT — a caller from another organization is still hidden as 404 at the tenant
///   boundary (threat T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionScopedVisibilityEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _hostSubject = "host-a";
    private const string _participantASubject = "participant-a";
    private const string _participantBSubject = "participant-b";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_reveal_in_one_session_is_not_in_a_concurrent_sessions_feed()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedTwoSessionScenarioAsync(factory);

        // The host reveals a Scene to the WHOLE audience of session A.
        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var reveal = await PostRevealAsync(hostClient, scenario.SessionAId, scenario.RevealedSceneId, "k-reveal");
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);

        // pA, connected to session A, SEES the reveal in their session-A feed.
        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgA);
        var feedAInSessionA = await GetFeedItemsAsync(participantAClient, scenario.ParticipantAId, scenario.SessionAId);
        Assert.Contains(feedAInSessionA, item => item.ResourceType == "Scene" && item.ResourceId == scenario.RevealedSceneId);

        // pB, connected to a DIFFERENT concurrent session B of the SAME workspace, does NOT see it.
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgA);
        var feedBInSessionB = await GetFeedItemsAsync(participantBClient, scenario.ParticipantBId, scenario.SessionBId);
        Assert.DoesNotContain(feedBInSessionB, item => item.ResourceId == scenario.RevealedSceneId);
        Assert.Empty(feedBInSessionB);

        // Even pA, querying their feed scoped to session B, sees nothing: the reveal is bounded to A.
        var feedAInSessionB = await GetFeedItemsAsync(participantAClient, scenario.ParticipantAId, scenario.SessionBId);
        Assert.DoesNotContain(feedAInSessionB, item => item.ResourceId == scenario.RevealedSceneId);
    }

    [Fact]
    public async Task A_reveal_in_one_session_is_not_in_a_concurrent_sessions_replay()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedTwoSessionScenarioAsync(factory);

        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostRevealAsync(hostClient, scenario.SessionAId, scenario.RevealedSceneId, "k-reveal")).StatusCode);

        // pA replaying their own session A receives the ContentRevealed event for the resource.
        using var participantAClient = factory.CreateClientFor(_participantASubject, _issuer, _orgA);
        var replayAInSessionA = await ReplayAsync(participantAClient, scenario.SessionAId, scenario.ParticipantAId);
        Assert.Contains(
            replayAInSessionA.Events,
            e => e.EventType == SessionEventTypes.ContentRevealed
                && e.Payload.Contains(scenario.RevealedSceneId.ToString(), StringComparison.Ordinal));

        // pB replaying their OWN concurrent session B receives NOTHING about the resource — the reveal was
        // appended only to session A's append-only stream, and replay re-runs the live recipient gate
        // bounded by the session. This is the realtime-delivery half of the crown jewel (threat T3).
        using var participantBClient = factory.CreateClientFor(_participantBSubject, _issuer, _orgA);
        var replayBInSessionB = await ReplayAsync(participantBClient, scenario.SessionBId, scenario.ParticipantBId);
        Assert.DoesNotContain(
            replayBInSessionB.Events,
            e => e.Payload.Contains(scenario.RevealedSceneId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_foreign_tenant_caller_is_hidden_as_404_on_the_session_scoped_feed()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedTwoSessionScenarioAsync(factory);

        // A user who belongs ONLY to org B addresses pA's feed under org A: hidden as 404 at the tenant
        // boundary, before any participant or session is consulted (threat T5).
        await factory.SeedAsync(async db =>
        {
            var foreignUser = await db.AddUserAsync(_issuer, "foreign-user");
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, foreignUser.Id, MembershipRole.Owner);
        });

        using var foreignClient = factory.CreateClientFor("foreign-user", _issuer, _orgB);
        var response = await foreignClient.GetAsync(
            $"/api/v1/participants/{scenario.ParticipantAId}/visible-feed?organizationSlug={_orgA}&sessionId={scenario.SessionAId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Seeds org A with workspace W running TWO concurrent LIVE sessions A and B, a workspace Host (who
    /// reveals), and two user-linked active participants: pA (its owner connects to session A) and pB (its
    /// owner connects to session B). Each participant's owner is an org member so the tenant resolves for
    /// them. The participants are disjoint (distinct rows owned by distinct users).
    /// </summary>
    private static async Task<TwoSessionScenario> SeedTwoSessionScenarioAsync(WorkspaceApiFactory factory)
    {
        TwoSessionScenario scenario = default;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, _hostSubject);
            var participantAUser = await db.AddUserAsync(_issuer, _participantASubject);
            var participantBUser = await db.AddUserAsync(_issuer, _participantBSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, participantAUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(org.Id, participantBUser.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            // Two concurrent live sessions of the SAME workspace.
            var sessionA = await db.AddSessionAsync(org.Id, workspace.Id, "Session A", SessionStatus.Live);
            var sessionB = await db.AddSessionAsync(org.Id, workspace.Id, "Session B", SessionStatus.Live);

            // Disjoint participants, one per session's audience.
            var participantA = await db.AddParticipantAsync(org.Id, workspace.Id, participantAUser.Id, displayName: "A");
            var participantB = await db.AddParticipantAsync(org.Id, workspace.Id, participantBUser.Id, displayName: "B");

            scenario = new TwoSessionScenario(
                org.Id,
                workspace.Id,
                sessionA.Id,
                sessionB.Id,
                participantA.Id,
                participantB.Id,
                Guid.CreateVersion7());
        });
        return scenario;
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        Guid sceneId,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Scene", resourceId = sceneId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<FeedItemDto>> GetFeedItemsAsync(
        HttpClient client,
        Guid participantId,
        Guid sessionId)
    {
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}&sessionId={sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);
        return feed.Items;
    }

    private static async Task<ReplayDto> ReplayAsync(HttpClient client, Guid sessionId, Guid participantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}&participantId={participantId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private readonly record struct TwoSessionScenario(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionAId,
        Guid SessionBId,
        Guid ParticipantAId,
        Guid ParticipantBId,
        Guid RevealedSceneId);

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
        string EventType,
        Guid SessionId,
        string Payload,
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid? TargetParticipantId);
}
