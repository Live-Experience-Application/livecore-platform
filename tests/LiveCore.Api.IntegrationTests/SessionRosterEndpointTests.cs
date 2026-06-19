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
/// HTTP integration tests for the participant roster + presence read endpoint (CORE-PRS-002, the "Vertical
/// Adopter Consumability Completeness" epic, <c>GET /api/v1/sessions/{sessionId}/roster</c>,
/// csv/api_routes.csv roles "workspace members"). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented request flow (authentication → tenant context resolver → endpoint → object-level authorization →
/// role-based projection) is exercised end-to-end exactly as in production. Presence is exercised by registering
/// a live connection in the singleton <see cref="RealtimeConnectionRegistry"/> the endpoint reads — the same
/// record the <c>SessionHub</c> writes on connect — so "presence reflects active realtime connections" is a real
/// assertion, not a mock.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5/T2):
/// <list type="bullet">
///   <item>POSITIVE: a host (Owner/Admin/Host/CoHost) reads the FULL roster with each participant's host-only
///   user link and presence; a Participant/Observer/Auditor reading the SAME session gets only the
///   host-only-field-stripped projection (NO user link).</item>
///   <item>PRESENCE: a participant with a live realtime connection is <c>present</c>; one without is not; a
///   connection scoped to a DIFFERENT session never marks the participant present (threats T1/T5).</item>
///   <item>NO LEAK (threat T2): not a single host-only field — the participant user link — appears in the
///   participant projection (neither the field name nor any user id value).</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a foreign tenant, a token-claim mismatch, an
///   unknown session and a non-member of the session's workspace are ALL hidden as 404; a missing
///   organizationSlug is 400; a malformed session id is 404.</item>
///   <item>ISOLATION (threat T5): one tenant's roster is never returned through another tenant's slug.</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// full-view/stripped-view sets, never an ordering comparison. The stripped set includes <b>Auditor</b> because
/// the projection reuses the central <c>VisibilityRoles</c> host/audience partition (Owner/Admin/Host/CoHost
/// view host-only content; every other role does not). All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionRosterEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The host-content roles that receive the FULL roster WITH the host-only participant user link
    /// (docs/06_AUTHORIZATION_MATRIX.md "View host-only content"; VisibilityRoles.ViewsHostOnlyContent).
    /// </summary>
    public static TheoryData<MembershipRole> FullRosterRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The roles that receive only the host-only-field-stripped roster WITHOUT the participant user link: the
    /// audience roles Participant/Observer and the audit role Auditor (which the central VisibilityRoles
    /// classification does not grant host-only content).
    /// </summary>
    public static TheoryData<MembershipRole> StrippedRosterRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // ---- 401 / 400 / malformed id ------------------------------------------

    [Fact]
    public async Task Read_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seeded = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{seeded.SessionId}/roster");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Read_with_a_malformed_or_empty_session_id_is_404(string rawSessionId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{rawSessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 200: host-content roles get the FULL roster WITH the user link -----

    [Theory]
    [MemberData(nameof(FullRosterRoles))]
    public async Task Read_returns_the_full_host_roster_for_a_host_content_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seeded = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(seeded.SessionId, body.SessionId);

        // Both active participants are present in the roster, each carrying the HOST-ONLY user link.
        Assert.Equal(2, body.Participants.Count);
        var ada = body.Participants.Single(p => p.ParticipantId == seeded.Participant1Id);
        Assert.Equal("Ada", ada.DisplayName);
        Assert.Equal(seeded.Participant1UserId, ada.UserProfileId);
        var grace = body.Participants.Single(p => p.ParticipantId == seeded.Participant2Id);
        Assert.Equal("Grace", grace.DisplayName);
        Assert.Equal(seeded.Participant2UserId, grace.UserProfileId);
    }

    [Fact]
    public async Task Read_excludes_removed_participants_from_the_roster()
    {
        // A soft-removed participant has left the audience and must not appear in the roster.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        Guid activeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
            var active = await db.AddParticipantAsync(org.Id, ws.Id, userProfileId: null, displayName: "Active");
            activeId = active.Id;
            await db.AddParticipantAsync(org.Id, ws.Id, userProfileId: null, displayName: "Gone", removed: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(activeId, Assert.Single(body.Participants).ParticipantId);
    }

    // ---- 200: audience/audit roles get the STRIPPED roster WITHOUT the link -

    [Theory]
    [MemberData(nameof(StrippedRosterRoles))]
    public async Task Read_returns_only_the_stripped_roster_for_a_non_host_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seeded = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The host-only field NAME and every host-only VALUE (the participant user links) are absent.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("userProfileId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.Participant1UserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.Participant2UserId.ToString(), raw, StringComparison.OrdinalIgnoreCase);

        // The audience still sees who is in the session by display identity, with presence.
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(seeded.SessionId, body.SessionId);
        Assert.Equal(2, body.Participants.Count);
        var ada = body.Participants.Single(p => p.ParticipantId == seeded.Participant1Id);
        Assert.Equal("Ada", ada.DisplayName);
        Assert.Null(ada.UserProfileId);
    }

    // ---- presence reflects active realtime connections ----------------------

    [Fact]
    public async Task Read_marks_a_participant_with_a_live_connection_present()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seeded = await SeedAsync(factory, subject, MembershipRole.Host);

        // Register a live PARTICIPANT connection for Ada in this session — the same record the SessionHub writes
        // on connect — so the registry the endpoint reads reports her present. Grace has no connection.
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        registry.Register(
            "conn-ada",
            new RealtimeConnectionSubject(
                seeded.OrganizationId,
                seeded.WorkspaceId,
                seeded.SessionId,
                seeded.Participant1UserId,
                seeded.Participant1Id),
            abort: () => { });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(body);
        Assert.True(body.Participants.Single(p => p.ParticipantId == seeded.Participant1Id).Present);
        Assert.False(body.Participants.Single(p => p.ParticipantId == seeded.Participant2Id).Present);
    }

    [Fact]
    public async Task Presence_is_scoped_to_the_session_a_connection_to_another_session_does_not_count()
    {
        // T1/T5: a connection Ada holds to a DIFFERENT session of the same workspace must not mark her present
        // in THIS session's roster — presence is matched on the full tenant/workspace/session tuple.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seeded = await SeedAsync(factory, subject, MembershipRole.Host);

        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        registry.Register(
            "conn-other-session",
            new RealtimeConnectionSubject(
                seeded.OrganizationId,
                seeded.WorkspaceId,
                Guid.CreateVersion7(), // a different session
                seeded.Participant1UserId,
                seeded.Participant1Id),
            abort: () => { });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RosterDto>(_json);
        Assert.NotNull(body);
        Assert.False(body.Participants.Single(p => p.ParticipantId == seeded.Participant1Id).Present);
    }

    // ---- 404 hidden: non-member / foreign tenant / unknown ------------------

    [Fact]
    public async Task Read_is_404_for_a_tenant_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        // The caller is an org member (so the tenant resolves) but holds NO membership in the session's
        // workspace, so the roster — and the session's very existence — is hidden as 404, never 403 (T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var outsider = await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, outsider.Id, MembershipRole.Owner);

            // A separate workspace the outsider is NOT a member of, holding the session.
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
            await db.AddParticipantAsync(org.Id, ws.Id, userProfileId: null, displayName: "Ada");
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is a full member of org A and names org A in the query, but the token asserts only org
        // B, so tenant resolution denies and the roster is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seeded = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{seeded.SessionId}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_session_in_an_entitled_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/roster?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_never_returns_another_tenants_roster_through_this_tenants_slug()
    {
        // T5 tenant isolation: the caller is a full Host of BOTH tenants. Org B holds a session; the caller asks
        // for that session id but scoped to org A — the session is not in org A, so it is hidden as 404. Reading
        // it scoped to org B (where the caller is a member of its workspace) succeeds.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-both";
        Guid sessionInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);

            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            var wsA = await db.AddWorkspaceAsync(orgA.Id, "a-show", "A Show");
            await db.AddWorkspaceMemberAsync(orgA.Id, wsA.Id, user.Id, MembershipRole.Host);

            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var wsB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, wsB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, wsB.Id, "B Opening", SessionStatus.Live);
            sessionInB = session.Id;
            await db.AddParticipantAsync(orgB.Id, wsB.Id, userProfileId: null, displayName: "Ada");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var crossTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/roster?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var sameTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/roster?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
    }

    /// <summary>
    /// Seeds, in org A, a caller that is both an organization member (so tenant resolution succeeds) and a
    /// workspace member with <paramref name="role"/> (which drives the projection), a workspace, a LIVE session
    /// and TWO active user-linked participants (Ada, Grace). Returns the seeded ids.
    /// </summary>
    private static async Task<SeededRoster> SeedAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        var result = default(SeededRoster);
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, role);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, caller.Id, role);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Live);

            // Two distinct user-linked participants so the host roster carries concrete user links to assert
            // present (host view) and absent (stripped view).
            var adaUser = await db.AddUserAsync(_issuer, $"ada-{subject}");
            var graceUser = await db.AddUserAsync(_issuer, $"grace-{subject}");
            var ada = await db.AddParticipantAsync(org.Id, ws.Id, adaUser.Id, "Ada");
            var grace = await db.AddParticipantAsync(org.Id, ws.Id, graceUser.Id, "Grace");

            result = new SeededRoster(
                org.Id, ws.Id, session.Id, ada.Id, adaUser.Id, grace.Id, graceUser.Id);
        });

        return result;
    }

    private readonly record struct SeededRoster(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid Participant1Id,
        Guid Participant1UserId,
        Guid Participant2Id,
        Guid Participant2UserId);

    /// <summary>
    /// A lenient view of a roster response carrying BOTH projections' participant fields, all nullable where the
    /// stripped view omits them, so a test can assert which fields the server actually returned. The full host
    /// view populates <c>UserProfileId</c>; the stripped audience view omits it (deserialized as null).
    /// </summary>
    private sealed record RosterDto(Guid SessionId, IReadOnlyList<RosterParticipantDto> Participants);

    private sealed record RosterParticipantDto(
        Guid ParticipantId,
        string DisplayName,
        Guid? UserProfileId,
        bool Present);
}
