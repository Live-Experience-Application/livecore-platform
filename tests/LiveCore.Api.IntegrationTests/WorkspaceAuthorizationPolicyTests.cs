using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Systematic authorization policy matrix for the workspace HTTP routes
/// (CORE-WS-005). This is a TEST story: it proves that every documented
/// role/membership rule is enforced SERVER-SIDE, for the full set of caller
/// situations, across every workspace route. It drives the real application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication
/// scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end exactly as in production.
///
/// The matrix sources of truth are <c>csv/api_routes.csv</c> (the route +
/// authorized-role list) and <c>docs/06_AUTHORIZATION_MATRIX.md</c> (the
/// who-can-do-what rules), reconciled with the ACTUAL documented status-code
/// contract in <c>docs/08_API_CONTRACTS.md</c> (401 unauthenticated, 403
/// authenticated-but-forbidden, 404 not-found-or-intentionally-hidden — 404
/// hides existence). The encoded rules, per route:
/// <list type="bullet">
///   <item><c>GET  /api/v1/workspaces</c> (list): any of the seven membership
///   roles may call it; results are filtered to the caller's memberships. A
///   caller not entitled to the target organization (no claim, no membership,
///   unknown tenant) is hidden as 404, not 403, so tenant existence does not
///   leak (T5).</item>
///   <item><c>POST /api/v1/workspaces</c> (create): organization Owner or Admin
///   only (201); the other five roles are 403. The organization is named in the
///   body, so a caller not entitled to it is 403 (a privileged write against a
///   named tenant), not a hidden 404.</item>
///   <item><c>GET  /api/v1/workspaces/{workspaceId}</c> (read one): authorized
///   by WORKSPACE membership, not organization role. A member of that workspace
///   gets 200; a non-member (even an organization Owner) gets a hidden 404
///   (object-level authorization, T1); a cross-tenant workspace id is a hidden
///   404 (T5).</item>
///   <item><c>PUT  /api/v1/workspaces/{workspaceId}</c> (rename) and
///   <c>POST /api/v1/workspaces/{workspaceId}/members</c> (invite): organization
///   Owner or Admin only (200/201); the other five roles are 403 — BUT only once
///   the workspace is known to exist in the resolved tenant. An unknown or
///   cross-tenant workspace id is a hidden 404 BEFORE the role is consulted, so a
///   low-role caller targeting a workspace they cannot see learns nothing about
///   it (existence is hidden first; T1/T5).</item>
/// </list>
///
/// The emphasis is the NEGATIVE space: 401 on every route, the exact 403-vs-404
/// distinction, tenant isolation (T5), and object-level authorization (T1).
/// Every assertion checks the SPECIFIC status code, so a wrong-status pass (for
/// example a 403 where the contract says hidden 404, which would leak existence)
/// is caught.
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit
/// enumerations of the allowed/denied sets, never an ordering comparison.
/// </summary>
public sealed class WorkspaceAuthorizationPolicyTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _inviteEmail = "invitee@example.test";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The complete generic role set (docs/06, csv/authorization_matrix.csv).</summary>
    public static TheoryData<MembershipRole> AllRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    /// <summary>
    /// The roles that may MANAGE a workspace ("Manage workspace settings" and
    /// "Manage members" in docs/06; csv/api_routes.csv roles "Owner,Admin" for
    /// create/rename/invite). Exact set, not a ladder.
    /// </summary>
    public static TheoryData<MembershipRole> ManagerRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
    ];

    /// <summary>
    /// The roles that may NOT manage a workspace (everything except Owner/Admin).
    /// These are the 403 cases on the privileged write routes.
    /// </summary>
    public static TheoryData<MembershipRole> NonManagerRoles =>
    [
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on EVERY route (docs/08: missing/invalid auth).
    // The route group's RequireAuthorization() challenges before any handler.
    // =====================================================================

    [Fact]
    public async Task List_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}",
            new UpdateWorkspaceRequest(_orgA, "Renamed"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invite_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // GET /api/v1/workspaces (list) — EVERY membership role is allowed (200),
    // results filtered to the caller's memberships (csv/api_routes.csv:
    // "Owner,Admin,Host,CoHost,Participant,Observer,Auditor").
    // =====================================================================

    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task List_is_200_for_every_org_member_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The list is a bounded page envelope (CORE-DX-003).
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task List_is_404_when_caller_has_no_membership_in_the_target_org(MembershipRole role)
    {
        // Tenant isolation (T5): the caller is a member of org B only (in EVERY
        // role) and the token asserts both orgs, but listing org A is hidden as
        // 404 — a non-member never learns whether the tenant exists, regardless
        // of how privileged they are in their OWN tenant.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-b-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // Tenant isolation (T5): the caller IS an Owner of org A but the token
        // only asserts org B. The claim mismatch denies before any membership is
        // consulted; listing org A is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_never_leaks_another_tenants_workspaces()
    {
        // T5: an Owner of org A who is a member of A's workspace must see only
        // A's workspace, never B's, even though both exist.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceInA = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var aShow = await db.AddWorkspaceAsync(orgA.Id, "a-show", "A Show");
            workspaceInA = aShow.Id;
            await db.AddWorkspaceMemberAsync(orgA.Id, aShow.Id, user.Id, MembershipRole.Owner);
            await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The list is a bounded page envelope (CORE-DX-003): assert over its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
        var only = Assert.Single(page.Items);
        Assert.Equal(workspaceInA, only.Id);
    }

    // =====================================================================
    // POST /api/v1/workspaces (create) — Owner/Admin -> 201, the other five
    // roles -> 403 (csv/api_routes.csv roles "Owner,Admin"; docs/06 "Manage
    // workspace settings"). The org is NAMED in the body, so a non-entitled
    // tenant resolution is 403, not a hidden 404 (it is a privileged write).
    // =====================================================================

    [Theory]
    [MemberData(nameof(ManagerRoles))]
    public async Task Create_is_201_for_owner_or_admin(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NonManagerRoles))]
    public async Task Create_is_403_for_a_non_manager_org_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoWorkspaceWithSlugAsync(factory, "summer-show");
    }

    [Theory]
    [MemberData(nameof(ManagerRoles))]
    public async Task Create_is_403_when_caller_has_no_membership_in_the_named_org(MembershipRole role)
    {
        // T5: the caller is Owner/Admin of org B and the token asserts both orgs,
        // but names org A (no membership there). It is a privileged write against
        // a named org, so resolution failure is 403, never a hidden 404, and no
        // workspace is created in A.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-b-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoWorkspaceWithSlugAsync(factory, "summer-show");
    }

    [Fact]
    public async Task Create_is_403_when_the_token_org_claim_does_not_match_the_named_org()
    {
        // T5: Owner of org A naming org A, but the token only asserts org B. The
        // claim mismatch denies; a named-org write is 403.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoWorkspaceWithSlugAsync(factory, "summer-show");
    }

    // =====================================================================
    // GET /api/v1/workspaces/{id} (read one) — authorized by WORKSPACE
    // membership, not organization role (csv/api_routes.csv "workspace
    // members"). Member -> 200; non-member -> hidden 404 (T1); cross-tenant ->
    // hidden 404 (T5). This is the object-level authorization heart of T1.
    // =====================================================================

    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task Get_by_id_is_200_for_a_member_of_that_workspace_in_any_role(MembershipRole role)
    {
        // Any workspace member, in any role, may read the workspace metadata
        // ("View workspace metadata" is allowed for every role in docs/06).
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(workspaceId, body.Id);
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public async Task Get_by_id_is_404_for_an_org_member_that_is_not_a_member_of_the_workspace(MembershipRole role)
    {
        // T1 object-level authorization: the caller is an organization member in
        // ANY role (including Owner/Admin) and the workspace is in the SAME org,
        // but the caller is not a member of THAT workspace. Read-one is gated on
        // workspace membership, so this is a hidden 404, never 403 and never 200:
        // an organization Owner is NOT automatically a member of every workspace,
        // and existence is hidden from a non-member.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"outsider-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, $"insider-{role}");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // Only the insider is a member of the workspace.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: a real workspace in org B, of which the caller IS a member,
        // addressed with organizationSlug = A. The cross-tenant id is hidden as
        // 404, never 403 and never 200.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Participant);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_is_404_when_caller_is_not_entitled_to_the_target_org()
    {
        // T5: the caller is a member of org B only; addressing a workspace under
        // org A (whether or not it exists) is hidden as 404 at the org boundary,
        // before any workspace lookup.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_is_404_for_an_unknown_workspace_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // PUT /api/v1/workspaces/{id} (rename) — Owner/Admin -> 200, the other five
    // -> 403, but the workspace EXISTENCE is checked first: an unknown or
    // cross-tenant workspace is a hidden 404 BEFORE the role is consulted.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ManagerRoles))]
    public async Task Update_is_200_for_owner_or_admin(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"manager-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Old Name");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("New Name", body.Name);
    }

    [Theory]
    [MemberData(nameof(NonManagerRoles))]
    public async Task Update_is_403_for_a_non_manager_org_role_on_an_existing_workspace(MembershipRole role)
    {
        // The workspace exists in the caller's tenant, so existence is not the
        // issue: the caller is a known tenant member but lacks "Manage workspace
        // settings" standing -> 403 (authorized to know the workspace exists in
        // their tenant, but not to change it). The name must be unchanged.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Old Name");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertWorkspaceNameAsync(factory, workspaceId, "Old Name");
    }

    [Theory]
    [MemberData(nameof(NonManagerRoles))]
    public async Task Update_is_404_for_a_non_manager_targeting_a_cross_tenant_workspace(MembershipRole role)
    {
        // T1/T5 + existence-before-role: a low-role caller in org A targets a
        // real workspace in org B (addressed via org A). Existence is hidden
        // first, so the caller gets a 404, NOT a 403 — they never learn the
        // workspace exists, and the role check is never even reached.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-a-{role}";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, role);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}",
            new UpdateWorkspaceRequest(_orgA, "Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceNameAsync(factory, workspaceInBId, "B Show");
    }

    [Fact]
    public async Task Update_is_404_for_an_owner_targeting_a_cross_tenant_workspace()
    {
        // T5: even an Owner of org A cannot rename a workspace in org B; the
        // cross-tenant id is hidden as 404, never 403, and the workspace is not
        // changed.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}",
            new UpdateWorkspaceRequest(_orgA, "Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceNameAsync(factory, workspaceInBId, "B Show");
    }

    [Fact]
    public async Task Update_is_404_for_an_unknown_workspace_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: Owner of org A names org A, but the token asserts only org B. The
        // resolver denies at the org boundary, so the workspace is hidden as 404,
        // and the workspace is unchanged.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Old Name");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceNameAsync(factory, workspaceId, "Old Name");
    }

    // =====================================================================
    // POST /api/v1/workspaces/{id}/members (invite) — Owner/Admin -> 201, the
    // other five -> 403, with the SAME existence-before-role rule as PUT: an
    // unknown or cross-tenant workspace is a hidden 404 before the role check.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ManagerRoles))]
    public async Task Invite_is_201_for_owner_or_admin(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"manager-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NonManagerRoles))]
    public async Task Invite_is_403_for_a_non_manager_org_role_on_an_existing_workspace(MembershipRole role)
    {
        // The workspace exists in the caller's tenant; the caller is a known
        // tenant member but lacks "Manage members" standing -> 403, and no
        // invitation is created.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Theory]
    [MemberData(nameof(NonManagerRoles))]
    public async Task Invite_is_404_for_a_non_manager_targeting_a_cross_tenant_workspace(MembershipRole role)
    {
        // T1/T5 + existence-before-role: a low-role caller in org A targets a
        // real workspace in org B. Existence is hidden first -> 404, not 403; no
        // invitation is created.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-a-{role}";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, role);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Fact]
    public async Task Invite_is_404_for_an_owner_targeting_a_cross_tenant_workspace()
    {
        // T5: even an Owner of org A cannot invite into a workspace in org B; the
        // cross-tenant id is hidden as 404, and no invitation is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Fact]
    public async Task Invite_is_404_for_an_unknown_workspace_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Fact]
    public async Task Invite_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: Owner of org A names org A, but the token asserts only org B. The
        // resolver denies at the org boundary; the workspace is hidden as 404 and
        // no invitation is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    // =====================================================================
    // Helpers — assert the SIDE EFFECTS as well as the status, so a wrong-status
    // pass that also mutated state is caught (no leak, no write on a denial).
    // =====================================================================

    private static async Task AssertNoInvitationsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.WorkspaceInvitations.AnyAsync());
    }

    private static async Task AssertNoWorkspaceWithSlugAsync(WorkspaceApiFactory factory, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.Workspaces.AnyAsync(w => w.Slug == slug));
    }

    private static async Task AssertWorkspaceNameAsync(WorkspaceApiFactory factory, Guid workspaceId, string expectedName)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var workspace = await context.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
        Assert.Equal(expectedName, workspace.Name);
    }

    private sealed record WorkspaceDto(
        Guid Id,
        Guid OrganizationId,
        string Slug,
        string Name,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
