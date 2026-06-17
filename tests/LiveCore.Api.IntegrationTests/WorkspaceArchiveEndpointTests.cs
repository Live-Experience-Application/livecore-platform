using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for workspace archive (CORE-LIFE-009,
/// <c>POST /api/v1/workspaces/{workspaceId}/archive</c>, csv/api_routes.csv role Owner). They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the documented request flow
/// (authentication -> tenant context resolver -> endpoint -> inline authorization -> audit) is exercised
/// end-to-end.
///
/// The story's decision is a SOFT, audited, OWNER-ONLY, terminal archive (a status on the Workspace
/// aggregate, not a hard delete). The acceptance criteria proven here:
/// <list type="bullet">
///   <item>an owner archives a workspace, it becomes read-only (rename / member invite / session create /
///   scene create are 409) and is excluded from the active list, while reads still succeed;</item>
///   <item>the archive is audited exactly once with the Active -&gt; Archived transition;</item>
///   <item>negative role/tenant: 401 for missing auth, 403 for a non-Owner org member (including Admin),
///   404 (hidden) for a cross-tenant/unknown workspace or a token/org mismatch, 400 for a missing
///   organization, 409 for an already-archived workspace.</item>
/// </list>
/// </summary>
public sealed class WorkspaceArchiveEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Archive_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: success for an org Owner; archived, audited, excluded ----------

    [Fact]
    public async Task Archive_succeeds_for_org_owner_marks_archived_audits_and_excludes_from_active_list()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid ownerProfileId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            ownerProfileId = owner.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // The owner is also a workspace member, so the workspace would appear in their active list and is
            // readable by id (used to prove the exclusion and the still-readable behavior below).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);

        // Before: the active list includes the workspace.
        var beforeList = await ReadWorkspacesAsync(
            await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}"));
        Assert.Contains(beforeList, w => w.Id == workspaceId);

        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadWorkspaceAsync(response);
        Assert.Equal(workspaceId, body.Id);
        Assert.Equal(nameof(WorkspaceStatus.Archived), body.Status);

        // The persisted workspace is archived.
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Archived);

        // After: the workspace is excluded from the active list...
        var afterList = await ReadWorkspacesAsync(
            await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}"));
        Assert.DoesNotContain(afterList, w => w.Id == workspaceId);

        // ...but is still readable by id (read-only, not hidden), carrying the new status.
        var readByIdResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, readByIdResponse.StatusCode);
        var readByIdBody = await ReadWorkspaceAsync(readByIdResponse);
        Assert.Equal(nameof(WorkspaceStatus.Archived), readByIdBody.Status);

        // The archive is audited exactly once, capturing the actor, the workspace and the Active -> Archived
        // status transition.
        var audit = Assert.Single(await ListWorkspaceArchivedAsync(factory));
        Assert.Equal(AuditAction.WorkspaceArchived, audit.Action);
        Assert.Equal(ownerProfileId, audit.ActorUserProfileId);
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(nameof(Workspace), audit.ResourceType);
        Assert.Equal(workspaceId, audit.ResourceId);
        Assert.Equal(nameof(WorkspaceStatus.Active), audit.PreviousState);
        Assert.Equal(nameof(WorkspaceStatus.Archived), audit.NewState);
        Assert.Null(audit.TargetParticipantId);
    }

    // ---- 403: authenticated org member who is not an Owner (Owner-only) ------

    [Theory]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Archive_is_403_for_an_org_member_that_is_not_owner(MembershipRole callerRole)
    {
        // Owner-only: even an Admin (the matrix's "optional" slot, denied by Core's fail-closed default) is 403.
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The workspace is untouched and nothing is audited.
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Active);
        Assert.Empty(await ListWorkspaceArchivedAsync(factory));
    }

    // ---- 404 hidden: cross-tenant / unknown / token mismatch (T1/T5) --------

    [Fact]
    public async Task Archive_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace exists in org B. Hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, owner.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceInBId}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceStatusAsync(factory, workspaceInBId, WorkspaceStatus.Active);
        Assert.Empty(await ListWorkspaceArchivedAsync(factory));
    }

    [Fact]
    public async Task Archive_is_404_for_an_unknown_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Archive_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller IS an Owner of org A and names org A, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Active);
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task Archive_is_400_when_the_organization_is_missing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/archive", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Active);
    }

    // ---- 409: archive is terminal -------------------------------------------

    [Fact]
    public async Task Archive_is_409_for_an_already_archived_workspace_and_changes_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Still archived, and the rejected second archive wrote no duplicate audit fact.
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Archived);
        Assert.Empty(await ListWorkspaceArchivedAsync(factory));
    }

    // ---- Read-only: an archived workspace rejects authoring mutations -------

    [Fact]
    public async Task Rename_is_409_for_an_archived_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}",
            new { organizationSlug = _orgA, name = "Renamed Show" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The rename did not take effect.
        await AssertWorkspaceNameAsync(factory, workspaceId, "Summer Show");
    }

    [Fact]
    public async Task Invite_member_is_409_for_an_archived_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new { organizationSlug = _orgA, email = "invitee@example.test", role = nameof(MembershipRole.Host) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // No invitation was created.
        Assert.Equal(0, await CountInvitationsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Create_session_is_409_for_an_archived_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            // A workspace membership with a session-control role, so the request reaches the archived guard
            // (which sits AFTER role authorization) rather than being denied at the role check.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new { organizationSlug = _orgA, title = "Opening Night" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Create_scene_is_409_for_an_archived_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new { organizationSlug = _orgA, title = "Act One" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountScenesAsync(factory, workspaceId));
    }

    // ---- Still allowed when archived: member removal (revocation) -----------

    [Fact]
    public async Task Member_removal_still_succeeds_on_an_archived_workspace()
    {
        // Revoking access is a safety operation, not authoring, so it remains available on an archived
        // (read-only) workspace.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- Helpers ------------------------------------------------------------

    private static async Task<WorkspaceDto> ReadWorkspaceAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<IReadOnlyList<WorkspaceDto>> ReadWorkspacesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The list is a bounded page envelope (CORE-DX-003): read the page and return its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
        return page.Items;
    }

    private static async Task AssertWorkspaceStatusAsync(
        WorkspaceApiFactory factory,
        Guid workspaceId,
        WorkspaceStatus expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var workspace = await context.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
        Assert.Equal(expected, workspace.Status);
    }

    private static async Task AssertWorkspaceNameAsync(
        WorkspaceApiFactory factory,
        Guid workspaceId,
        string expectedName)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var workspace = await context.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
        Assert.Equal(expectedName, workspace.Name);
    }

    private static async Task<int> CountInvitationsAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.WorkspaceInvitations.AsNoTracking().CountAsync(i => i.WorkspaceId == workspaceId);
    }

    private static async Task<int> CountSessionsAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Sessions.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId);
    }

    private static async Task<int> CountScenesAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Scenes.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId);
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListWorkspaceArchivedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditAction.WorkspaceArchived)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }

    private sealed record WorkspaceDto(
        Guid Id,
        Guid OrganizationId,
        string Slug,
        string Name,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
