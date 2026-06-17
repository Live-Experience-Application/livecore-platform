// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the content block deletion route (CORE-LIFE-004, the "Resource Lifecycle and
/// Deletion" epic): <c>DELETE /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}</c>. They drive the
/// real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme +
/// EF Core SQLite, foreign keys ON), so the documented request flow (authentication -> tenant context
/// resolver -> scene resolution -> endpoint -> inline authorization -> deletion service) is exercised
/// end-to-end exactly as in production.
///
/// Coverage, per the story's required tests ("Integration + authorization negative tests"; the unit-level
/// cascade lives in <c>ContentBlockDeletionServiceTests</c>):
/// <list type="bullet">
///   <item>HAPPY PATH / "visibility rules and revisions cleaned up consistently": a host deletes a content
///   block -> 204; the content block (with its inline revision history), its visibility rules (audience-wide
///   AND selected-participant) and its asset link are all gone, while the SCENE, the linked ASSET and an
///   unrelated content block's own rule/link survive; and a <c>ContentBlockDeleted</c> audit record is
///   appended.</item>
///   <item>401 unauthenticated.</item>
///   <item>The WORKSPACE-membership role sweep: allowed {Owner, Admin, Host, CoHost} -> 204 vs denied
///   {Participant, Observer, Auditor} -> 403, every denial asserting the content block still exists (no side
///   effect) and no rationale/body leak.</item>
///   <item>"foreign-tenant 404": a real content block in org B addressed with organizationSlug = A -> 404 and
///   the content block survives.</item>
///   <item>"sibling-scene 404" (the scene-scoped lookup): a real content block in sibling scene Y addressed
///   through scene X (which the caller hosts in the same workspace) -> 404 and the content block survives.</item>
///   <item>A non-member of the scene's workspace (an org Owner who is not a workspace member) -> 404-hidden,
///   never 403.</item>
///   <item>"non-existent content block is a safe 404" (and audits nothing); 400 missing organizationSlug; 404
///   for a malformed/empty scene or content block id.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code,
/// asserts the content block is unchanged where a side effect was possible, and asserts the Problem Details
/// body leaks no existence/rationale or content body (threats T1/T5/T7).
/// </summary>
public sealed class ContentBlockDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive body so the leak assertions can prove the host-prepared content never appears in a denial
    // (threat T7).
    private const string _secretBody = "top-secret-block-body";

    /// <summary>The content-block-deletion roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> DeleteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT delete a content block (the audience and audit roles).</summary>
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
    public async Task Delete_content_block_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{Guid.CreateVersion7()}/content-blocks/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — content block deleted, dependents cascaded, unrelated
    // survive, audit appended.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_204_and_cascades_dependents_while_unrelated_survive()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid sceneId = Guid.Empty;
        Guid blockAId = Guid.Empty;
        Guid blockBId = Guid.Empty;
        Guid assetId = Guid.Empty;
        Guid audienceRuleAId = Guid.Empty;
        Guid participantRuleAId = Guid.Empty;
        Guid linkToAId = Guid.Empty;
        Guid unrelatedRuleBId = Guid.Empty;
        Guid unrelatedLinkToBId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
            var blockA = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _secretBody);
            var blockB = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "block-b body");
            blockAId = blockA.Id;
            blockBId = blockB.Id;
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);

            // BlockA dependents.
            var audienceRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockA.Id, VisibilityState.Visible);
            audienceRuleAId = audienceRule.Id;
            var participantRule = await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockA.Id, participant.Id, VisibilityState.Visible);
            participantRuleAId = participantRule.Id;
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id);
            assetId = asset.Id;
            var linkToA = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, blockA.Id, user.Id);
            linkToAId = linkToA.Id;

            // UNRELATED BlockB dependents that must survive.
            var unrelatedRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockB.Id, VisibilityState.Visible);
            unrelatedRuleBId = unrelatedRule.Id;
            var unrelatedLink = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, blockB.Id, user.Id);
            unrelatedLinkToBId = unrelatedLink.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockAId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The content block and all of its dependents are gone.
        Assert.False(await context.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == blockAId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == audienceRuleAId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == participantRuleAId));
        Assert.False(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == linkToAId));

        // The scene, the linked asset and the unrelated content block's own dependents survive.
        Assert.True(await context.Scenes.AsNoTracking().AnyAsync(s => s.Id == sceneId));
        Assert.True(await context.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == blockBId));
        Assert.True(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId));
        Assert.True(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == unrelatedRuleBId));
        Assert.True(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == unrelatedLinkToBId));

        // The deletion is audited as a single ContentBlockDeleted fact for the deleted content block.
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.ContentBlockDeleted)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(blockAId, entry.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.Content.ContentBlock), entry.ResourceType);
    }

    // =====================================================================
    // DELETE-ROLE SWEEP — allowed roles get 204.
    // =====================================================================

    [Theory]
    [MemberData(nameof(DeleteRoles))]
    public async Task Delete_content_block_is_204_for_a_delete_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _secretBody);
            blockId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertContentBlockExistsAsync(factory, blockId, exists: false);
    }

    // =====================================================================
    // NON-DELETE-ROLE SWEEP — denied roles get 403, the content block survives.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonDeleteRoles))]
    public async Task Delete_content_block_is_403_for_a_non_delete_role_and_keeps_the_content_block(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _secretBody);
            blockId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockExistsAsync(factory, blockId, exists: true);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real content block in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_404_for_a_block_in_another_tenant_and_keeps_the_block()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sceneInBId = Guid.Empty;
        Guid blockInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var sceneInB = await db.AddSceneAsync(orgB.Id, workspaceInB.Id, "B Scene", 1);
            sceneInBId = sceneInB.Id;
            var block = await db.AddContentBlockAsync(orgB.Id, workspaceInB.Id, sceneInB.Id, ContentBlockType.Text, _secretBody);
            blockInBId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneInBId}/content-blocks/{blockInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockExistsAsync(factory, blockInBId, exists: true);
    }

    // =====================================================================
    // SIBLING-SCENE 404 — a real content block in sibling scene Y addressed
    // through scene X (both in the workspace the caller hosts). The
    // scene-scoped lookup must not reach a block in another scene.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_404_when_addressed_through_a_sibling_scene_and_keeps_the_block()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid sceneXId = Guid.Empty;
        Guid blockInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var sceneX = await db.AddSceneAsync(org.Id, workspace.Id, "Scene X", 1);
            sceneXId = sceneX.Id;
            var sceneY = await db.AddSceneAsync(org.Id, workspace.Id, "Scene Y", 2);
            var blockInY = await db.AddContentBlockAsync(org.Id, workspace.Id, sceneY.Id, ContentBlockType.Text, _secretBody);
            blockInYId = blockInY.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneXId}/content-blocks/{blockInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockExistsAsync(factory, blockInYId, exists: true);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the scene's
    // workspace must not learn the content block exists.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _secretBody);
            blockId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockExistsAsync(factory, blockId, exists: true);
    }

    // =====================================================================
    // SAFE 404 — deleting a non-existent content block changes nothing and
    // audits nothing.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_404_for_an_unknown_block_in_the_callers_scene()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A safe 404 audits nothing.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.ContentBlockDeleted));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_content_block_is_404_for_a_malformed_or_empty_block_id(string blockId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_content_block_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
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
            $"/api/v1/scenes/{sceneId}/content-blocks/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Delete_content_block_is_400_without_the_organization_query_parameter_and_keeps_the_block()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            sceneId = scene.Id;
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _secretBody);
            blockId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertContentBlockExistsAsync(factory, blockId, exists: true);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertContentBlockExistsAsync(WorkspaceApiFactory factory, Guid contentBlockId, bool exists)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var found = await context.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == contentBlockId);
        Assert.Equal(exists, found);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no content-block/scene/tenant existence or
    /// authorization rationale, and never echoes the host-prepared content body (threat T7): it carries only
    /// the generic title/detail used for every denial, with no slug, role, "why" wording or content body.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_secretBody, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scene-x", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scene-y", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }
}
