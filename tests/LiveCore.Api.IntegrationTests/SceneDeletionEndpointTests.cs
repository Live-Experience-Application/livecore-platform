using System.Net;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the scene deletion route (CORE-LIFE-005, the "Resource Lifecycle and Deletion"
/// epic): <c>DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId}</c>. They drive the real application
/// over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -> tenant context resolver -> workspace
/// resolution -> endpoint -> inline authorization -> deletion service) is exercised end-to-end exactly as in
/// production.
///
/// Coverage, per the story's required tests ("Integration: delete scene re-packs order and removes content,
/// negative role/tenant tests"; the unit-level cascade/re-pack lives in <c>SceneDeletionServiceTests</c>):
/// <list type="bullet">
///   <item>HAPPY PATH / "re-packs order and removes content": a host deletes a middle scene -> 204; the
///   scene, its own visibility rule, its child content block and that block's rule and asset link are all
///   gone, while the linked ASSET and the neighbouring scenes survive and the survivors re-pack to a
///   contiguous, gap-free ordering; and a <c>SceneDeleted</c> audit record is appended.</item>
///   <item>401 unauthenticated.</item>
///   <item>The WORKSPACE-membership role sweep: allowed {Owner, Admin, Host, CoHost} -> 204 vs denied
///   {Participant, Observer, Auditor} -> 403, every denial asserting the scene still exists (no side effect)
///   and no rationale/body leak.</item>
///   <item>"foreign-tenant 404": a real scene in org B addressed with organizationSlug = A -> 404 and the
///   scene survives.</item>
///   <item>A non-member of the route's workspace (an org Owner who is not a workspace member) -> 404-hidden,
///   never 403.</item>
///   <item>"non-existent scene is a safe 404" (and audits nothing); 400 missing organizationSlug; 404 for a
///   malformed/empty scene or workspace id.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code, asserts
/// the scene is unchanged where a side effect was possible, and asserts the Problem Details body leaks no
/// existence/rationale or content (threats T1/T5/T7).
/// </summary>
public sealed class SceneDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive scene title and block body so the leak assertions can prove host-prepared content never
    // appears in a denial (threat T7).
    private const string _secretTitle = "top-secret-scene-title";
    private const string _secretBody = "top-secret-block-body";

    /// <summary>The scene-deletion roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> DeleteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT delete a scene (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonDeleteRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/scenes/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — scene deleted, child content + rules cascaded, ordering
    // re-packed without gaps, unrelated rows survive, audit appended.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_is_204_and_repacks_order_and_cascades_content_while_unrelated_survive()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid scene0Id = Guid.Empty;
        Guid sceneToDeleteId = Guid.Empty;
        Guid scene2Id = Guid.Empty;
        Guid childBlockId = Guid.Empty;
        Guid assetId = Guid.Empty;
        Guid sceneRuleId = Guid.Empty;
        Guid childRuleId = Guid.Empty;
        Guid childLinkId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);

            var scene0 = await db.AddSceneAsync(org.Id, workspace.Id, "Scene Zero", 0);
            scene0Id = scene0.Id;
            var sceneToDelete = await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 1);
            sceneToDeleteId = sceneToDelete.Id;
            var scene2 = await db.AddSceneAsync(org.Id, workspace.Id, "Scene Two", 2);
            scene2Id = scene2.Id;

            // The scene's own visibility rule, a child content block with its own rule + asset link.
            var sceneRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Scene, sceneToDelete.Id, VisibilityState.Visible);
            sceneRuleId = sceneRule.Id;
            var childBlock = await db.AddContentBlockAsync(org.Id, workspace.Id, sceneToDelete.Id, ContentBlockType.Text, _secretBody);
            childBlockId = childBlock.Id;
            var childRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.ContentBlock, childBlock.Id, VisibilityState.Visible);
            childRuleId = childRule.Id;
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id);
            assetId = asset.Id;
            var childLink = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, childBlock.Id, user.Id);
            childLinkId = childLink.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneToDeleteId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The scene and all of its dependents (own rule, child block, child block's rule + link) are gone.
        Assert.False(await context.Scenes.AsNoTracking().AnyAsync(s => s.Id == sceneToDeleteId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == sceneRuleId));
        Assert.False(await context.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == childBlockId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == childRuleId));
        Assert.False(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == childLinkId));

        // The linked asset survives, and the surviving scenes re-pack to a contiguous 0,1.
        Assert.True(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId));
        var scene0 = await context.Scenes.AsNoTracking().SingleAsync(s => s.Id == scene0Id);
        var scene2 = await context.Scenes.AsNoTracking().SingleAsync(s => s.Id == scene2Id);
        Assert.Equal(0, scene0.Order);
        Assert.Equal(1, scene2.Order);

        // The deletion is audited as a single SceneDeleted fact for the deleted scene.
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.SceneDeleted)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(sceneToDeleteId, entry.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.Scenes.Scene), entry.ResourceType);
    }

    // =====================================================================
    // DELETE-ROLE SWEEP — allowed roles get 204.
    // =====================================================================

    [Theory]
    [MemberData(nameof(DeleteRoles))]
    public async Task Delete_scene_is_204_for_a_delete_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertSceneExistsAsync(factory, sceneId, exists: false);
    }

    // =====================================================================
    // NON-DELETE-ROLE SWEEP — denied roles get 403, the scene survives.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonDeleteRoles))]
    public async Task Delete_scene_is_403_for_a_non_delete_role_and_keeps_the_scene(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertSceneExistsAsync(factory, sceneId, exists: true);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real scene in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_is_404_for_a_scene_in_another_tenant_and_keeps_the_scene()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid sceneInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var sceneInB = await db.AddSceneAsync(orgB.Id, workspaceInB.Id, _secretTitle, 0);
            sceneInBId = sceneInB.Id;
        });

        // The caller holds both tenants in the token but addresses org B's scene with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceInBId}/scenes/{sceneInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertSceneExistsAsync(factory, sceneInBId, exists: true);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's
    // workspace must not learn the scene exists.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertSceneExistsAsync(factory, sceneId, exists: true);
    }

    // =====================================================================
    // SAFE 404 — deleting a non-existent scene changes nothing and audits
    // nothing.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_is_404_for_an_unknown_scene_in_the_callers_workspace_and_audits_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A safe 404 audits nothing.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.SceneDeleted));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_scene_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_scene_is_404_for_a_malformed_or_empty_workspace_id(string workspaceId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Delete_scene_is_400_without_the_organization_query_parameter_and_keeps_the_scene()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSceneExistsAsync(factory, sceneId, exists: true);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertSceneExistsAsync(WorkspaceApiFactory factory, Guid sceneId, bool exists)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var found = await context.Scenes.AsNoTracking().AnyAsync(s => s.Id == sceneId);
        Assert.Equal(exists, found);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no scene/tenant existence or authorization
    /// rationale, and never echoes the host-prepared scene title or content body (threat T7): it carries only
    /// the generic title/detail used for every denial, with no slug, role, "why" wording or content.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_secretTitle, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_secretBody, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }
}
