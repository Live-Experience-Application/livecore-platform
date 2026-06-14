using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the participant PRESENCE routes (CORE-PRS-001) — the story's required
/// "Integration: join then leave persist one event each, are delivered to hosts/observers, contain no display
/// names/PII; foreign-tenant/unauthorized denied". They drive the REAL application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), with the
/// REAL join/leave services, session-event publisher, recipient resolver and reconnect-replay filter in the
/// loop, so the documented "authorize -&gt; persist event -&gt; compute recipients -&gt; deliver to recipient
/// groups" flow runs end-to-end exactly as in production. CORE-EVT-002 added the
/// <c>ParticipantJoined</c>/<c>ParticipantLeft</c> emission but left it unreachable; this story adds the missing
/// caller, so these tests assert presence works through the HTTP entry point.
///
/// "Delivered to hosts/observers" is proved through the reconnect-replay route
/// (<c>GET /api/v1/sessions/{sessionId}/events</c>), which replays exactly the events delivered to the caller's
/// server-managed groups: a host and an observer each replaying the presence event proves it reached the hosts
/// and observers groups, without needing a live SignalR connection.
///
/// Coverage:
/// <list type="bullet">
///   <item>JOIN then LEAVE persist exactly one event each (one <c>ParticipantJoined</c>, one
///   <c>ParticipantLeft</c>), each delivered to the host and an observer (replay), each payload
///   identifier-only — never the participant's display name (no PII leak, threat T7).</item>
///   <item>NEGATIVE AUTHORIZATION / ISOLATION — a non-control workspace role is 403; a tenant member who is not
///   a member of the session's workspace is hidden 404; a cross-tenant session is hidden 404; an anonymous
///   caller is 401 — and every one emits NO event, so a denied command can never leak presence to the audience
///   (fail-closed; threats T1/T3/T5).</item>
///   <item>The <c>session.participant.max</c> free-tier cap (CORE-MON-005) is enforced on this REAL join path:
///   a join at the cap is 409 and emits no event. An ended session is 409. A leave is idempotent: an
///   already-left participant is a 200 no-op that emits no second event.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionParticipantPresenceEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive display name (participant PII) that must NEVER appear in any persisted event payload or any
    // replayed envelope (threat T7).
    private const string _participantDisplayName = "Backstage Pass Holder";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // HEADLINE — join then leave, one event each, delivered to hosts/observers, no PII.
    // =====================================================================

    [Fact]
    public async Task Join_then_leave_persist_one_event_each_delivered_to_hosts_and_observers_without_PII()
    {
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        const string observerSubject = "observer-a";
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;
        Guid organizationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, hostSubject);
            var observerUser = await db.AddUserAsync(_issuer, observerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, observerUser.Id, MembershipRole.Observer);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, observerUser.Id, MembershipRole.Observer);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: null, displayName: _participantDisplayName);
            organizationId = org.Id;
            sessionId = session.Id;
            participantId = participant.Id;
        });

        using var host = factory.CreateClientFor(hostSubject, _issuer, _orgA);
        using var observer = factory.CreateClientFor(observerSubject, _issuer, _orgA);

        // JOIN — the host admits the participant.
        var joinResponse = await host.PostAsync(JoinUrl(sessionId, participantId), content: null);
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var joinBody = await joinResponse.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(joinBody);
        Assert.Equal("Joined", joinBody.Outcome);
        Assert.Equal(participantId, joinBody.ParticipantId);

        // Exactly ONE ParticipantJoined event, identifier-only.
        var afterJoin = await SessionEventsAsync(factory, organizationId, sessionId);
        var joined = Assert.Single(afterJoin);
        Assert.Equal(SessionEventTypes.ParticipantJoined, joined.EventType);
        AssertPayloadIsParticipantIdentifierOnly(joined.Payload, participantId);

        // Delivered to hosts AND observers: both replay the ParticipantJoined event (it reached their groups).
        Assert.Contains(
            (await ReplayAsync(host, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantJoined);
        Assert.Contains(
            (await ReplayAsync(observer, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantJoined);

        // LEAVE — the host removes the participant.
        var leaveResponse = await host.PostAsync(LeaveUrl(sessionId, participantId), content: null);
        Assert.Equal(HttpStatusCode.OK, leaveResponse.StatusCode);
        var leaveBody = await leaveResponse.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(leaveBody);
        Assert.Equal("Left", leaveBody.Outcome);

        // One event each: the stream now holds the earlier ParticipantJoined plus exactly one ParticipantLeft.
        var afterLeave = await SessionEventsAsync(factory, organizationId, sessionId);
        Assert.Equal(2, afterLeave.Count);
        Assert.Single(afterLeave, e => e.EventType == SessionEventTypes.ParticipantJoined);
        var left = Assert.Single(afterLeave, e => e.EventType == SessionEventTypes.ParticipantLeft);
        AssertPayloadIsParticipantIdentifierOnly(left.Payload, participantId);

        // Delivered to hosts AND observers: both replay the ParticipantLeft event too.
        Assert.Contains(
            (await ReplayAsync(host, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantLeft);
        Assert.Contains(
            (await ReplayAsync(observer, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantLeft);

        // No PII leak: not one persisted payload nor one replayed envelope carries the participant's display name.
        AssertNoEventLeaksDisplayName(afterLeave);
        AssertNoReplayLeaksDisplayName(await ReplayAsync(host, sessionId));
        AssertNoReplayLeaksDisplayName(await ReplayAsync(observer, sessionId));
    }

    // =====================================================================
    // NEGATIVE AUTHORIZATION — a denied command emits NO event.
    // =====================================================================

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Join_by_a_non_control_role_is_403_and_emits_no_event(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionWithParticipantAsync(factory, subject, role, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(JoinUrl(seed.SessionId, seed.ParticipantId), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Leave_by_a_non_control_role_is_403_and_emits_no_event(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionWithParticipantAsync(factory, subject, role, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(LeaveUrl(seed.SessionId, seed.ParticipantId), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    [Fact]
    public async Task Join_by_a_tenant_member_who_is_not_in_the_sessions_workspace_is_404_and_emits_no_event()
    {
        // The caller is an Owner of the organization (so the tenant resolves) but holds NO membership in the
        // session's workspace, so the session must be hidden as 404 — a non-member must not learn it exists
        // (threats T1/T5) — and nothing is emitted.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // Deliberately NO workspace membership for the caller.
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: null);
            organizationId = org.Id;
            sessionId = session.Id;
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(JoinUrl(sessionId, participantId), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));
    }

    [Fact]
    public async Task Join_for_a_session_in_another_tenant_is_404_and_emits_no_event()
    {
        // T5: a real, Live session in org B addressed with organizationSlug = A (the caller's own org). Even
        // though the caller is also a Host in B, addressing the org-B session through slug A hides it as 404, and
        // NO event is written for the org-B session.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid orgBId = Guid.Empty;
        Guid sessionInBId = Guid.Empty;
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
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B Session", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(orgB.Id, workspaceInB.Id, userProfileId: null);
            orgBId = orgB.Id;
            sessionInBId = session.Id;
            participantInBId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInBId}/participants/{participantInBId}/join?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, orgBId, sessionInBId));
    }

    [Fact]
    public async Task Join_without_a_token_is_401_and_emits_no_event()
    {
        await using var factory = new WorkspaceApiFactory();
        var seed = await SeedSessionWithParticipantAsync(factory, "host-a", MembershipRole.Host, SessionStatus.Live);

        using var client = factory.CreateAnonymousClient();
        var response = await client.PostAsync(JoinUrl(seed.SessionId, seed.ParticipantId), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    // =====================================================================
    // STATE + QUOTA — the reused service's denials surface on the real path.
    // =====================================================================

    [Fact]
    public async Task Join_at_the_session_participant_cap_is_409_and_emits_no_event()
    {
        // CORE-MON-005 enforced on the REAL path: the session is at its session.participant.max limit, so the
        // host's join is refused (the limit, not the caller, is the reason) and emits nothing.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionWithParticipantAsync(factory, subject, MembershipRole.Host, SessionStatus.Live);
        await factory.SeedAsync(async db =>
        {
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionParticipantMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Session);
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.Session, seed.SessionId, entitlement, limit: 1);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.Session, seed.SessionId, quota, usedAmount: 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(JoinUrl(seed.SessionId, seed.ParticipantId), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    [Fact]
    public async Task Join_an_ended_session_is_409_and_emits_no_event()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionWithParticipantAsync(factory, subject, MembershipRole.Host, SessionStatus.Ended);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(JoinUrl(seed.SessionId, seed.ParticipantId), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    [Fact]
    public async Task Leave_an_already_removed_participant_is_200_AlreadyLeft_and_emits_no_event()
    {
        // The leave is idempotent: a participant that already left holds no presence to remove, so the command is
        // a 200 no-op that emits no second ParticipantLeft.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: null, removed: true);
            organizationId = org.Id;
            sessionId = session.Id;
            participantId = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(LeaveUrl(sessionId, participantId), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("AlreadyLeft", body.Outcome);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static string JoinUrl(Guid sessionId, Guid participantId)
        => $"/api/v1/sessions/{sessionId}/participants/{participantId}/join?organizationSlug={_orgA}";

    private static string LeaveUrl(Guid sessionId, Guid participantId)
        => $"/api/v1/sessions/{sessionId}/participants/{participantId}/leave?organizationSlug={_orgA}";

    /// <summary>
    /// Seeds org A with a caller holding <paramref name="role"/> at both the org and workspace level, a session
    /// in <paramref name="status"/>, and one active participant. Returns the ids.
    /// </summary>
    private static async Task<Seed> SeedSessionWithParticipantAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role,
        SessionStatus status)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", status);
            var participant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: null, displayName: _participantDisplayName);
            seed = new Seed(org.Id, workspace.Id, session.Id, participant.Id);
        });
        return seed;
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

    private static async Task<ReplayDto> ReplayAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>The persisted payload is the participant id and nothing else — no display name or other PII (threat T7).</summary>
    private static void AssertPayloadIsParticipantIdentifierOnly(string payload, Guid participantId)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(participantId, document.RootElement.GetProperty("ParticipantId").GetGuid());
        Assert.False(
            payload.Contains(_participantDisplayName, StringComparison.OrdinalIgnoreCase),
            "A persisted event payload leaked the participant display name (PII).");
    }

    private static void AssertNoEventLeaksDisplayName(IReadOnlyList<SessionEvent> events)
        => Assert.All(
            events,
            e => Assert.False(
                e.Payload.Contains(_participantDisplayName, StringComparison.OrdinalIgnoreCase),
                "A persisted event payload leaked the participant display name (PII)."));

    private static void AssertNoReplayLeaksDisplayName(ReplayDto replay)
        => Assert.All(
            replay.Events,
            e => Assert.False(
                e.Payload.Contains(_participantDisplayName, StringComparison.OrdinalIgnoreCase),
                "A replayed envelope leaked the participant display name (PII)."));

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid SessionId, Guid ParticipantId);

    private sealed record PresenceDto(Guid SessionId, Guid ParticipantId, string Outcome);

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
