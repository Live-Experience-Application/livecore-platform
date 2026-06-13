using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the session lifecycle EVENT emission (CORE-EVT-001): starting and ending a
/// session must append exactly one durable <c>SessionStarted</c>/<c>SessionEnded</c> session event each,
/// deliver it to the authorized session audience (hosts/observers/participants), append a matching
/// append-only audit record, and have reconnect replay include it. They drive the REAL application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), with the REAL recipient resolver and replay filter in the loop, so the documented
/// "persist event -&gt; compute recipients -&gt; project payload -&gt; send to recipient groups" flow runs
/// end-to-end exactly as in production.
///
/// Coverage, per the story's required tests ("Integration: start/end persist exactly one event each and
/// reach hosts/observers; replay includes them"):
/// <list type="bullet">
///   <item>EMISSION — start persists exactly one audience-wide <c>SessionStarted</c> event (no visibility
///   subject, no selected participant) and one <c>SessionStarted</c> audit record (Prepared → Live); end
///   persists exactly one <c>SessionEnded</c> event and audit record (Live → Ended); a start-then-end
///   persists exactly one of each, in order.</item>
///   <item>DELIVERY + REPLAY — the host, an observer and an active participant each replay the
///   <c>SessionStarted</c> event (the subjectless audience event reaches the whole session audience).</item>
///   <item>NEGATIVE AUTHORIZATION / ISOLATION — a non-control workspace role is 403 and emits NO event and
///   NO audit; an out-of-state command is 409 and emits neither; a cross-tenant session is hidden as 404
///   and emits neither — so a denied/rejected command can never leak an event to the audience
///   (fail-closed; threats T1/T3/T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionLifecycleEventEmissionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // EMISSION — exactly one event + one audit record per successful transition.
    // =====================================================================

    [Fact]
    public async Task Start_persists_exactly_one_audience_wide_SessionStarted_event_and_audit_record()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Prepared);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one durable event: audience-wide (no target participant), subjectless (no visibility
        // subject), authored by the host, with the session id + new status in its payload (identifiers only).
        var sessionEvent = Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(SessionEventTypes.SessionStarted, sessionEvent.EventType);
        Assert.Equal(seed.SessionId, sessionEvent.SessionId);
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.HasVisibilitySubject);
        Assert.Equal(seed.HostProfileId, sessionEvent.CreatedBy);
        Assert.Contains(seed.SessionId.ToString(), sessionEvent.Payload, StringComparison.Ordinal);
        Assert.Contains(nameof(SessionStatus.Live), sessionEvent.Payload, StringComparison.Ordinal);

        // Exactly one matching audit record: the Prepared → Live transition on the session resource.
        var entry = Assert.Single(await AuditAsync(factory, seed.OrganizationId));
        Assert.Equal(AuditAction.SessionStarted, entry.Action);
        Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(nameof(Session), entry.ResourceType);
        Assert.Equal(seed.SessionId, entry.ResourceId);
        Assert.Equal(nameof(SessionStatus.Prepared), entry.PreviousState);
        Assert.Equal(nameof(SessionStatus.Live), entry.NewState);
        Assert.Equal(seed.HostProfileId, entry.ActorUserProfileId);
        Assert.Null(entry.TargetParticipantId);
    }

    [Fact]
    public async Task End_persists_exactly_one_SessionEnded_event_and_audit_record()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sessionEvent = Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(SessionEventTypes.SessionEnded, sessionEvent.EventType);
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.HasVisibilitySubject);
        Assert.Contains(nameof(SessionStatus.Ended), sessionEvent.Payload, StringComparison.Ordinal);

        var entry = Assert.Single(await AuditAsync(factory, seed.OrganizationId));
        Assert.Equal(AuditAction.SessionEnded, entry.Action);
        Assert.Equal(seed.SessionId, entry.ResourceId);
        Assert.Equal(nameof(SessionStatus.Live), entry.PreviousState);
        Assert.Equal(nameof(SessionStatus.Ended), entry.NewState);
        Assert.Equal(seed.HostProfileId, entry.ActorUserProfileId);
    }

    [Fact]
    public async Task Start_then_end_persists_exactly_one_event_and_one_audit_record_each()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Prepared);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var start = await client.PostAsync($"/api/v1/sessions/{seed.SessionId}/start?organizationSlug={_orgA}", content: null);
        var end = await client.PostAsync($"/api/v1/sessions/{seed.SessionId}/end?organizationSlug={_orgA}", content: null);

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        // Two events, in append order: SessionStarted then SessionEnded.
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(2, events.Count);
        Assert.Equal(SessionEventTypes.SessionStarted, events[0].EventType);
        Assert.Equal(SessionEventTypes.SessionEnded, events[1].EventType);

        // Two audit records, one per transition.
        var audit = await AuditAsync(factory, seed.OrganizationId);
        Assert.Equal(2, audit.Count);
        Assert.Equal(AuditAction.SessionStarted, audit[0].Action);
        Assert.Equal(AuditAction.SessionEnded, audit[1].Action);
    }

    // =====================================================================
    // DELIVERY + REPLAY — the subjectless audience event reaches the host, an
    // observer and an active participant.
    // =====================================================================

    [Fact]
    public async Task SessionStarted_reaches_hosts_observers_and_participants_via_replay()
    {
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        const string observerSubject = "observer-a";
        const string participantSubject = "participant-a";
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, hostSubject);
            var observerUser = await db.AddUserAsync(_issuer, observerSubject);
            var participantUser = await db.AddUserAsync(_issuer, participantSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, observerUser.Id, MembershipRole.Observer);
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, observerUser.Id, MembershipRole.Observer);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, displayName: "P");
            sessionId = session.Id;
            participantId = participant.Id;
        });

        using var host = factory.CreateClientFor(hostSubject, _issuer, _orgA);
        var started = await host.PostAsync($"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", content: null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        // The host replays the event (the session-hosts group always receives it).
        var hostReplay = await ReplayAsync(host, sessionId);
        Assert.Equal(SessionEventTypes.SessionStarted, Assert.Single(hostReplay.Events).EventType);

        // An observer replays the event (the subjectless audience event reaches the observers group).
        using var observer = factory.CreateClientFor(observerSubject, _issuer, _orgA);
        var observerReplay = await ReplayAsync(observer, sessionId);
        Assert.Equal(SessionEventTypes.SessionStarted, Assert.Single(observerReplay.Events).EventType);

        // An active participant replays the event (the audience fan-out reaches each active participant,
        // ungated because the event has no visibility subject).
        using var participant = factory.CreateClientFor(participantSubject, _issuer, _orgA);
        var participantReplay = await ReplayAsync(participant, sessionId, participantId);
        Assert.Equal(SessionEventTypes.SessionStarted, Assert.Single(participantReplay.Events).EventType);
    }

    // =====================================================================
    // NEGATIVE — a denied or rejected command emits NO event and NO audit.
    // =====================================================================

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Start_by_a_non_control_role_is_403_and_emits_no_event_or_audit(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role, SessionStatus.Prepared);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Empty(await AuditAsync(factory, seed.OrganizationId));
    }

    [Fact]
    public async Task Start_an_already_live_session_is_409_and_emits_no_event_or_audit()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Empty(await AuditAsync(factory, seed.OrganizationId));
    }

    [Fact]
    public async Task Start_for_a_session_in_another_tenant_is_404_and_emits_no_event()
    {
        // T5: a real, Prepared session in org B addressed with organizationSlug = A (the caller's own org).
        // Hidden as 404, and NO event/audit is written for the org-B session.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sessionInBId = Guid.Empty;
        Guid orgBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B Session", SessionStatus.Prepared);
            sessionInBId = session.Id;
            orgBId = orgB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInBId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, orgBId, sessionInBId));
        Assert.Empty(await AuditAsync(factory, orgBId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Seeds org A with a caller holding <paramref name="role"/> at both the org and workspace level, and a
    /// session in <paramref name="status"/>. Returns the ids (including the caller's resolved user profile
    /// id, the event/audit actor).
    /// </summary>
    private static async Task<Seed> SeedSessionAsync(
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
            seed = new Seed(org.Id, workspace.Id, session.Id, user.Id);
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

    private static async Task<IReadOnlyList<AuditLogEntry>> AuditAsync(
        WorkspaceApiFactory factory,
        Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
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

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid SessionId, Guid HostProfileId);

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
