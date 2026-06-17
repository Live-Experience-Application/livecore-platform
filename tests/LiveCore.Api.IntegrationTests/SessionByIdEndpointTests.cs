using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the by-session-id read (CORE-DX-003,
/// <c>GET /api/v1/sessions/{sessionId}</c>, csv/api_routes.csv roles "workspace members"). They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core
/// SQLite, foreign keys ON), so the documented request flow (authentication -> tenant context resolver ->
/// endpoint -> object-level authorization) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>POSITIVE: every workspace-member role reads the session and receives the single generic
///   role-projected <see cref="SessionResponse"/> (a session has no host-vs-participant content split, so the
///   read is membership-gated and returns the same safe shape the list does).</item>
///   <item>HIDDEN-404 (threats T1/T5): a foreign/cross-tenant session, an unknown session, a malformed/empty
///   id, and a session in a workspace the caller is NOT a member of are ALL an indistinguishable 404.</item>
///   <item>Request shape: a missing organizationSlug is 400; an unauthenticated caller is 401.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweep is an explicit enumeration, never an ordering
/// comparison.
/// </summary>
public sealed class SessionByIdEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Every workspace-member role: all may READ a session by id.</summary>
    public static TheoryData<MembershipRole> AllMemberRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    [Fact]
    public async Task Read_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllMemberRoles))]
    public async Task Read_returns_the_role_projected_session_for_any_workspace_member(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(sessionId, body.Id);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal("Opening Night", body.Title);
        Assert.Equal(nameof(SessionStatus.Prepared), body.Status);
    }

    [Fact]
    public async Task Read_is_404_for_a_session_in_another_tenant()
    {
        // T5: a real session in org B, of which the caller is a Host, addressed with organizationSlug = A.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sessionInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspace.Id, "B Session", SessionStatus.Prepared);
            sessionInB = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionInB}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        // T1: the caller is an org Owner in org A and the session is in org A, but the caller is NOT a member of
        // the session's workspace. A non-member must not learn the session exists, so 404 (not 403).
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_session()
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
            $"/api/v1/sessions/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Read_is_404_for_a_malformed_or_empty_session_id(string sessionId)
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
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/sessions/{sessionId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record SessionDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        string Title,
        string Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
