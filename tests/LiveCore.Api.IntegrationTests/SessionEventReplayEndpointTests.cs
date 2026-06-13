using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the reconnect-replay endpoint (CORE-RT-005,
/// <c>GET /api/v1/sessions/{sessionId}/events</c>). They drive the REAL application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), with
/// the REAL Visibility engine and the REAL recipient resolver in the loop, so the documented reconnect flow
/// (authenticate -&gt; resolve context -&gt; resolve participant identity -&gt; query events after the last
/// acknowledged id -&gt; filter each event through Visibility -&gt; project recipient-safe payloads,
/// docs/09_EVENT_CATALOG.md) runs end-to-end exactly as in production.
///
/// The durable events are produced by the real reveal command (POST .../reveal), so the replay reconstructs
/// the actual stream. Coverage is the story's required "Realtime integration tests with selected/unselected
/// participants" plus the threat-model control "reconnect replay filter" (docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>SELECTED vs UNSELECTED — the selected participant replays the private reveal AND the audience
///   reveal; the UNSELECTED participant replays ONLY the audience reveal and NEVER the other's private
///   reveal (the crown jewel, threat T3).</item>
///   <item>HOST — replays every event, the private one carrying the routing target (the host confirmation),
///   the audience one carrying none.</item>
///   <item>CURSOR — replaying after an acknowledged event id returns only the later events.</item>
///   <item>AUTHORIZATION + ISOLATION — 401 unauthenticated; 400 missing organizationSlug; 404 for a
///   malformed session id, a cross-tenant session, an org member who is not session audience, and a
///   participant trying to replay a participant feed it does NOT own (fail-closed, every denial hidden as
///   404, never 403).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventReplayEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _hostSubject = "host-a";
    private const string _p1Subject = "participant-1";
    private const string _p2Subject = "participant-2";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // Selected / unselected — the headline anti-leak proof.
    // =====================================================================

    [Fact]
    public async Task The_selected_participant_replays_the_private_reveal_and_the_audience_reveal()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var privateResource = Guid.CreateVersion7();
        var audienceResource = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToParticipantAsync(host, scenario.SessionId, privateResource, scenario.ParticipantOneId, "k-private");
        await RevealToAudienceAsync(host, scenario.SessionId, audienceResource, "k-audience");

        using var participantOne = factory.CreateClientFor(_p1Subject, _issuer, _orgA);
        var replay = await ReplayAsync(participantOne, scenario.SessionId, participantId: scenario.ParticipantOneId);

        var payloads = Payloads(replay);
        // CORE-EVT-003: each reveal now emits ContentRevealed AND the subject-gated VisibilityRuleChanged,
        // both visible to the selected participant, so they replay all four events.
        Assert.Equal(4, replay.Events.Count);
        Assert.Contains(payloads, payload => payload.Contains(privateResource.ToString(), StringComparison.Ordinal));
        Assert.Contains(payloads, payload => payload.Contains(audienceResource.ToString(), StringComparison.Ordinal));
        // A participant never learns who else a reveal targeted: the audience projection omits the target.
        Assert.All(replay.Events, item => Assert.Null(item.TargetParticipantId));
    }

    [Fact]
    public async Task The_unselected_participant_replays_only_the_audience_reveal_never_the_private_one()
    {
        // THE crown jewel: the reconnect replay re-filters every event, so an unselected participant can
        // never replay a reveal targeted at someone else (threat T3).
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var privateResource = Guid.CreateVersion7();
        var audienceResource = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToParticipantAsync(host, scenario.SessionId, privateResource, scenario.ParticipantOneId, "k-private");
        await RevealToAudienceAsync(host, scenario.SessionId, audienceResource, "k-audience");

        using var participantTwo = factory.CreateClientFor(_p2Subject, _issuer, _orgA);
        var replay = await ReplayAsync(participantTwo, scenario.SessionId, participantId: scenario.ParticipantTwoId);

        var payloads = Payloads(replay);
        // CORE-EVT-003: the unselected participant replays the audience reveal's two events (ContentRevealed
        // + VisibilityRuleChanged) but NEVER the private reveal's events — the crown jewel, threat T3.
        Assert.Equal(2, replay.Events.Count);
        Assert.All(replay.Events, item => Assert.Contains(audienceResource.ToString(), item.Payload, StringComparison.Ordinal));
        Assert.DoesNotContain(payloads, payload => payload.Contains(privateResource.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_host_replays_every_event_with_the_routing_target_on_the_private_one()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var privateResource = Guid.CreateVersion7();
        var audienceResource = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToParticipantAsync(host, scenario.SessionId, privateResource, scenario.ParticipantOneId, "k-private");
        await RevealToAudienceAsync(host, scenario.SessionId, audienceResource, "k-audience");

        var replay = await ReplayAsync(host, scenario.SessionId);

        // CORE-EVT-003: the host replays every event — each reveal's ContentRevealed and VisibilityRuleChanged.
        Assert.Equal(4, replay.Events.Count);

        // The host projection carries the "to whom" confirmation on the private reveal's ContentRevealed,
        // and none on the audience-wide reveal's (docs/09 "host receives audit/confirmation event").
        var privateRevealed = replay.Events.Single(
            item => item.EventType == SessionEventTypes.ContentRevealed
                && item.Payload.Contains(privateResource.ToString(), StringComparison.Ordinal));
        var audienceRevealed = replay.Events.Single(
            item => item.EventType == SessionEventTypes.ContentRevealed
                && item.Payload.Contains(audienceResource.ToString(), StringComparison.Ordinal));
        Assert.Equal(scenario.ParticipantOneId, privateRevealed.TargetParticipantId);
        Assert.Null(audienceRevealed.TargetParticipantId);
    }

    [Fact]
    public async Task Replaying_after_an_acknowledged_event_returns_only_the_later_events()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToAudienceAsync(host, scenario.SessionId, Guid.CreateVersion7(), "k-1");
        await RevealToAudienceAsync(host, scenario.SessionId, Guid.CreateVersion7(), "k-2");

        var full = await ReplayAsync(host, scenario.SessionId);
        // CORE-EVT-003: each audience reveal emits ContentRevealed + VisibilityRuleChanged, so two reveals
        // append four events.
        Assert.Equal(4, full.Events.Count);

        // Acknowledge the first event (whichever sorts first in append order) and replay after it: only the
        // three later events come back, in order.
        var cursor = full.Events[0].EventId;
        var tail = await ReplayAsync(host, scenario.SessionId, afterEventId: cursor);

        Assert.Equal(3, tail.Events.Count);
        Assert.Equal(full.Events[1].EventId, tail.Events[0].EventId);
    }

    [Fact]
    public async Task Replaying_with_no_acknowledged_events_returns_the_whole_visible_stream()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        // No reveals yet: the stream is empty, so the replay is an empty (but valid) envelope.
        var empty = await ReplayAsync(host, scenario.SessionId);
        Assert.Empty(empty.Events);
        Assert.Equal(scenario.SessionId, empty.SessionId);
        Assert.NotEqual(default, empty.GeneratedAt);
    }

    // =====================================================================
    // Negative authorization / isolation — every denial hidden as 404 (never 403).
    // =====================================================================

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/events?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await host.GetAsync($"/api/v1/sessions/{scenario.SessionId}/events");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_or_empty_session_id_is_404(string sessionId)
    {
        await using var factory = new WorkspaceApiFactory();
        await SeedScenarioAsync(factory);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await host.GetAsync($"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_session_in_another_tenant_is_404()
    {
        // T5 cross-tenant: a real session in org B addressed with organizationSlug = A (the caller's own
        // org). Hidden as 404 at the org-scoped resolution.
        await using var factory = new WorkspaceApiFactory();
        Guid sessionInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _hostSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B", SessionStatus.Live);
            sessionInB = session.Id;
        });

        using var client = factory.CreateClientFor(_hostSubject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionInB}/events?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_org_member_who_is_not_session_audience_is_404()
    {
        // The caller is an org member but NOT a member of the session's workspace and owns no participant:
        // no legitimate realtime relationship, hidden as 404 (never 403).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_participant_cannot_replay_a_feed_it_does_not_own()
    {
        // Stale/forged identity: participant two requests participant ONE's feed. The connection resolver
        // admits a participant only for a participant record they OWN, so this is hidden as 404 — a caller
        // can never replay another participant's feed (docs/11 "stale connection cannot subscribe to
        // another participant feed"; threat T3).
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);

        using var participantTwo = factory.CreateClientFor(_p2Subject, _issuer, _orgA);
        var response = await participantTwo.GetAsync(
            $"/api/v1/sessions/{scenario.SessionId}/events?organizationSlug={_orgA}&participantId={scenario.ParticipantOneId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_after_event_id_is_400_for_an_authorized_caller()
    {
        // The cursor is validated only AFTER authorization (so an unauthorized caller gets 404, not 400):
        // an authorized host with a malformed afterEventId gets 400.
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await host.GetAsync(
            $"/api/v1/sessions/{scenario.SessionId}/events?organizationSlug={_orgA}&afterEventId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Seeds org A with a Live session in workspace W, a workspace Host, and two user-linked active
    /// participants (each an org member so the tenant resolves for them). Returns the ids used to drive
    /// reveals and replays.
    /// </summary>
    private static async Task<Scenario> SeedScenarioAsync(WorkspaceApiFactory factory)
    {
        Scenario scenario = default;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, _hostSubject);
            var participantOneUser = await db.AddUserAsync(_issuer, _p1Subject);
            var participantTwoUser = await db.AddUserAsync(_issuer, _p2Subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, participantOneUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(org.Id, participantTwoUser.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participantOne = await db.AddParticipantAsync(org.Id, workspace.Id, participantOneUser.Id, displayName: "P1");
            var participantTwo = await db.AddParticipantAsync(org.Id, workspace.Id, participantTwoUser.Id, displayName: "P2");
            scenario = new Scenario(org.Id, workspace.Id, session.Id, participantOne.Id, participantTwo.Id);
        });
        return scenario;
    }

    private static async Task RevealToAudienceAsync(HttpClient host, Guid sessionId, Guid resourceId, string idempotencyKey)
    {
        var response = await PostRevealAsync(
            host, sessionId, new { organizationSlug = _orgA, resourceType = "Entity", resourceId }, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RevealToParticipantAsync(
        HttpClient host,
        Guid sessionId,
        Guid resourceId,
        Guid participantId,
        string idempotencyKey)
    {
        var response = await PostRevealAsync(
            host,
            sessionId,
            new { organizationSlug = _orgA, resourceType = "Entity", resourceId, participantId },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<ReplayDto> ReplayAsync(
        HttpClient client,
        Guid sessionId,
        Guid? participantId = null,
        Guid? afterEventId = null)
    {
        var url = $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}";
        if (participantId is { } participant)
        {
            url += $"&participantId={participant}";
        }

        if (afterEventId is { } cursor)
        {
            url += $"&afterEventId={cursor}";
        }

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static IReadOnlyList<string> Payloads(ReplayDto replay)
        => replay.Events.Select(item => item.Payload).ToList();

    private readonly record struct Scenario(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid ParticipantOneId,
        Guid ParticipantTwoId);

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
