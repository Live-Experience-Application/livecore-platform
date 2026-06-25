// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the participant SELF-PROVISIONING behavior of the session-self read
/// (CORE-PSELF-002, the "Participant Self-Service Lifecycle" epic, <c>GET /api/v1/sessions/{sessionId}/me</c>).
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication
/// scheme + EF Core SQLite, foreign keys ON), so the documented request flow (authentication → tenant context
/// resolver → endpoint → object-level workspace-membership authorization → server-side participant creation) is
/// exercised end-to-end exactly as in production.
///
/// CORE-PSELF-002 deepens the CORE-PSELF-001 self-id read (which assumed a participant already existed): on the
/// FIRST session-self read by an authenticated workspace member who has no participant yet, the handler
/// self-provisions one via <c>Participant.Create</c> + the Participants-owned <c>IParticipantRepository.AddAsync</c>
/// (idempotent on the unique (workspace_id, user_id) index), so the whole audience surface (the visible feed and
/// the roster) becomes reachable without a host pre-creating a participant.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>POSITIVE: a workspace member who is NOT pre-provisioned calls <c>/me</c> and receives a 200
///   <c>SessionParticipantContext</c> with a stable participantId, then reads its OWN visible feed (now
///   reachable) and appears in the roster with <c>isSelf</c> true.</item>
///   <item>IDEMPOTENT: a re-call returns the SAME participantId and creates no second participant (the unique
///   (workspace_id, user_id) index).</item>
///   <item>NEGATIVE (fail-closed, never 403): a tenant member who is NOT a member of the session's workspace,
///   and a foreign-tenant caller, both stay a hidden 404 and provision nothing — authorization is not
///   loosened.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionParticipantSelfProvisionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- POSITIVE: first self-read self-provisions, feed + roster become reachable ----

    [Fact]
    public async Task A_workspace_member_self_provisions_on_first_self_read_and_appears_in_the_roster()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "audience-member";
        var seeded = await SeedWorkspaceMemberAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // FIRST session-self read: the caller has no participant yet but IS a workspace member, so it
        // self-provisions one and receives its own context with a stable participant id.
        var meResponse = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(me);
        Assert.Equal(seeded.SessionId, me.SessionId);
        Assert.NotEqual(Guid.Empty, me.ParticipantId);
        // The token subject is the initial display name of a self-provisioned participant.
        Assert.Equal(subject, me.DisplayName);
        Assert.False(me.Present); // no live realtime connection registered

        // The participant-keyed audience reads are now REACHABLE for the caller: it can read its OWN visible feed
        // (the feed is session-scoped, so it names the session it is for).
        var feedResponse = await client.GetAsync(
            $"/api/v1/participants/{me.ParticipantId}/visible-feed?organizationSlug={_orgA}&sessionId={seeded.SessionId}");
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        var feed = await feedResponse.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);
        Assert.Equal(me.ParticipantId, feed.ParticipantId);

        // ...and it appears in the session roster with isSelf true for exactly its own entry.
        var rosterResponse = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, rosterResponse.StatusCode);
        var roster = await rosterResponse.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(roster);
        var ownEntry = roster.Participants.Single(p => p.ParticipantId == me.ParticipantId);
        Assert.True(ownEntry.IsSelf);
        Assert.All(
            roster.Participants.Where(p => p.ParticipantId != me.ParticipantId),
            p => Assert.False(p.IsSelf));
    }

    // ---- IDEMPOTENT: a re-call returns the same participant and creates no second ----

    [Fact]
    public async Task Self_provisioning_is_idempotent_a_recall_returns_the_same_participant_and_creates_no_second()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "audience-member";
        var seeded = await SeedWorkspaceMemberAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.GetAsync($"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstMe = await first.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(firstMe);

        var second = await client.GetAsync($"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondMe = await second.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(secondMe);

        // Same stable participant id across calls — the second read resolves the existing participant.
        Assert.Equal(firstMe.ParticipantId, secondMe.ParticipantId);

        // Exactly ONE participant exists in the workspace (no second was written — the unique index is the
        // idempotency backstop).
        Assert.Equal(1, await CountParticipantsAsync(factory, seeded.WorkspaceId));
    }

    // ---- NEGATIVE: a non-member / foreign-tenant caller is 404, never 403, and provisions nothing ----

    [Fact]
    public async Task A_tenant_member_who_is_not_a_workspace_member_cannot_self_provision_and_is_404()
    {
        // The caller is an org member (so tenant resolution succeeds) but NOT a member of the session's OWN
        // workspace, so it cannot self-provision — fail-closed hidden 404, never 403 (threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider";
        Guid sessionId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is deliberately NOT enrolled as a member of this workspace.
            workspaceId = ws.Id;
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Fail-closed: NOTHING was provisioned for a non-member of the workspace.
        Assert.Equal(0, await CountParticipantsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task A_foreign_tenant_caller_cannot_self_provision_and_is_404()
    {
        // T5: the caller is a workspace member of org A and names org A in the query, but the token asserts only
        // org B, so tenant resolution denies and the read is hidden as 404 — no participant is provisioned.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "audience-member";
        var seeded = await SeedWorkspaceMemberAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(0, await CountParticipantsAsync(factory, seeded.WorkspaceId));
    }

    /// <summary>
    /// Seeds, in org A, a caller that is an organization member (so tenant resolution succeeds) AND a member of
    /// the session's workspace (so it may self-provision and read the roster), a workspace and a LIVE session —
    /// but deliberately NO participant, so the first session-self read must create one. Returns the seeded ids.
    /// </summary>
    private static async Task<SeededMember> SeedWorkspaceMemberAsync(WorkspaceApiFactory factory, string subject)
    {
        var result = default(SeededMember);
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, caller.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);

            result = new SeededMember(org.Id, ws.Id, session.Id, caller.Id);
        });

        return result;
    }

    /// <summary>Counts the participants persisted in the given workspace (used to assert fail-closed / idempotent).</summary>
    private static async Task<int> CountParticipantsAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
        {
            count = await db.Participants.CountAsync(participant => participant.WorkspaceId == workspaceId);
        });
        return count;
    }

    private readonly record struct SeededMember(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid CallerUserId);

    private sealed record SelfContextDto(
        Guid SessionId,
        Guid ParticipantId,
        string DisplayName,
        bool Present);

    /// <summary>A lenient view of the participant visible-feed response carrying only the echoed participant id.</summary>
    private sealed record FeedDto(Guid ParticipantId);

    /// <summary>A lenient view of a roster response carrying the audience entry's id, presence and isSelf.</summary>
    private sealed record RosterDto(Guid SessionId, IReadOnlyList<RosterParticipantDto> Participants);

    private sealed record RosterParticipantDto(
        Guid ParticipantId,
        string DisplayName,
        bool Present,
        bool IsSelf);
}
