using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the recap read endpoint (CORE-RCP-003, the "Vertical Authoring and Read API
/// Completeness" epic, <c>GET /api/v1/sessions/{sessionId}/recap</c>, csv/api_routes.csv roles "workspace
/// members"). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test
/// authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow (authentication →
/// tenant context resolver → endpoint → object-level authorization → role-based projection) is exercised
/// end-to-end exactly as in production.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5/T8):
/// <list type="bullet">
///   <item>POSITIVE: a generated recap is readable by Host/Owner/Admin (and CoHost/Auditor) with the FULL host
///   projection (the recap body present); a Participant/Observer reading the SAME recap gets only the
///   participant-safe summary projection (NO body).</item>
///   <item>NO LEAK (threat T2/T8): not a single host-only field — the recap body, the producer, the
///   tenant/workspace ids, the version or the produced timestamp — appears in the participant projection.</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a foreign tenant, a token-claim mismatch, an
///   unknown session, a non-member of the session's workspace, and a session with no recap yet are ALL hidden
///   as 404; a missing organizationSlug is 400; a malformed session id is 404.</item>
///   <item>ISOLATION (threat T5): one tenant's recap is never returned through another tenant's slug; the
///   most-recently-generated recap is the one returned.</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// full-view/summary-view sets, never an ordering comparison. All fixtures are generic Core vocabulary
/// (AGENTS.md).
/// </summary>
public sealed class RecapReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive recap body so a "the body never reaches the audience" assertion is unambiguous: it must
    // appear in the host view and must NOT appear anywhere in the participant view.
    private const string _recapBody = "host-only-recap-body-sentinel: opened, three scenes activated, two reveals.";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The host-content / metadata roles that receive the FULL recap view WITH the body
    /// (docs/06_AUTHORIZATION_MATRIX.md "View host-only content"; RecapProjection.ReceivesFullView).
    /// </summary>
    public static TheoryData<MembershipRole> FullViewRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Auditor,
    ];

    /// <summary>
    /// The audience roles that receive only the host-only-field-stripped summary view WITHOUT the body (a
    /// generated recap is "Participant-visible only after separate reveal", docs/09_EVENT_CATALOG.md).
    /// </summary>
    public static TheoryData<MembershipRole> SummaryViewRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    // ---- 401 / 400 / malformed id ------------------------------------------

    [Fact]
    public async Task Read_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, _, session) = await SeedRecapAsync(db, subject, MembershipRole.Host);
            sessionId = session;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}/recap");

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
            $"/api/v1/sessions/{rawSessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 200: host-content roles get the FULL view WITH the body -----------

    [Theory]
    [MemberData(nameof(FullViewRoles))]
    public async Task Read_returns_the_full_host_view_for_a_host_content_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid recapId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (org, ws, _, session) = await SeedRecapAsync(db, subject, role);
            organizationId = org;
            workspaceId = ws;
            sessionId = session;
            recapId = (await db.Recaps.SingleAsync()).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecapDto>(_json);
        Assert.NotNull(body);

        // The FULL view carries every generic field, most importantly the host-only recap BODY.
        Assert.Equal(recapId, body.Id);
        Assert.Equal(sessionId, body.SessionId);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal(_recapBody, body.Summary);
        Assert.Equal(1, body.RecapVersion);
        Assert.NotNull(body.GeneratedAt);
    }

    // ---- 200: audience roles get the STRIPPED summary view WITHOUT the body -

    [Theory]
    [MemberData(nameof(SummaryViewRoles))]
    public async Task Read_returns_only_the_stripped_summary_view_for_an_audience_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        Guid recapId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, _, session) = await SeedRecapAsync(db, subject, role);
            sessionId = session;
            recapId = (await db.Recaps.SingleAsync()).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecapDto>(_json);
        Assert.NotNull(body);

        // The audience gets only the non-sensitive identity (id + sessionId); every host-only field is absent.
        Assert.Equal(recapId, body.Id);
        Assert.Equal(sessionId, body.SessionId);
        Assert.Null(body.Summary);
        Assert.Null(body.OrganizationId);
        Assert.Null(body.WorkspaceId);
        Assert.Null(body.GeneratedByUserProfileId);
        Assert.Null(body.RecapVersion);
        Assert.Null(body.GeneratedAt);
    }

    // ---- T2/T8: the participant projection leaks NO host-only field ---------

    [Fact]
    public async Task The_participant_projection_excludes_every_host_only_field()
    {
        // A HOST-produced recap so there is a concrete producer id (a host-only field) to prove is stripped.
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        const string participantSubject = "participant-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid producerId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, hostSubject);
            var participant = await db.AddUserAsync(_issuer, participantSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            organizationId = org.Id;
            producerId = host.Id;
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, participant.Id, MembershipRole.Participant);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = ws.Id;
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, host.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, participant.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Ended);
            sessionId = session.Id;
            await db.AddRecapAsync(org.Id, ws.Id, session.Id, _recapBody, generatedByUserProfileId: host.Id);
        });

        // The HOST view contains the body and the host-only fields.
        using (var hostClient = factory.CreateClientFor(hostSubject, _issuer, _orgA))
        {
            var hostResponse = await hostClient.GetAsync(
                $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.OK, hostResponse.StatusCode);
            var hostRaw = await hostResponse.Content.ReadAsStringAsync();
            Assert.Contains(_recapBody, hostRaw, StringComparison.Ordinal);
            Assert.Contains(producerId.ToString(), hostRaw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(workspaceId.ToString(), hostRaw, StringComparison.OrdinalIgnoreCase);
        }

        // The PARTICIPANT view leaks NONE of them — neither the body nor any host-only field name or value.
        using (var participantClient = factory.CreateClientFor(participantSubject, _issuer, _orgA))
        {
            var participantResponse = await participantClient.GetAsync(
                $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.OK, participantResponse.StatusCode);
            var raw = await participantResponse.Content.ReadAsStringAsync();

            // The body and every host-only VALUE are absent.
            Assert.DoesNotContain(_recapBody, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(producerId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(organizationId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(workspaceId.ToString(), raw, StringComparison.OrdinalIgnoreCase);

            // The host-only field NAMES are absent too (the summary view is exactly {id, sessionId}).
            foreach (var forbidden in new[]
                     {
                         "summary",
                         "organizationId",
                         "workspaceId",
                         "generatedByUserProfileId",
                         "recapVersion",
                         "generatedAt",
                     })
            {
                Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---- 404 hidden: non-member / foreign tenant / unknown / no recap -------

    [Fact]
    public async Task Read_is_404_for_a_tenant_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        // The caller is an org member (so the tenant resolves) but holds NO membership in the session's
        // workspace, so the recap — and the session's very existence — is hidden as 404, never 403 (T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var outsider = await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, outsider.Id, MembershipRole.Owner);

            // A separate workspace the outsider is NOT a member of, holding the recapped session.
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Ended);
            sessionId = session.Id;
            await db.AddRecapAsync(org.Id, ws.Id, session.Id);
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is a full member of org A and names org A in the query, but the token asserts only org
        // B, so tenant resolution denies and the recap is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, _, session) = await SeedRecapAsync(db, subject, MembershipRole.Host);
            sessionId = session;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_never_returns_another_tenants_recap_through_this_tenants_slug()
    {
        // T5 tenant isolation: the caller is a full Host of BOTH tenants. Org B holds a recapped session; the
        // caller asks for that session id but scoped to org A — the session is not in org A, so it is hidden as
        // 404. Reading it scoped to org B (where the caller is a member of its workspace) succeeds.
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
            var session = await db.AddSessionAsync(orgB.Id, wsB.Id, "B Opening", SessionStatus.Ended);
            sessionInB = session.Id;
            await db.AddRecapAsync(orgB.Id, wsB.Id, session.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        // Org B's recapped session is NOT reachable through org A's slug.
        var crossTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/recap?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        // It IS reachable through its own tenant's slug.
        var sameTenant = await client.GetAsync(
            $"/api/v1/sessions/{sessionInB}/recap?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
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
            $"/api/v1/sessions/{Guid.CreateVersion7()}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_session_has_no_recap_yet()
    {
        // The session exists and the caller is a full member of its workspace, but no recap has been generated
        // yet — hidden as 404 (the recap does not exist), never an empty body.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Ended);
            sessionId = session.Id;
            // No recap seeded.
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- which recap: the most recently generated one ----------------------

    [Fact]
    public async Task Read_returns_the_most_recently_generated_recap()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        const string latestBody = "the latest recap body sentinel";
        Guid sessionId = Guid.Empty;
        Guid latestRecapId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Ended);
            sessionId = session.Id;

            // An earlier recap, then a later one (the time-ordered surrogate id determines produced order).
            await db.AddRecapAsync(
                org.Id, ws.Id, session.Id, "an earlier recap body", generatedAt: TestData.SeedTime);
            var latest = await db.AddRecapAsync(
                org.Id,
                ws.Id,
                session.Id,
                latestBody,
                generatedByUserProfileId: user.Id,
                generatedAt: TestData.SeedTime.AddMinutes(5));
            latestRecapId = latest.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/sessions/{sessionId}/recap?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecapDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(latestRecapId, body.Id);
        Assert.Equal(latestBody, body.Summary);
    }

    /// <summary>
    /// Seeds, in org A, a user that is both an organization member (so tenant resolution succeeds) and a
    /// workspace member with <paramref name="role"/> (which drives the projection), a workspace, an ended
    /// session and a SYSTEM recap (the kind the worker produces). Returns the (organizationId, workspaceId,
    /// userProfileId, sessionId).
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid WorkspaceId, Guid UserProfileId, Guid SessionId)> SeedRecapAsync(
        LiveCoreDbContext db,
        string subject,
        MembershipRole role)
    {
        var user = await db.AddUserAsync(_issuer, subject);
        var org = await db.AddOrganizationAsync(_orgA);
        await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
        await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, role);
        var session = await db.AddSessionAsync(org.Id, ws.Id, "Opening Night", SessionStatus.Ended);
        await db.AddRecapAsync(org.Id, ws.Id, session.Id, _recapBody);
        return (org.Id, ws.Id, user.Id, session.Id);
    }

    /// <summary>
    /// A lenient view of the recap response that carries BOTH projections' fields, all nullable, so a test can
    /// assert which fields the server actually returned. The full host view (<c>RecapView</c>) populates all of
    /// them; the audience summary view (<c>RecapSummaryView</c>) populates only <c>Id</c> and <c>SessionId</c>,
    /// leaving the rest absent (deserialized as null/default).
    /// </summary>
    private sealed record RecapDto(
        Guid Id,
        Guid SessionId,
        Guid? OrganizationId,
        Guid? WorkspaceId,
        Guid? GeneratedByUserProfileId,
        int? RecapVersion,
        string? Summary,
        DateTimeOffset? GeneratedAt);
}
