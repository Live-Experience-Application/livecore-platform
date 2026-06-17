// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace create/read/update API
/// (CORE-WS-003). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, with a test authentication scheme and an
/// EF Core SQLite database, so the documented request flow (authentication ->
/// tenant context resolver -> endpoint -> inline authorization) is exercised
/// end-to-end.
///
/// The heart of the story is NEGATIVE authorization and tenant isolation:
/// <list type="bullet">
///   <item>401 for missing/invalid auth on every protected route;</item>
///   <item>403 for an authenticated organization member who is not Owner/Admin
///   on the privileged write routes (POST/PUT);</item>
///   <item>403/404 for a caller whose token claim or membership does not match
///   the target organization (T5 tenant isolation);</item>
///   <item>404 (hidden, not 403) for a non-member or cross-tenant read of a
///   workspace by id (T1 object-level authorization);</item>
///   <item>409 on a duplicate workspace slug within an organization;</item>
///   <item>filtered listing that never leaks another org's or another member's
///   workspaces.</item>
/// </list>
/// </summary>
public sealed class WorkspaceEndpointsTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401: missing / invalid auth on every protected route ---------------

    [Fact]
    public async Task List_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}",
            new UpdateWorkspaceRequest(_orgA, "Renamed"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- POST /workspaces ----------------------------------------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Create_succeeds_for_org_owner_or_admin(MembershipRole role)
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
        var body = await ReadWorkspaceAsync(response);
        Assert.Equal("summer-show", body.Slug);
        Assert.Equal("Summer Show", body.Name);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.NotEqual(Guid.Empty, body.OrganizationId);
    }

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Create_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole role)
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
    }

    [Fact]
    public async Task Create_is_403_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // Tenant isolation (T5): the caller is an Owner of org A and names org A
        // in the body, but the token only asserts org B. The resolver denies on
        // the claim mismatch before any membership is consulted.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        // Token asserts only org B, not the targeted org A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_403_when_the_caller_has_no_membership_in_the_target_org()
    {
        // Tenant isolation (T5): the caller is an Owner of org B and the token
        // asserts both orgs, but the caller has no membership in the targeted
        // org A. The authoritative grant is missing, so resolution denies.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            // Membership in B only.
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Owner);
            _ = orgA;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- CORE-SPEC-002: a quota denial writes the documented audit record ----

    [Fact]
    public async Task Create_over_quota_is_409_and_writes_a_QuotaExceeded_audit_fact()
    {
        // A workspace.active.max quota EXISTS for the User subject but the authorized Owner holds NO entitlement,
        // so the atomic consume is fail-closed denied (zero allowance) and the create is 409. CORE-SPEC-002 records
        // that denial as a real, tenant-scoped AuditAction.QuotaExceeded fact.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        var orgId = Guid.Empty;
        var userId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync("workspace.active.max");
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User, QuotaUnit.Count);
            orgId = org.Id;
            userId = user.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var entry = Assert.Single(await db.AuditLogs.AsNoTracking().ToListAsync());
            Assert.Equal(AuditAction.QuotaExceeded, entry.Action);
            Assert.Equal(orgId, entry.OrganizationId);
            Assert.Equal(userId, entry.ActorUserProfileId);
            Assert.Equal(nameof(EntitlementSubjectType.User), entry.ResourceType);
            Assert.Equal(userId, entry.ResourceId);
        });
    }

    [Fact]
    public async Task Create_by_an_unauthorized_role_writes_no_quota_audit_fact()
    {
        // NEGATIVE authorization (threat T1): a non-Owner/Admin org member is denied 403 BEFORE the quota check, so
        // an unauthorized caller never produces a QuotaExceeded audit fact — the audit is fail-closed, recorded only
        // for an authorized-but-over-quota caller.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync("workspace.active.max");
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User, QuotaUnit.Count);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Empty(await db.AuditLogs.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task Create_is_409_on_a_duplicate_slug_within_the_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            await db.AddWorkspaceAsync(org.Id, "summer-show", "Existing");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Duplicate"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("Summer Show", "")] // blank slug
    [InlineData("Summer Show", "Bad Slug!")] // invalid slug shape
    [InlineData("", "summer-show")] // blank name
    [InlineData(null, "summer-show")] // missing name
    public async Task Create_is_400_on_an_invalid_body(string? name, string? slug)
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
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, slug, name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_400_when_the_organization_is_missing_from_the_body()
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
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(null, "summer-show", "Summer Show"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- GET /workspaces (filtered list) ------------------------------------

    [Fact]
    public async Task List_returns_only_the_callers_workspaces_in_their_org()
    {
        // The caller is a Participant in org A and a member of ONE of A's two
        // workspaces. Org B has its own workspace. The listing must return only
        // the one workspace the caller is a member of: not A's other workspace,
        // and never B's (T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid memberWorkspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other-a");

            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);

            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Participant);

            var memberWorkspace = await db.AddWorkspaceAsync(orgA.Id, "member-show", "Member Show");
            var nonMemberWorkspace = await db.AddWorkspaceAsync(orgA.Id, "secret-show", "Secret Show");
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            memberWorkspaceId = memberWorkspace.Id;

            // Caller is a member of only the first workspace in A.
            await db.AddWorkspaceMemberAsync(
                orgA.Id, memberWorkspace.Id, caller.Id, MembershipRole.Participant);
            // Another user is a member of the second workspace in A.
            await db.AddWorkspaceMemberAsync(
                orgA.Id, nonMemberWorkspace.Id, other.Id, MembershipRole.Participant);
            _ = workspaceInB;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The list is a bounded page envelope (CORE-DX-003): read the page and assert over its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
        var only = Assert.Single(page.Items);
        Assert.Equal(memberWorkspaceId, only.Id);
        Assert.Equal("member-show", only.Slug);
    }

    [Fact]
    public async Task List_is_404_when_the_caller_is_not_entitled_to_the_target_org()
    {
        // The caller is a member of org B only; listing org A is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_400_without_the_organization_query_parameter()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- GET /workspaces/{id} -----------------------------------------------

    [Fact]
    public async Task Get_by_id_returns_200_for_a_workspace_member()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadWorkspaceAsync(response);
        Assert.Equal(workspaceId, body.Id);
        Assert.Equal("summer-show", body.Slug);
    }

    [Fact]
    public async Task Get_by_id_is_404_for_a_non_member_in_the_same_org()
    {
        // The caller is in org A and the workspace is in org A, but the caller is
        // not a member of THAT workspace: hidden as 404, not 403 (T1).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // Only the owner is a member of the workspace.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_is_404_for_a_workspace_in_another_tenant()
    {
        // Tenant isolation (T5): a real workspace in org B, requested by a caller
        // who is a member of it, but addressed with organizationSlug = A (the
        // caller's own org). The cross-tenant id is hidden as 404, never 403.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Participant);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        // Address B's workspace through tenant A: hidden.
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- PUT /workspaces/{id} (rename slice) --------------------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Update_renames_for_owner_or_admin_without_moving_tenant(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"manager-{role}";
        Guid workspaceId = Guid.Empty;
        Guid organizationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            organizationId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Old Name");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadWorkspaceAsync(response);
        Assert.Equal("New Name", body.Name);
        // The tenant boundary and the slug are unchanged: a rename never moves
        // the workspace (T5).
        Assert.Equal(workspaceId, body.Id);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal("summer-show", body.Slug);
    }

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Update_is_403_for_an_insufficient_org_role(MembershipRole role)
    {
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
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new UpdateWorkspaceRequest(_orgA, "New Name"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_is_404_for_a_workspace_in_another_tenant()
    {
        // Tenant isolation (T5): the caller is an Owner of org A; a workspace
        // exists in org B. Updating B's workspace through tenant A is hidden as
        // 404, never 403, and the workspace is not changed.
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

    private static async Task<WorkspaceDto> ReadWorkspaceAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private sealed record WorkspaceDto(
        Guid Id,
        Guid OrganizationId,
        string Slug,
        string Name,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
