// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the SELF-SERVICE participant presence behavior of the presence routes
/// (CORE-PSELF-003, the "Participant Self-Service Lifecycle" epic). The presence commands
/// (<c>POST /api/v1/sessions/{sessionId}/participants/{participantId}/join</c> and <c>.../leave</c>) now authorize
/// the principal that OWNS the target participant to record its OWN presence — in ADDITION to the existing
/// host admission — so, combined with the CORE-PSELF-002 self-provision, a single authenticated audience member
/// can both provision its participant and join, with no host step. They drive the REAL application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), with
/// the REAL join/leave services, session-event publisher, recipient resolver and reconnect-replay filter in the
/// loop, so the documented "authorize → persist event → compute recipients → deliver" flow runs end-to-end.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>POSITIVE (the combined journey): the authenticated OWNER of a participant (self-provisioned via
///   CORE-PSELF-002 on the first <c>GET /sessions/{id}/me</c>) self-joins for ITS OWN participantId and succeeds
///   (today host-admission-only made this a 403), appears in the roster with <c>isSelf</c> true, then self-leaves
///   — and the catalogued <c>ParticipantJoined</c>/<c>ParticipantLeft</c> events are STILL appended and delivered
///   to the session audience (proved through a host's reconnect-replay), each payload identifier-only.</item>
///   <item>NEGATIVE: a workspace member joining a participant it does NOT own is denied 403 and emits no event;
///   a caller who owns a participant but is NOT a member of the session's workspace stays a fail-closed 404 (never
///   403, the membership gate is checked before ownership) and emits no event; and HOST admission of a participant
///   the host does not own is unchanged (still 200, still emits the event).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionParticipantSelfPresenceEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive display name (participant PII) that must NEVER appear in any persisted event payload (threat T7).
    private const string _participantDisplayName = "Backstage Pass Holder";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // POSITIVE — self-provision, then self-join, roster, self-leave, events delivered to the audience.
    // =====================================================================

    [Fact]
    public async Task A_self_provisioned_participant_can_self_join_appear_in_the_roster_and_self_leave_with_events_delivered()
    {
        await using var factory = new WorkspaceApiFactory();
        const string memberSubject = "audience-member";
        const string hostSubject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // The self-service caller is a plain workspace Participant (NOT a session-control role) with no
            // pre-created participant, so the first /me read self-provisions one (CORE-PSELF-002).
            var member = await db.AddUserAsync(_issuer, memberSubject);
            var host = await db.AddUserAsync(_issuer, hostSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, member.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, member.Id, MembershipRole.Participant);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            organizationId = org.Id;
            sessionId = session.Id;
        });

        using var member = factory.CreateClientFor(memberSubject, _issuer, _orgA);
        using var host = factory.CreateClientFor(hostSubject, _issuer, _orgA);

        // CORE-PSELF-002: the first self-read self-provisions the caller's own participant.
        var meResponse = await member.GetAsync($"/api/v1/sessions/{sessionId}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(me);
        Assert.NotEqual(Guid.Empty, me.ParticipantId);

        // SELF-JOIN for ITS OWN participantId — today host-admission-only makes this a 403; CORE-PSELF-003 admits it.
        var joinResponse = await member.PostAsync(JoinUrl(sessionId, me.ParticipantId), content: null);
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var joinBody = await joinResponse.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(joinBody);
        Assert.Equal("Joined", joinBody.Outcome);
        Assert.Equal(me.ParticipantId, joinBody.ParticipantId);

        // Exactly ONE ParticipantJoined event, identifier-only, routed through the same JoinAsync service.
        var afterJoin = await SessionEventsAsync(factory, organizationId, sessionId);
        var joined = Assert.Single(afterJoin);
        Assert.Equal(SessionEventTypes.ParticipantJoined, joined.EventType);
        AssertPayloadIsParticipantIdentifierOnly(joined.Payload, me.ParticipantId);

        // Delivered to the session audience: the host replays the ParticipantJoined event (it reached the hosts group).
        Assert.Contains(
            (await ReplayAsync(host, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantJoined);

        // The caller appears in its own session roster with isSelf true for exactly its own entry.
        var roster = await RosterAsync(member, sessionId);
        var ownEntry = roster.Participants.Single(p => p.ParticipantId == me.ParticipantId);
        Assert.True(ownEntry.IsSelf);

        // SELF-LEAVE for ITS OWN participantId.
        var leaveResponse = await member.PostAsync(LeaveUrl(sessionId, me.ParticipantId), content: null);
        Assert.Equal(HttpStatusCode.OK, leaveResponse.StatusCode);
        var leaveBody = await leaveResponse.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(leaveBody);
        Assert.Equal("Left", leaveBody.Outcome);

        // One ParticipantLeft event in addition to the earlier ParticipantJoined.
        var afterLeave = await SessionEventsAsync(factory, organizationId, sessionId);
        Assert.Equal(2, afterLeave.Count);
        Assert.Single(afterLeave, e => e.EventType == SessionEventTypes.ParticipantJoined);
        var left = Assert.Single(afterLeave, e => e.EventType == SessionEventTypes.ParticipantLeft);
        AssertPayloadIsParticipantIdentifierOnly(left.Payload, me.ParticipantId);

        // Delivered to the session audience: the host replays the ParticipantLeft event too.
        Assert.Contains(
            (await ReplayAsync(host, sessionId)).Events,
            e => e.EventType == SessionEventTypes.ParticipantLeft);
    }

    // =====================================================================
    // NEGATIVE — an unrelated caller is denied, fail-closed, and emits no event.
    // =====================================================================

    [Fact]
    public async Task Self_join_for_a_participant_the_caller_does_not_own_is_403_and_emits_no_event()
    {
        // The caller is a workspace member (a Participant) but the TARGET participant belongs to someone else.
        // The caller is neither a session-control role nor the participant's owner, so it is an unrelated caller
        // denied 403 — and nothing is emitted (fail-closed; threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "audience-member";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid foreignParticipantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var other = await db.AddUserAsync(_issuer, "someone-else");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(org.Id, other.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, caller.Id, MembershipRole.Participant);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, other.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            // The target participant is owned by ANOTHER user, not the caller.
            var foreignParticipant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: other.Id, displayName: _participantDisplayName);
            organizationId = org.Id;
            sessionId = session.Id;
            foreignParticipantId = foreignParticipant.Id;
        });

        using var caller = factory.CreateClientFor(callerSubject, _issuer, _orgA);

        var joinResponse = await caller.PostAsync(JoinUrl(sessionId, foreignParticipantId), content: null);
        Assert.Equal(HttpStatusCode.Forbidden, joinResponse.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));

        // The leave verb authorizes identically.
        var leaveResponse = await caller.PostAsync(LeaveUrl(sessionId, foreignParticipantId), content: null);
        Assert.Equal(HttpStatusCode.Forbidden, leaveResponse.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));
    }

    [Fact]
    public async Task Self_join_by_a_caller_who_owns_the_participant_but_is_not_a_workspace_member_is_404_and_emits_no_event()
    {
        // The caller OWNS the target participant but is NOT a member of the session's workspace (only an org
        // member). Authorization is NOT loosened: the workspace-membership gate is checked BEFORE ownership, so a
        // non-member is hidden as a fail-closed 404 (never 403) and the self-service path cannot be reached
        // (threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "audience-member";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid ownParticipantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is deliberately NOT enrolled as a member of this workspace.
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            // The participant is linked to the caller's own user, so the caller genuinely owns it...
            var own = await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: caller.Id);
            organizationId = org.Id;
            sessionId = session.Id;
            ownParticipantId = own.Id;
        });

        using var caller = factory.CreateClientFor(callerSubject, _issuer, _orgA);

        // ...but it is still 404, never 403: the membership gate fails closed before the ownership match.
        var response = await caller.PostAsync(JoinUrl(sessionId, ownParticipantId), content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));
    }

    [Fact]
    public async Task Self_join_for_a_session_in_another_tenant_is_404_and_emits_no_event()
    {
        // T5: the caller is a workspace member of org A (and owns its participant there) but names org A in the
        // query while the token asserts only org B, so tenant resolution denies and the command is hidden as a
        // fail-closed 404 — never 403 — and emits nothing.
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "audience-member";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid ownParticipantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(orgA.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(orgA.Id, workspace.Id, caller.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(orgA.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            var own = await db.AddParticipantAsync(orgA.Id, workspace.Id, userProfileId: caller.Id);
            organizationId = orgA.Id;
            sessionId = session.Id;
            ownParticipantId = own.Id;
        });

        // The token claims only org B, so resolving org A fails.
        using var caller = factory.CreateClientFor(callerSubject, _issuer, _orgB);

        var response = await caller.PostAsync(JoinUrl(sessionId, ownParticipantId), content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await SessionEventsAsync(factory, organizationId, sessionId));
    }

    // =====================================================================
    // HOST admission unchanged — the new self-service branch does not regress host-driven presence.
    // =====================================================================

    [Fact]
    public async Task A_host_admitting_a_participant_it_does_not_own_still_succeeds_and_emits_the_event()
    {
        // The host does NOT own the target participant (it is linked to another user), yet host admission still
        // works exactly as before: a session-control role may admit ANY participant.
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, hostSubject);
            var member = await db.AddUserAsync(_issuer, "audience-member");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, member.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, member.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: member.Id, displayName: _participantDisplayName);
            organizationId = org.Id;
            sessionId = session.Id;
            participantId = participant.Id;
        });

        using var host = factory.CreateClientFor(hostSubject, _issuer, _orgA);

        var joinResponse = await host.PostAsync(JoinUrl(sessionId, participantId), content: null);
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var joined = Assert.Single(await SessionEventsAsync(factory, organizationId, sessionId));
        Assert.Equal(SessionEventTypes.ParticipantJoined, joined.EventType);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static string JoinUrl(Guid sessionId, Guid participantId)
        => $"/api/v1/sessions/{sessionId}/participants/{participantId}/join?organizationSlug={_orgA}";

    private static string LeaveUrl(Guid sessionId, Guid participantId)
        => $"/api/v1/sessions/{sessionId}/participants/{participantId}/leave?organizationSlug={_orgA}";

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

    private static async Task<RosterDto> RosterAsync(HttpClient client, Guid sessionId)
    {
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}/roster?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
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

    private sealed record PresenceDto(Guid SessionId, Guid ParticipantId, string Outcome);

    private sealed record SelfContextDto(Guid SessionId, Guid ParticipantId, string DisplayName, bool Present);

    private sealed record RosterDto(Guid SessionId, IReadOnlyList<RosterParticipantDto> Participants);

    private sealed record RosterParticipantDto(Guid ParticipantId, string DisplayName, bool Present, bool IsSelf);

    private sealed record ReplayDto(Guid SessionId, IReadOnlyList<ReplayItemDto> Events, DateTimeOffset GeneratedAt);

    private sealed record ReplayItemDto(
        Guid EventId,
        string EventType,
        Guid SessionId,
        string Payload,
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid? TargetParticipantId);
}
