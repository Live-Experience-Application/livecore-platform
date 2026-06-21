// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the participant self-resolution endpoint (CORE-PSELF-001, the "Participant
/// Self-Identification" epic, <c>GET /api/v1/sessions/{sessionId}/me</c>, csv/api_routes.csv roles
/// "Participant (self)"). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication → tenant context resolver → endpoint → server-side principal-to-participant resolution) is
/// exercised end-to-end exactly as in production. Presence is exercised by registering a live connection in the
/// singleton <see cref="RealtimeConnectionRegistry"/> the endpoint reads — the same record the <c>SessionHub</c>
/// writes on connect — so "presence reflects active realtime connections" is a real assertion, not a mock.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5/T7):
/// <list type="bullet">
///   <item>POSITIVE: a participant resolves its OWN participantId via <c>/me</c>, and the id MATCHES the roster
///   (with the roster's <c>isSelf</c> marker true for exactly that entry); presence reflects an active realtime
///   connection.</item>
///   <item>SELF-ONLY / NO LEAK (threats T2/T7): the response carries ONLY the caller's own identity — no
///   host-only participant user link, and not a single OTHER participant's user id.</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; 400 missing organizationSlug; a malformed session id
///   is 404; a member who is NOT a participant of the session, a REMOVED participant, a foreign tenant (token
///   claim mismatch), and an unknown session are ALL hidden as 404, never 403.</item>
///   <item>ISOLATION (threat T5): one tenant's session is never resolved through another tenant's slug.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionParticipantSelfEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401 / 400 / malformed id ------------------------------------------

    [Fact]
    public async Task Resolve_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        var seeded = await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{seeded.SessionId}/me");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Resolve_with_a_malformed_or_empty_session_id_is_404(string rawSessionId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{rawSessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 200: a participant resolves its own context, matching the roster ----

    [Fact]
    public async Task A_participant_resolves_its_own_participant_id_matching_the_roster()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        var seeded = await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var meResponse = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(me);
        Assert.Equal(seeded.SessionId, me.SessionId);
        Assert.Equal(seeded.OwnParticipantId, me.ParticipantId);
        Assert.Equal("Me", me.DisplayName);
        Assert.False(me.Present); // no live connection registered yet

        // The id MATCHES the roster, and the roster marks exactly this entry isSelf.
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

    [Fact]
    public async Task The_response_never_exposes_another_participant_user_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        var seeded = await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Self-only: no host-only user-link field, and not a single OTHER participant's user id value.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("userProfileId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.OtherUserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        // The caller's OWN user id is not echoed either — the context carries only the surrogate participant id.
        Assert.DoesNotContain(seeded.OwnUserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Presence_reflects_an_active_realtime_connection()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        var seeded = await SeedSelfAsync(factory, subject);

        // Register a live participant connection for the caller in this session — the same record the SessionHub
        // writes on connect — so the registry the endpoint reads reports the caller present.
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        registry.Register(
            "conn-self",
            new RealtimeConnectionSubject(
                seeded.OrganizationId,
                seeded.WorkspaceId,
                seeded.SessionId,
                seeded.OwnUserId,
                seeded.OwnParticipantId),
            abort: () => { });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(me);
        Assert.True(me.Present);
    }

    // ---- 404 hidden: non-participant / removed / foreign tenant / unknown ----

    [Fact]
    public async Task Resolve_is_404_for_a_member_who_is_not_a_participant_of_the_session()
    {
        // The caller is an org AND workspace member (a Host) of the session's workspace but holds NO participant
        // record, so it has nothing to resolve — hidden as 404, never 403 (threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-not-participant";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
            // A different participant exists, but none linked to the caller.
            var other = await db.AddUserAsync(_issuer, $"other-{subject}");
            await db.AddParticipantAsync(org.Id, ws.Id, other.Id, "Grace");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_is_404_for_a_removed_participant()
    {
        // A soft-removed participant has left the audience and holds no standing, so its context is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "removed-participant";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
            await db.AddParticipantAsync(org.Id, ws.Id, user.Id, "Gone", removed: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is a full participant of org A and names org A in the query, but the token asserts only
        // org B, so tenant resolution denies and the context is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        var seeded = await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_is_404_for_an_unknown_session_in_an_entitled_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        await SeedSelfAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/me?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolve_never_crosses_tenants_through_this_tenants_slug()
    {
        // T5 tenant isolation: the caller is a participant in BOTH tenants. Org B holds a session the caller
        // participates in; asking for that session id scoped to org A (where it does not exist) is hidden as 404,
        // while scoping it to org B resolves the caller's own context.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-both";
        Guid sessionInB = Guid.Empty;
        Guid ownInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);

            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Participant);
            var wsA = await db.AddWorkspaceAsync(orgA.Id, "a-show", "A Show");
            await db.AddSessionAsync(orgA.Id, wsA.Id, "A Opening", SessionStatus.Live);

            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Participant);
            var wsB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            var sessionB = await db.AddSessionAsync(orgB.Id, wsB.Id, "B Opening", SessionStatus.Live);
            sessionInB = sessionB.Id;
            var own = await db.AddParticipantAsync(orgB.Id, wsB.Id, user.Id, "Me");
            ownInB = own.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var crossTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/me?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var sameTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/me?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        var me = await sameTenant.Content.ReadFromJsonAsync<SelfContextDto>(_json);
        Assert.NotNull(me);
        Assert.Equal(ownInB, me.ParticipantId);
    }

    /// <summary>
    /// Seeds, in org A, a caller that is an organization member (so tenant resolution succeeds) and a workspace
    /// member (so it can also read the roster for the cross-check), a workspace, a LIVE session, the caller's OWN
    /// participant (linked to its user) and a second, distinct participant. Returns the seeded ids.
    /// </summary>
    private static async Task<SeededSelf> SeedSelfAsync(WorkspaceApiFactory factory, string subject)
    {
        var result = default(SeededSelf);
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, caller.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);

            var own = await db.AddParticipantAsync(org.Id, ws.Id, caller.Id, "Me");
            var otherUser = await db.AddUserAsync(_issuer, $"other-{subject}");
            var other = await db.AddParticipantAsync(org.Id, ws.Id, otherUser.Id, "Grace");

            result = new SeededSelf(
                org.Id, ws.Id, session.Id, own.Id, caller.Id, other.Id, otherUser.Id);
        });

        return result;
    }

    private readonly record struct SeededSelf(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid OwnParticipantId,
        Guid OwnUserId,
        Guid OtherParticipantId,
        Guid OtherUserId);

    private sealed record SelfContextDto(
        Guid SessionId,
        Guid ParticipantId,
        string DisplayName,
        bool Present);

    /// <summary>A lenient view of a roster response carrying the audience entry's id, presence and isSelf.</summary>
    private sealed record RosterDto(Guid SessionId, IReadOnlyList<RosterParticipantDto> Participants);

    private sealed record RosterParticipantDto(
        Guid ParticipantId,
        string DisplayName,
        bool Present,
        bool IsSelf);
}
