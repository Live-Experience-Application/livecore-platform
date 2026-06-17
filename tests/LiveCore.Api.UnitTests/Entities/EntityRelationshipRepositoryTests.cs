// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="EntityRelationshipRepository"/>
/// (CORE-ENT-003).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the FOUR foreign
/// keys into <c>organizations</c>/<c>workspaces</c>/<c>entities</c> (TWO cascade paths to
/// <c>entities</c>), the unique edge index and the directed round-trip are exercised on every test
/// run without any database server or Docker. The behaviors under test (scoped equality lookups, the
/// deterministic ordering, full isolation between workspaces and tenants, list-by-source /
/// list-by-entity scoping, the per-workspace unique edge, the self-loop rejection and the four FK
/// rejections) are relational semantics shared with PostgreSQL; provider-specific verification
/// happens against PostgreSQL in the deployment pipeline (livecore-deploy) and the isolation test
/// story.
///
/// At the aggregate + repository level (no HTTP endpoints — csv/api_routes.csv defines no
/// entity-relationship route; template loading and search/visibility/graph traversal are
/// CORE-ENT-004..005):
/// <list type="bullet">
///   <item>ROUND-TRIP: a directed edge (its endpoints and kind) round-trips through the
///   database.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in docs/07_SECURITY_THREAT_MODEL.md;
///   threat T1 broken object-level authorization; docs/06_AUTHORIZATION_MATRIX.md: organization
///   boundary checked before workspace boundary): an edge in workspace W1 is never returned via
///   workspace W2; an edge in organization A is never resolved via organization B (both directions);
///   ListByWorkspace / ListBySource / ListByEntity never borrow another workspace's or tenant's
///   edges; and the four foreign keys reject an edge referencing a non-existent organization,
///   workspace, source entity or target entity (four separate tests).</item>
///   <item>UNIQUENESS: the same directed edge of the same kind is rejected as a duplicate and the
///   existing row is unchanged; a different kind or the reverse edge is allowed.</item>
///   <item>SELF-LOOP: an edge from an entity to itself is rejected at the aggregate.</item>
///   <item>SAME-WORKSPACE-ENDPOINTS BOUNDARY: the simple endpoint foreign keys guarantee the
///   endpoints EXIST but NOT that they are in the edge's workspace; that coupling is the application
///   layer's responsibility (mirrors Entity/entity_type_id, ContentBlock/scene_id). One test
///   documents this precisely.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
///
/// THE TEMPLATE BOUNDARY (docs/04): all example kinds are GENERIC and NEUTRAL ("links-to",
/// "relates-to", "rel-alpha") — no vertical vocabulary appears, proving the edge is stored as
/// generic data (AGENTS.md, csv/forbidden_core_terms.csv).
///
/// "Visibility tests": entity/relationship visibility FILTERING is CORE-ENT-005 — the bare edge
/// model has no visibility surface — so the applicable coverage here is the template/structural
/// validation plus the tenant/workspace isolation negatives proven below.
/// </summary>
public sealed class EntityRelationshipRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private const string _kind = "links-to";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntityRelationshipRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step
        // still uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK
        // constraints in the model are genuinely exercised (including the TWO cascade paths to
        // entities).
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<EntityType> SeedEntityTypeAsync(Guid organizationId, Guid workspaceId, string typeKey)
    {
        var entityType = EntityType.Create(organizationId, workspaceId, typeKey, "Display", "{}", _createdAt);
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        Assert.Equal(EntityTypeAddResult.Added, await repository.AddAsync(entityType, CancellationToken.None));
        return entityType;
    }

    private async Task<Entity> SeedEntityAsync(Guid organizationId, Guid workspaceId, Guid entityTypeId, string name)
    {
        var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, "{}", _createdAt);
        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.Equal(EntityAddResult.Added, await repository.AddAsync(entity, CancellationToken.None));
        return entity;
    }

    private async Task<EntityRelationship> SeedRelationshipAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        Guid targetEntityId,
        string kind = _kind)
    {
        var relationship = EntityRelationship.Create(
            organizationId, workspaceId, sourceEntityId, targetEntityId, kind, _createdAt);
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        Assert.Equal(
            EntityRelationshipAddResult.Added,
            await repository.AddAsync(relationship, CancellationToken.None));
        return relationship;
    }

    /// <summary>
    /// Seeds an organization, a workspace, a type and two entities in that workspace, returning the
    /// ids needed to build edges. The common fixture for the edge tests.
    /// </summary>
    private async Task<(Organization Organization, Workspace Workspace, Entity Source, Entity Target)>
        SeedTwoEntitiesAsync(string organizationSlug = _organizationSlugA, string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var source = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "source");
        var target = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "target");
        return (organization, workspace, source, target);
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task Relationship_round_trips_through_the_database()
    {
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var seeded = await SeedRelationshipAsync(
            organization.Id, workspace.Id, source.Id, target.Id, "relates-to");

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(source.Id, loaded.SourceEntityId);
        Assert.Equal(target.Id, loaded.TargetEntityId);
        Assert.Equal("relates-to", loaded.RelationshipKind);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task FindById_for_a_missing_relationship_returns_null()
    {
        var (organization, workspace, _, _) = await SeedTwoEntitiesAsync();

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    // --- Listing (deterministic order) -----------------------------------------

    [Fact]
    public async Task ListByWorkspace_returns_relationships_in_deterministic_id_order()
    {
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var third = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "third");

        var first = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        var second = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, third.Id, "links-to");
        var thirdEdge = await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, third.Id, "links-to");

        var expected = new[] { first, second, thirdEdge }
            .OrderBy(relationship => relationship.Id)
            .Select(relationship => relationship.Id)
            .ToArray();

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var firstCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        var secondCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(expected, firstCall.Select(relationship => relationship.Id).ToArray());
        // Repeating the query yields the SAME order: the sequence is deterministic.
        Assert.Equal(
            firstCall.Select(relationship => relationship.Id).ToArray(),
            secondCall.Select(relationship => relationship.Id).ToArray());
    }

    [Fact]
    public async Task ListByWorkspace_for_a_workspace_with_no_relationships_is_empty()
    {
        var (organization, workspace, _, _) = await SeedTwoEntitiesAsync();

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        var relationships = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(relationships);
    }

    [Fact]
    public async Task ListBySource_returns_only_outgoing_edges_of_the_named_entity()
    {
        // The edge is directed: ListBySource returns only the OUTGOING edges (source == entity), not
        // the incoming ones.
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var outgoing1 = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        var outgoing2 = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "relates-to");
        // An incoming edge for `source` (it is the TARGET here) must NOT appear in ListBySource.
        var incoming = await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, source.Id, "links-to");

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var outgoing = await repository.ListBySourceEntityAsync(
            organization.Id, workspace.Id, source.Id, CancellationToken.None);

        Assert.Equal(2, outgoing.Count);
        Assert.Contains(outgoing, relationship => relationship.Id == outgoing1.Id);
        Assert.Contains(outgoing, relationship => relationship.Id == outgoing2.Id);
        Assert.DoesNotContain(outgoing, relationship => relationship.Id == incoming.Id);
        // Deterministic id order.
        var expected = new[] { outgoing1, outgoing2 }.OrderBy(r => r.Id).Select(r => r.Id).ToArray();
        Assert.Equal(expected, outgoing.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task ListByEntity_returns_all_touching_edges_in_both_directions()
    {
        // ListByEntity is the undirected "all edges touching the entity" listing: both the
        // outgoing edge (source == entity) and the incoming edge (target == entity) are returned.
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var other = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "other");

        var outgoing = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        var incoming = await SeedRelationshipAsync(organization.Id, workspace.Id, other.Id, source.Id, "links-to");
        // An edge that does NOT touch `source` at all.
        var unrelated = await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, other.Id, "links-to");

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var touchingEdges = await repository.ListByEntityAsync(
            organization.Id, workspace.Id, source.Id, CancellationToken.None);

        Assert.Equal(2, touchingEdges.Count);
        Assert.Contains(touchingEdges, relationship => relationship.Id == outgoing.Id);
        Assert.Contains(touchingEdges, relationship => relationship.Id == incoming.Id);
        Assert.DoesNotContain(touchingEdges, relationship => relationship.Id == unrelated.Id);
    }

    [Fact]
    public async Task ListBySource_and_ListByEntity_for_an_entity_with_no_edges_are_empty()
    {
        var (organization, workspace, source, _) = await SeedTwoEntitiesAsync();

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        Assert.Empty(await repository.ListBySourceEntityAsync(
            organization.Id, workspace.Id, source.Id, CancellationToken.None));
        Assert.Empty(await repository.ListByEntityAsync(
            organization.Id, workspace.Id, source.Id, CancellationToken.None));
    }

    // --- Uniqueness (duplicate edge rejected, existing unchanged) --------------

    [Fact]
    public async Task The_same_directed_edge_of_the_same_kind_cannot_be_created_twice()
    {
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var first = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");

        // A second edge with the same (workspace, source, target, kind) is rejected as a duplicate.
        var duplicate = EntityRelationship.Create(
            organization.Id, workspace.Id, source.Id, target.Id, "links-to", _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new EntityRelationshipRepository(context);
            Assert.Equal(
                EntityRelationshipAddResult.Duplicate,
                await repository.AddAsync(duplicate, CancellationToken.None));
        }

        // The existing row is untouched and exactly one edge exists for that tuple.
        await using (var context = CreateContext())
        {
            var repository = new EntityRelationshipRepository(context);
            var all = await repository.ListByWorkspaceAsync(
                organization.Id, workspace.Id, CancellationToken.None);
            Assert.Equal(first.Id, Assert.Single(all).Id);
        }
    }

    [Fact]
    public async Task A_different_kind_or_the_reverse_edge_is_allowed_between_the_same_pair()
    {
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");

        // Same pair, DIFFERENT kind: allowed (differs in the unique tuple).
        await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "relates-to");
        // REVERSE edge, same kind: allowed (source/target are swapped, so the tuple differs).
        await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, source.Id, "links-to");

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        var all = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task The_same_edge_coexists_in_two_different_workspaces()
    {
        // The uniqueness is per-WORKSPACE: the same logical edge may exist in two different
        // workspaces, each isolated (threat T5).
        var (organization, workspace1, source1, target1) = await SeedTwoEntitiesAsync();
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeInW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-alpha");
        var source2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id, "source");
        var target2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id, "target");

        var inW1 = await SeedRelationshipAsync(organization.Id, workspace1.Id, source1.Id, target1.Id, "links-to");
        var inW2 = await SeedRelationshipAsync(organization.Id, workspace2.Id, source2.Id, target2.Id, "links-to");

        Assert.NotEqual(inW1.Id, inW2.Id);
    }

    // --- Self-loop -------------------------------------------------------------

    [Fact]
    public async Task A_self_loop_is_rejected_at_the_aggregate()
    {
        // The chosen behavior: an edge from an entity to itself is rejected at the aggregate, so it
        // never reaches the database (distinct-endpoints invariant).
        var (organization, workspace, source, _) = await SeedTwoEntitiesAsync();

        Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            organization.Id, workspace.Id, source.Id, source.Id, "links-to", _createdAt));
    }

    // --- Empty-id guards -------------------------------------------------------

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByWorkspaceAsync(
            Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByWorkspaceAsync(
            Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListBySource_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySourceEntityAsync(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySourceEntityAsync(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySourceEntityAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByEntity_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByEntityAsync(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByEntityAsync(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByEntityAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // --- Workspace / tenant isolation negatives (both directions) --------------

    [Fact]
    public async Task A_relationship_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): an edge exists in workspace W1 only. Looking
        // it up by id under workspace W2 must return null even though both the edge and W2 exist.
        var (organization, workspace1, source, target) = await SeedTwoEntitiesAsync();
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedRelationshipAsync(organization.Id, workspace1.Id, source.Id, target.Id);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_relationship_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md:
        // organization boundary checked before workspace boundary): an edge exists in a workspace
        // owned by organization A. Looking the SAME workspace id and edge id up under organization B's
        // id must return null even though the workspace id and edge id are correct.
        var (organizationA, workspaceInA, source, target) = await SeedTwoEntitiesAsync();
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var inA = await SeedRelationshipAsync(organizationA.Id, workspaceInA.Id, source.Id, target.Id);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_workspaces_relationships()
    {
        var (organization, workspace1, source1, target1) = await SeedTwoEntitiesAsync();
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeInW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-alpha");
        var source2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id, "source");
        var target2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id, "target");

        var inW1 = await SeedRelationshipAsync(organization.Id, workspace1.Id, source1.Id, target1.Id);
        var inW2 = await SeedRelationshipAsync(organization.Id, workspace2.Id, source2.Id, target2.Id);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var w1Relationships = await repository.ListByWorkspaceAsync(
            organization.Id, workspace1.Id, CancellationToken.None);

        Assert.Equal(inW1.Id, Assert.Single(w1Relationships).Id);
        Assert.DoesNotContain(w1Relationships, relationship => relationship.Id == inW2.Id);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_tenants_relationships()
    {
        // The list is tenant-scoped: querying organization B's id for a workspace owned by
        // organization A returns nothing (threat T5).
        var (organizationA, workspaceInA, source, target) = await SeedTwoEntitiesAsync();
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        await SeedRelationshipAsync(organizationA.Id, workspaceInA.Id, source.Id, target.Id);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        var underA = await repository.ListByWorkspaceAsync(
            organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var underB = await repository.ListByWorkspaceAsync(
            organizationB.Id, workspaceInA.Id, CancellationToken.None);

        Assert.Single(underA);
        Assert.Empty(underB);
    }

    [Fact]
    public async Task ListBySource_and_ListByEntity_never_cross_the_workspace_or_tenant_boundary()
    {
        // Both by-entity reads are tenant- and workspace-scoped: the same entity id queried under a
        // foreign workspace or tenant returns nothing (threat T5).
        var (organizationA, workspace1, source, target) = await SeedTwoEntitiesAsync();
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspace2 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        await SeedRelationshipAsync(organizationA.Id, workspace1.Id, source.Id, target.Id);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        Assert.Single(await repository.ListBySourceEntityAsync(
            organizationA.Id, workspace1.Id, source.Id, CancellationToken.None));
        Assert.Single(await repository.ListByEntityAsync(
            organizationA.Id, workspace1.Id, source.Id, CancellationToken.None));

        // Wrong workspace.
        Assert.Empty(await repository.ListBySourceEntityAsync(
            organizationA.Id, workspace2.Id, source.Id, CancellationToken.None));
        Assert.Empty(await repository.ListByEntityAsync(
            organizationA.Id, workspace2.Id, source.Id, CancellationToken.None));
        // Wrong tenant.
        Assert.Empty(await repository.ListBySourceEntityAsync(
            organizationB.Id, workspace1.Id, source.Id, CancellationToken.None));
        Assert.Empty(await repository.ListByEntityAsync(
            organizationB.Id, workspace1.Id, source.Id, CancellationToken.None));
    }

    // --- Foreign-key enforcement (FOUR separate FKs) ---------------------------

    [Fact]
    public async Task A_relationship_cannot_reference_a_source_entity_that_does_not_exist()
    {
        // The source_entity_id foreign key is enforced (PRAGMA foreign_keys = ON): an edge whose
        // source entity does not exist is rejected (threat T5). The organization, workspace and target
        // are real to isolate the source FK as the failing constraint.
        var (organization, workspace, _, target) = await SeedTwoEntitiesAsync();
        var ghost = EntityRelationship.Create(
            organization.Id, workspace.Id, Guid.CreateVersion7(), target.Id, _kind, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_relationship_cannot_reference_a_target_entity_that_does_not_exist()
    {
        // The target_entity_id foreign key is enforced: an edge whose target entity does not exist is
        // rejected (threat T5). The organization, workspace and source are real to isolate the target
        // FK as the failing constraint.
        var (organization, workspace, source, _) = await SeedTwoEntitiesAsync();
        var ghost = EntityRelationship.Create(
            organization.Id, workspace.Id, source.Id, Guid.CreateVersion7(), _kind, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_relationship_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced: an edge whose workspace does not exist is rejected
        // (threat T5). The organization and both endpoints are real to isolate the workspace FK.
        var (organization, _, source, target) = await SeedTwoEntitiesAsync();
        var ghost = EntityRelationship.Create(
            organization.Id, Guid.CreateVersion7(), source.Id, target.Id, _kind, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_relationship_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: an edge whose tenant does not exist is rejected
        // even when the workspace and both endpoints exist, so the row can never carry a tenant
        // boundary that is not a real organization (threat T5).
        var (_, workspace, source, target) = await SeedTwoEntitiesAsync();
        var ghost = EntityRelationship.Create(
            Guid.CreateVersion7(), workspace.Id, source.Id, target.Id, _kind, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    // --- Cascade delete (an entity's edges go with it) -------------------------

    [Fact]
    public async Task Deleting_an_endpoint_entity_cascades_to_its_edges()
    {
        // Both endpoint FKs cascade: removing an entity removes every edge it is an endpoint of (as
        // source OR as target). This exercises the TWO cascade paths to the entities table.
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var other = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "other");

        // `source` is the SOURCE of one edge and the TARGET of another.
        await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        await SeedRelationshipAsync(organization.Id, workspace.Id, other.Id, source.Id, "links-to");
        // An edge that does not touch `source` survives.
        var survivor = await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, other.Id, "links-to");

        await using (var context = CreateContext())
        {
            var entityRepository = new EntityRepository(context);
            var loadedSource = await entityRepository.FindByIdAsync(
                organization.Id, workspace.Id, source.Id, CancellationToken.None);
            Assert.NotNull(loadedSource);
            context.Entities.Remove(loadedSource);
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await using var verifyContext = CreateContext();
        var repository = new EntityRelationshipRepository(verifyContext);
        var remaining = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        // Only the survivor edge (target -> other) remains; both edges touching `source` were cascaded
        // away.
        Assert.Equal(survivor.Id, Assert.Single(remaining).Id);
    }

    // --- Removal (CORE-LIFE-002: the edge can now be deleted) ------------------

    [Fact]
    public async Task RemoveAsync_deletes_the_edge_and_leaves_both_endpoints_intact()
    {
        // The headline CORE-LIFE-002 behavior: an edge that previously could only be added is now
        // removable. Removing it deletes exactly the one edge row and touches neither endpoint entity
        // (it is the inverse of the per-edge insert, NOT a cascade from an endpoint).
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var edge = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        // A second, unrelated edge in the same workspace must survive the removal of the first.
        var survivor = await SeedRelationshipAsync(organization.Id, workspace.Id, target.Id, source.Id, "links-to");

        await using (var context = CreateContext())
        {
            var repository = new EntityRelationshipRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, edge.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            await repository.RemoveAsync(loaded, CancellationToken.None);
        }

        await using var verifyContext = CreateContext();
        var verifyRepository = new EntityRelationshipRepository(verifyContext);

        // The removed edge is gone; the unrelated edge remains.
        Assert.Null(await verifyRepository.FindByIdAsync(
            organization.Id, workspace.Id, edge.Id, CancellationToken.None));
        var remaining = await verifyRepository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        Assert.Equal(survivor.Id, Assert.Single(remaining).Id);

        // Both endpoint entities are untouched by the edge removal.
        var entityRepository = new EntityRepository(verifyContext);
        Assert.NotNull(await entityRepository.FindByIdAsync(
            organization.Id, workspace.Id, source.Id, CancellationToken.None));
        Assert.NotNull(await entityRepository.FindByIdAsync(
            organization.Id, workspace.Id, target.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_removes_only_the_addressed_edge_between_the_same_pair()
    {
        // Two edges of DIFFERENT kinds connect the same pair. Removing one leaves the other untouched,
        // proving removal targets exactly the loaded row, not the (source, target) pair.
        var (organization, workspace, source, target) = await SeedTwoEntitiesAsync();
        var linksTo = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "links-to");
        var relatesTo = await SeedRelationshipAsync(organization.Id, workspace.Id, source.Id, target.Id, "relates-to");

        await using (var context = CreateContext())
        {
            var repository = new EntityRelationshipRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, linksTo.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            await repository.RemoveAsync(loaded, CancellationToken.None);
        }

        await using var verifyContext = CreateContext();
        var verifyRepository = new EntityRelationshipRepository(verifyContext);
        var remaining = await verifyRepository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        Assert.Equal(relatesTo.Id, Assert.Single(remaining).Id);
    }

    [Fact]
    public async Task RemoveAsync_rejects_a_null_relationship()
    {
        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.RemoveAsync(null!, CancellationToken.None));
    }

    // --- Same-workspace-endpoints boundary (mirrors Entity/entity_type_id) -----

    [Fact]
    public async Task A_simple_endpoint_fk_allows_an_endpoint_from_another_workspace_the_application_layer_must_couple()
    {
        // SAME-WORKSPACE-ENDPOINTS BOUNDARY (carry-over, deferred to the create-relationship endpoint
        // story): the source_entity_id / target_entity_id foreign keys are SIMPLE single-column FKs
        // into entities(id) — like Entity.entity_type_id and ContentBlock.scene_id. They guarantee the
        // endpoints EXIST but do NOT guarantee they are in the edge's OWN workspace. This test
        // documents exactly that: an edge stored in workspace W1 may reference an entity that lives in
        // workspace W2, and the insert is referentially VALID (the FK is satisfied because the entity
        // row exists). Preventing a cross-workspace endpoint is the responsibility of the future
        // create-relationship application flow, which resolves each endpoint via the workspace-scoped
        // IEntityRepository.FindByIdAsync(org, ws, entityId) before creating the edge. The DB FK alone
        // does NOT enforce the same-workspace coupling.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeInW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha");
        var typeInW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-alpha");
        var sourceInW1 = await SeedEntityAsync(organization.Id, workspace1.Id, typeInW1.Id, "source");
        // The TARGET lives in workspace W2.
        var targetInW2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id, "target");

        // The edge is stored in W1 but its target is an entity whose own workspace is W2. The simple FK
        // is satisfied (the entity row exists), so the insert succeeds — the DB does not couple the
        // workspaces.
        var relationship = EntityRelationship.Create(
            organization.Id, workspace1.Id, sourceInW1.Id, targetInW2.Id, "links-to", _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRelationshipRepository(context);
        Assert.Equal(
            EntityRelationshipAddResult.Added,
            await repository.AddAsync(relationship, CancellationToken.None));

        // The edge is genuinely persisted in W1, referencing a target entity whose own workspace is
        // W2 — proving the FK guarantees existence, not same-workspace membership. The same-workspace
        // coupling is the application layer's responsibility (mirrors Entity/entity_type_id).
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, relationship.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(targetInW2.Id, loaded.TargetEntityId);
        Assert.Equal(workspace1.Id, loaded.WorkspaceId);
        Assert.NotEqual(workspace1.Id, workspace2.Id);
    }
}
