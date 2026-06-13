using System.Net;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the entity deletion route (CORE-LIFE-003, the "Resource Lifecycle and
/// Deletion" epic): <c>DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}</c>. They drive the
/// real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme +
/// EF Core SQLite, foreign keys ON), so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> deletion service) is exercised end-to-end exactly as in
/// production.
///
/// Coverage, per the story's required tests ("Integration: delete entity, dependent edges/rules resolved,
/// negative role/tenant tests"; unit-level cascade lives in <c>EntityDeletionServiceTests</c>):
/// <list type="bullet">
///   <item>HAPPY PATH / "delete entity, dependents resolved": a host deletes an entity -> 204; the entity,
///   its edges (both directions), its visibility rules (audience-wide AND selected-participant) and its
///   asset link are all gone, while the OTHER endpoint entity, the linked ASSET and an unrelated entity's
///   own rule/link survive; and an <c>EntityDeleted</c> audit record is appended.</item>
///   <item>401 unauthenticated.</item>
///   <item>The WORKSPACE-membership role sweep: allowed {Owner, Admin, Host, CoHost} -> 204 vs denied
///   {Participant, Observer, Auditor} -> 403, every denial asserting the entity still exists (no side
///   effect) and no rationale leak.</item>
///   <item>"foreign-tenant 404": a real entity in org B addressed with organizationSlug = A -> 404 and the
///   entity survives.</item>
///   <item>"foreign-workspace 404": a real entity in sibling workspace Y addressed through workspace X
///   (which the caller hosts) -> 404 and the entity survives.</item>
///   <item>A non-member of the route's workspace (an org Owner who is not a workspace member) -> 404-hidden,
///   never 403.</item>
///   <item>"non-existent entity is a safe 404"; 400 missing organizationSlug; 404 for a malformed/empty
///   workspace or entity id.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code,
/// asserts the entity is unchanged where a side effect was possible, and asserts the Problem Details body
/// leaks no existence/rationale (threats T1/T5/T7).
/// </summary>
public sealed class EntityDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    /// <summary>The entity-deletion roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> DeleteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT delete an entity (the audience and audit roles).</summary>
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
    public async Task Delete_entity_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entities/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — entity deleted, dependents cascaded, unrelated survive,
    // audit appended.
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_204_and_cascades_dependents_while_unrelated_survive()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityAId = Guid.Empty;
        Guid entityBId = Guid.Empty;
        Guid entityCId = Guid.Empty;
        Guid assetId = Guid.Empty;
        Guid edgeAToBId = Guid.Empty;
        Guid edgeBToAId = Guid.Empty;
        Guid audienceRuleAId = Guid.Empty;
        Guid participantRuleAId = Guid.Empty;
        Guid linkToAId = Guid.Empty;
        Guid unrelatedRuleCId = Guid.Empty;
        Guid unrelatedLinkToCId = Guid.Empty;
        Guid unrelatedEdgeId = Guid.Empty;
        Guid orgId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entityA = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-a");
            var entityB = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-b");
            var entityC = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-c");
            entityAId = entityA.Id;
            entityBId = entityB.Id;
            entityCId = entityC.Id;
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);

            // EntityA dependents.
            var audienceRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Entity, entityA.Id, VisibilityState.Visible);
            audienceRuleAId = audienceRule.Id;
            var participantRule = await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Entity, entityA.Id, participant.Id, VisibilityState.Visible);
            participantRuleAId = participantRule.Id;
            var edgeAToB = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, entityA.Id, entityB.Id);
            edgeAToBId = edgeAToB.Id;
            var edgeBToA = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, entityB.Id, entityA.Id);
            edgeBToAId = edgeBToA.Id;
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id);
            assetId = asset.Id;
            var linkToA = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entityA.Id, user.Id);
            linkToAId = linkToA.Id;

            // UNRELATED EntityC dependents that must survive.
            var unrelatedRule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, VisibilityResourceType.Entity, entityC.Id, VisibilityState.Visible);
            unrelatedRuleCId = unrelatedRule.Id;
            var unrelatedLink = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entityC.Id, user.Id);
            unrelatedLinkToCId = unrelatedLink.Id;
            var unrelatedEdge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, entityB.Id, entityC.Id);
            unrelatedEdgeId = unrelatedEdge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityAId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The entity and all of its dependents are gone.
        Assert.False(await context.Entities.AsNoTracking().AnyAsync(e => e.Id == entityAId));
        Assert.False(await context.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == edgeAToBId));
        Assert.False(await context.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == edgeBToAId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == audienceRuleAId));
        Assert.False(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == participantRuleAId));
        Assert.False(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == linkToAId));

        // The other endpoint entity, the linked asset and the unrelated entity's own dependents survive.
        Assert.True(await context.Entities.AsNoTracking().AnyAsync(e => e.Id == entityBId));
        Assert.True(await context.Entities.AsNoTracking().AnyAsync(e => e.Id == entityCId));
        Assert.True(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId));
        Assert.True(await context.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == unrelatedRuleCId));
        Assert.True(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == unrelatedLinkToCId));
        Assert.True(await context.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == unrelatedEdgeId));

        // The deletion is audited as a single EntityDeleted fact for the deleted entity.
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.EntityDeleted)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(entityAId, entry.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.Entities.Entity), entry.ResourceType);
    }

    // =====================================================================
    // DELETE-ROLE SWEEP — allowed roles get 204.
    // =====================================================================

    [Theory]
    [MemberData(nameof(DeleteRoles))]
    public async Task Delete_entity_is_204_for_a_delete_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-a");
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertEntityExistsAsync(factory, entityId, exists: false);
    }

    // =====================================================================
    // NON-DELETE-ROLE SWEEP — denied roles get 403, the entity survives.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonDeleteRoles))]
    public async Task Delete_entity_is_403_for_a_non_delete_role_and_keeps_the_entity(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-a");
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityExistsAsync(factory, entityId, exists: true);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real entity in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_404_for_an_entity_in_another_tenant_and_keeps_the_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid entityInBId = Guid.Empty;
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
            var type = await db.AddEntityTypeAsync(orgB.Id, workspaceInB.Id, "type-alpha");
            var entity = await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "entity-b");
            entityInBId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entities/{entityInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityExistsAsync(factory, entityInBId, exists: true);
    }

    // =====================================================================
    // FOREIGN-WORKSPACE 404 — a real entity in sibling workspace Y addressed
    // through workspace X (which the caller hosts).
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_404_when_addressed_through_a_sibling_workspace_and_keeps_the_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid entityInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-alpha");
            var entityY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "entity-y");
            entityInYId = entityY.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceXId}/entities/{entityInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityExistsAsync(factory, entityInYId, exists: true);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's
    // workspace must not learn the entity exists.
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-a");
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityExistsAsync(factory, entityId, exists: true);
    }

    // =====================================================================
    // SAFE 404 — deleting a non-existent entity changes nothing.
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_404_for_an_unknown_entity_in_the_callers_workspace()
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
            $"/api/v1/workspaces/{workspaceId}/entities/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A safe 404 audits nothing.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.EntityDeleted));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_entity_is_404_for_a_malformed_or_empty_entity_id(string entityId)
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
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Delete_entity_is_400_without_the_organization_query_parameter_and_keeps_the_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "entity-a");
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityExistsAsync(factory, entityId, exists: true);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertEntityExistsAsync(WorkspaceApiFactory factory, Guid entityId, bool exists)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var found = await context.Entities.AsNoTracking().AnyAsync(e => e.Id == entityId);
        Assert.Equal(exists, found);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no entity/tenant existence or authorization
    /// rationale (threat T7): it carries only the generic title/detail used for every denial, with no id,
    /// slug, role or "why" wording.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("workspace-x", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace-y", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }
}
