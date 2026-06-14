using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the scene and content/visibility lifecycle EVENT emission (CORE-EVT-003):
/// activating a scene and changing a resource's visibility must surface as the documented session events —
/// <c>SceneActivated</c> and the realtime <c>VisibilityRuleChanged</c> (distinct from the audit record) —
/// so a reconnecting client can reconstruct state, with the recipient projection VISIBILITY-CORRECT. They
/// drive the REAL application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication
/// scheme + EF Core SQLite, foreign keys ON), with the REAL reveal/hide command, the REAL recipient
/// resolver and the REAL replay filter in the loop, so the documented "persist event -&gt; compute
/// recipients -&gt; project payload -&gt; send to recipient groups" flow runs end-to-end exactly as in
/// production.
///
/// Coverage, per the story's required tests ("Integration: events reach only authorized recipients;
/// participant never receives a hidden-resource event"):
/// <list type="bullet">
///   <item>EMISSION — revealing a Scene appends ContentRevealed + VisibilityRuleChanged + SceneActivated;
///   revealing a non-Scene appends NO SceneActivated; hiding appends ContentHidden + VisibilityRuleChanged
///   (Hidden). Each new event carries the governed resource as its visibility subject; an idempotent retry
///   appends nothing more.</item>
///   <item>AUTHORIZED RECIPIENTS — an audience-wide scene activation reaches the host AND an active
///   participant who may see the scene (replay).</item>
///   <item>NO HIDDEN-RESOURCE LEAK (the headline) — a scene activated for ONE selected participant is never
///   replayed by another participant (the scene is hidden from them); and a hide's VisibilityRuleChanged
///   (its subject is now hidden) reaches the host but NEVER a participant.</item>
///   <item>NEGATIVE AUTHORIZATION — a caller without the reveal role is 403 and emits none of the
///   events.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class VisibilityLifecycleEventEmissionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _hostSubject = "host-a";
    private const string _p1Subject = "participant-1";
    private const string _p2Subject = "participant-2";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // EMISSION — the right events, carrying the right subject, on a real change.
    // =====================================================================

    [Fact]
    public async Task Revealing_a_scene_emits_content_revealed_visibility_rule_changed_and_scene_activated()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var sceneId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await PostRevealAsync(host, scenario.SessionId, Body("Scene", sceneId), "k-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await SessionEventsAsync(factory, scenario.OrganizationId, scenario.SessionId);
        Assert.Equal(3, events.Count);

        // The central reveal event (CORE-RT-003) is unchanged.
        Assert.Single(events, e => e.EventType == SessionEventTypes.ContentRevealed);

        // The realtime VisibilityRuleChanged carries the scene as its subject and the new Visible state.
        var ruleChanged = Assert.Single(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.Null(ruleChanged.TargetParticipantId);
        Assert.Equal(nameof(VisibilityResourceType.Scene), ruleChanged.VisibilitySubjectType);
        Assert.Equal(sceneId, ruleChanged.VisibilitySubjectId);
        Assert.Contains(nameof(VisibilityState.Visible), ruleChanged.Payload, StringComparison.Ordinal);
        Assert.Equal(scenario.HostProfileId, ruleChanged.CreatedBy);

        // The scene switch: SceneActivated carries the scene as subject, the scene id in its payload.
        var activated = Assert.Single(events, e => e.EventType == SessionEventTypes.SceneActivated);
        Assert.Null(activated.TargetParticipantId);
        Assert.Equal(nameof(VisibilityResourceType.Scene), activated.VisibilitySubjectType);
        Assert.Equal(sceneId, activated.VisibilitySubjectId);
        Assert.Contains(sceneId.ToString(), activated.Payload, StringComparison.Ordinal);
        Assert.Equal(scenario.HostProfileId, activated.CreatedBy);
    }

    [Fact]
    public async Task Revealing_a_non_scene_resource_emits_no_scene_activated()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var resourceId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await PostRevealAsync(host, scenario.SessionId, Body("ContentBlock", resourceId), "k-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await SessionEventsAsync(factory, scenario.OrganizationId, scenario.SessionId);
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Single(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.DoesNotContain(events, e => e.EventType == SessionEventTypes.SceneActivated);
    }

    [Fact]
    public async Task Hiding_a_resource_emits_visibility_rule_changed_hidden_carrying_the_subject()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var resourceId = Guid.CreateVersion7();
        await factory.SeedAsync(async db => await db.AddVisibilityRuleAsync(
            scenario.OrganizationId, scenario.WorkspaceId, scenario.SessionId, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible));

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var response = await PostHideAsync(host, scenario.SessionId, Body("Entity", resourceId), "k-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await SessionEventsAsync(factory, scenario.OrganizationId, scenario.SessionId);
        Assert.Equal(2, events.Count);

        // ContentHidden carries NO subject (coarse routing so the audience can be told to remove it).
        var hidden = Assert.Single(events, e => e.EventType == SessionEventTypes.ContentHidden);
        Assert.Null(hidden.VisibilitySubjectType);
        Assert.Null(hidden.VisibilitySubjectId);

        // VisibilityRuleChanged DOES carry the now-hidden subject and the Hidden state.
        var ruleChanged = Assert.Single(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.Equal(nameof(VisibilityResourceType.Entity), ruleChanged.VisibilitySubjectType);
        Assert.Equal(resourceId, ruleChanged.VisibilitySubjectId);
        Assert.Contains(nameof(VisibilityState.Hidden), ruleChanged.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idempotent_retry_emits_no_additional_events()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var sceneId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        var first = await PostRevealAsync(host, scenario.SessionId, Body("Scene", sceneId), "k-1");
        var retry = await PostRevealAsync(host, scenario.SessionId, Body("Scene", sceneId), "k-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        // The first reveal emitted three events; the idempotent retry emitted none.
        Assert.Equal(3, (await SessionEventsAsync(factory, scenario.OrganizationId, scenario.SessionId)).Count);
    }

    // =====================================================================
    // AUTHORIZED RECIPIENTS — the scene activation reaches the audience.
    // =====================================================================

    [Fact]
    public async Task An_audience_wide_scene_activation_reaches_the_host_and_an_active_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var sceneId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        Assert.Equal(HttpStatusCode.OK, (await PostRevealAsync(host, scenario.SessionId, Body("Scene", sceneId), "k-1")).StatusCode);

        // The host replays every event including SceneActivated (the session-hosts group always receives it).
        var hostReplay = await ReplayAsync(host, scenario.SessionId);
        Assert.Contains(hostReplay.Events, e => e.EventType == SessionEventTypes.SceneActivated);

        // An active participant for whom the scene is audience-visible replays the SceneActivated event too.
        using var participant = factory.CreateClientFor(_p1Subject, _issuer, _orgA);
        var participantReplay = await ReplayAsync(participant, scenario.SessionId, scenario.ParticipantOneId);
        var activated = Assert.Single(participantReplay.Events, e => e.EventType == SessionEventTypes.SceneActivated);
        Assert.Contains(sceneId.ToString(), activated.Payload, StringComparison.Ordinal);
        // The audience projection omits the routing target (the participant never learns who else was targeted).
        Assert.Null(activated.TargetParticipantId);
    }

    // =====================================================================
    // NO HIDDEN-RESOURCE LEAK — the headline anti-leak proofs.
    // =====================================================================

    [Fact]
    public async Task A_participant_never_replays_a_scene_activated_for_a_scene_hidden_from_them()
    {
        // THE required test: a scene activated for ONE selected participant must never reach another
        // participant. The recipient resolver gates the event on its visibility subject (the scene), so the
        // non-selected participant — for whom the scene is hidden — replays NONE of the reveal's events
        // (threats T2/T3).
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var sceneId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostRevealAsync(host, scenario.SessionId, BodyForParticipant("Scene", sceneId, scenario.ParticipantOneId), "k-1")).StatusCode);

        // The SELECTED participant replays the scene's three events (all visible to them).
        using var selected = factory.CreateClientFor(_p1Subject, _issuer, _orgA);
        var selectedReplay = await ReplayAsync(selected, scenario.SessionId, scenario.ParticipantOneId);
        Assert.Single(selectedReplay.Events, e => e.EventType == SessionEventTypes.SceneActivated);
        Assert.Single(selectedReplay.Events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.Single(selectedReplay.Events, e => e.EventType == SessionEventTypes.ContentRevealed);

        // The UNSELECTED participant replays NONE of them — the scene is hidden from them.
        using var other = factory.CreateClientFor(_p2Subject, _issuer, _orgA);
        var otherReplay = await ReplayAsync(other, scenario.SessionId, scenario.ParticipantTwoId);
        Assert.Empty(otherReplay.Events);
    }

    [Fact]
    public async Task A_hide_visibility_rule_changed_reaches_the_host_but_never_a_participant()
    {
        // The hide's VisibilityRuleChanged carries the now-hidden resource as its subject, so the resolver
        // gates it to the hosts only: an audience-wide reveal-then-hide leaves the host replaying the
        // VisibilityRuleChanged(Hidden) while the participant replays ONLY the subjectless ContentHidden
        // (told to remove the resource) and NEVER a VisibilityRuleChanged about the now-hidden resource.
        await using var factory = new WorkspaceApiFactory();
        var scenario = await SeedScenarioAsync(factory);
        var resourceId = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        Assert.Equal(HttpStatusCode.OK, (await PostRevealAsync(host, scenario.SessionId, Body("Entity", resourceId), "k-reveal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostHideAsync(host, scenario.SessionId, Body("Entity", resourceId), "k-hide")).StatusCode);

        // The host replays a VisibilityRuleChanged carrying the Hidden state (the security-relevant record).
        var hostReplay = await ReplayAsync(host, scenario.SessionId);
        Assert.Contains(
            hostReplay.Events,
            e => e.EventType == SessionEventTypes.VisibilityRuleChanged
                && e.Payload.Contains(nameof(VisibilityState.Hidden), StringComparison.Ordinal));

        // The participant replays the subjectless ContentHidden (to remove the resource) but NEVER any
        // VisibilityRuleChanged — its subject is now hidden, so a participant never receives it (no leak).
        using var participant = factory.CreateClientFor(_p1Subject, _issuer, _orgA);
        var participantReplay = await ReplayAsync(participant, scenario.SessionId, scenario.ParticipantOneId);
        Assert.Contains(participantReplay.Events, e => e.EventType == SessionEventTypes.ContentHidden);
        Assert.DoesNotContain(participantReplay.Events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
    }

    // =====================================================================
    // NEGATIVE AUTHORIZATION — a denied command emits none of the events.
    // =====================================================================

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task A_non_reveal_role_is_403_and_emits_no_events(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new Seed(org.Id, workspace.Id, session.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body("Scene", Guid.CreateVersion7()), "k-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string resourceType, Guid resourceId)
        => new { organizationSlug = _orgA, resourceType, resourceId };

    private static object BodyForParticipant(string resourceType, Guid resourceId, Guid participantId)
        => new { organizationSlug = _orgA, resourceType, resourceId, participantId };

    /// <summary>
    /// Seeds org A with a Live session in workspace W, a workspace Host, and two user-linked active
    /// participants (each an org member so the tenant resolves for them on replay).
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
            scenario = new Scenario(org.Id, workspace.Id, session.Id, hostUser.Id, participantOne.Id, participantTwo.Id);
        });
        return scenario;
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(HttpClient client, Guid sessionId, object body, string idempotencyKey)
        => await PostAsync(client, $"/api/v1/sessions/{sessionId}/reveal", body, idempotencyKey);

    private static async Task<HttpResponseMessage> PostHideAsync(HttpClient client, Guid sessionId, object body, string idempotencyKey)
        => await PostAsync(client, $"/api/v1/sessions/{sessionId}/hide", body, idempotencyKey);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<ReplayDto> ReplayAsync(HttpClient client, Guid sessionId, Guid? participantId = null)
    {
        var url = $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}";
        if (participantId is { } participant)
        {
            url += $"&participantId={participant}";
        }

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync();
    }

    private readonly record struct Scenario(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid HostProfileId,
        Guid ParticipantOneId,
        Guid ParticipantTwoId);

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

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
