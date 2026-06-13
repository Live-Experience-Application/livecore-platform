using System.Net;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the entity relationship removal route (CORE-LIFE-002, the "Resource
/// Lifecycle and Deletion" epic): <c>DELETE
/// /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}</c>. They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme +
/// EF Core SQLite, foreign keys ON), so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests (unit-level removal lives in
/// <c>EntityRelationshipRepositoryTests</c>; this is the integration half):
/// <list type="bullet">
///   <item>HAPPY PATH / "edge removed": a write-role caller removes an edge -> 204 and the row is
///   gone, while an unrelated edge in the same workspace survives.</item>
///   <item>401 unauthenticated.</item>
///   <item>The WORKSPACE-membership role sweep: allowed {Owner, Admin, Host, CoHost} -> 204 vs denied
///   {Participant, Observer, Auditor} -> 403, every denial asserting the edge still exists (no side
///   effect).</item>
///   <item>"foreign-tenant 404": a real edge in org B addressed with organizationSlug = A -> 404 and
///   the edge survives.</item>
///   <item>"foreign-workspace 404": a real edge in sibling workspace Y addressed through workspace X
///   (which the caller hosts) -> 404 and the edge survives — the endpoint FKs do not couple the
///   workspaces, so the workspace-scoped lookup is what isolates them.</item>
///   <item>A non-member of the route's workspace (an org Owner who is not a workspace member) ->
///   404-hidden, never 403.</item>
///   <item>"non-existent edge is a safe 404": a write-role caller deleting an unknown id -> 404.</item>
///   <item>400 missing organizationSlug; 404 for a malformed/empty workspace or relationship id.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code,
/// asserts the edge is unchanged where a side effect was possible, and asserts the Problem Details
/// body leaks no existence/rationale (threats T1/T5/T7).
/// </summary>
public sealed class EntityRelationshipRemovalEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    /// <summary>The relationship-removal roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> RemoveRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace-member roles that may NOT remove a relationship (the audience and audit roles).
    /// These are the 403 cases.
    /// </summary>
    public static TheoryData<MembershipRole> NonRemoveRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated. RequireAuthorization() challenges before any
    // handler runs.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-relationships/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — the edge is removed (204), unrelated edges survive.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_204_and_deletes_only_the_addressed_edge()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid edgeId = Guid.Empty;
        Guid survivorId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var sourceEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, sourceEntity.Id, targetEntity.Id, "links-to");
            edgeId = edge.Id;
            // An unrelated edge (the reverse direction) in the same workspace must survive.
            var survivor = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, targetEntity.Id, sourceEntity.Id, "links-to");
            survivorId = survivor.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{edgeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertRelationshipExistsAsync(factory, edgeId, exists: false);
        await AssertRelationshipExistsAsync(factory, survivorId, exists: true);
    }

    // =====================================================================
    // WRITE-ROLE SWEEP — allowed roles get 204.
    // =====================================================================

    [Theory]
    [MemberData(nameof(RemoveRoles))]
    public async Task Remove_relationship_is_204_for_a_remove_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid edgeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var sourceEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, sourceEntity.Id, targetEntity.Id);
            edgeId = edge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{edgeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertRelationshipExistsAsync(factory, edgeId, exists: false);
    }

    // =====================================================================
    // NON-WRITE-ROLE SWEEP — denied roles get 403, the edge survives.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonRemoveRoles))]
    public async Task Remove_relationship_is_403_for_a_non_remove_role_and_keeps_the_edge(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid edgeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var sourceEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, sourceEntity.Id, targetEntity.Id);
            edgeId = edge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{edgeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        // The denial must not have removed the edge.
        await AssertRelationshipExistsAsync(factory, edgeId, exists: true);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real edge in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_404_for_an_edge_in_another_tenant_and_keeps_the_edge()
    {
        // T5: a real edge in a workspace owned by org B, owned by a caller who is a Host of that
        // workspace, but addressed with organizationSlug = A. The cross-tenant id is hidden as 404,
        // never 403, and the edge survives.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid edgeInBId = Guid.Empty;
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
            var sourceEntity = await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(orgB.Id, workspaceInB.Id, sourceEntity.Id, targetEntity.Id);
            edgeInBId = edge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entity-relationships/{edgeInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipExistsAsync(factory, edgeInBId, exists: true);
    }

    // =====================================================================
    // FOREIGN-WORKSPACE 404 — a real edge in sibling workspace Y addressed
    // through workspace X (which the caller hosts). The endpoint FKs do not
    // couple the workspaces, so the workspace-scoped lookup isolates them.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_404_when_addressed_through_a_sibling_workspace_and_keeps_the_edge()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid edgeInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            // The caller is a Host of workspace X.
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            // The edge lives in sibling workspace Y of the SAME org.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-alpha");
            var sourceY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "source");
            var targetY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspaceY.Id, sourceY.Id, targetY.Id);
            edgeInYId = edge.Id;
        });

        // Address the edge (which lives in Y) through the route's workspace X. The workspace-scoped
        // FindByIdAsync(org, X, edge) returns nothing because the edge belongs to Y -> hidden 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-relationships/{edgeInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipExistsAsync(factory, edgeInYId, exists: true);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's
    // workspace must not learn the edge exists.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid edgeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            // The caller is an Owner of the ORG but not a member of the workspace.
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var sourceEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, sourceEntity.Id, targetEntity.Id);
            edgeId = edge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{edgeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipExistsAsync(factory, edgeId, exists: true);
    }

    // =====================================================================
    // SAFE 404 — removing a non-existent edge changes nothing.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_404_for_an_unknown_edge_in_the_callers_workspace()
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
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Remove_relationship_is_404_for_a_malformed_or_empty_relationship_id(string relationshipId)
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
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Remove_relationship_is_400_without_the_organization_query_parameter_and_keeps_the_edge()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid edgeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var sourceEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            var targetEntity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            var edge = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, sourceEntity.Id, targetEntity.Id);
            edgeId = edge.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{edgeId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A 400 on the query parameter must not have removed the edge.
        await AssertRelationshipExistsAsync(factory, edgeId, exists: true);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertRelationshipExistsAsync(WorkspaceApiFactory factory, Guid relationshipId, bool exists)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var found = await context.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == relationshipId);
        Assert.Equal(exists, found);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no edge/tenant existence or authorization
    /// rationale (threat T7): it carries only the generic title/detail used for every denial, with no
    /// id, slug, role or "why" wording.
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
