using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Tests for <see cref="EntitySearchService"/> (CORE-ENT-005, the last story of the "Entity System
/// and Templates" epic) — entity search within a workspace WITH VISIBILITY FILTERING. They mirror
/// the precedent of <c>SessionParticipantJoinService</c>'s tests: the service is a
/// plain decision service over the real EF Core repository, driven against an in-memory SQLite
/// database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so the tenant/workspace
/// scoping, the type filter and the deterministic ordering all run against genuinely persisted
/// state.
///
/// The story's required tests are "Template validation tests and visibility tests"; this is finally
/// the story where the VISIBILITY tests apply:
/// <list type="bullet">
///   <item>HOST view: the host-capable roles (Owner/Admin/Host/CoHost — "View host-only content" =
///   yes, docs/06_AUTHORIZATION_MATRIX.md) get every matching workspace entity.</item>
///   <item>AUDIENCE fail-closed: Participant/Observer (and the audit role Auditor, and any undefined
///   role) get the EMPTY visibility-filtered view — even when entities exist — because the
///   audience-visible subset is computed by the future Visibility engine (CORE-VIS), not here. No
///   entity existence leaks to an audience caller (threats T1/T5 in
///   docs/07_SECURITY_THREAT_MODEL.md). This mirrors the CORE-SES-005 participant-visible feed
///   skeleton.</item>
///   <item>FILTERING: the generic name substring (ordinal, case-insensitive) and the optional type
///   filter narrow the host result.</item>
///   <item>TENANT/WORKSPACE ISOLATION (threat T5; organization boundary checked before workspace
///   boundary, docs/06): a host search in one organization/workspace never returns another
///   organization's or another workspace's entities, in both directions.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every fixture name/value is GENERIC and
/// NEUTRAL ("alpha", "beta", "type-alpha"); no vertical vocabulary appears (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntitySearchServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";
    private const string _validValues = """{"label":"value"}""";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntitySearchServiceTests()
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
        // constraints in the model are genuinely exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private EntitySearchService CreateService(LiveCoreDbContext context)
        => new(new EntityRepository(context));

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

    private async Task<Entity> SeedEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name = "alpha",
        string values = _validValues)
    {
        var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, values, _createdAt);
        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.Equal(EntityAddResult.Added, await repository.AddAsync(entity, CancellationToken.None));
        return entity;
    }

    /// <summary>Seeds one organization + workspace + entity type and returns their ids.</summary>
    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid EntityTypeId)> SeedWorkspaceWithTypeAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA,
        string typeKey = "type-alpha")
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, typeKey);
        return (organization.Id, workspace.Id, entityType.Id);
    }

    // --- Host view: host-capable roles get every matching entity --------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_capable_role_gets_every_matching_entity(MembershipRole role)
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var a = await SeedEntityAsync(org, ws, typeId, "alpha");
        var b = await SeedEntityAsync(org, ws, typeId, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, role, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.True(result.IsHostView);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == a.Id);
        Assert.Contains(result.Items, e => e.Id == b.Id);
    }

    [Fact]
    public async Task Host_view_returns_entities_in_deterministic_id_order()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var first = await SeedEntityAsync(org, ws, typeId, "first");
        var second = await SeedEntityAsync(org, ws, typeId, "second");
        var third = await SeedEntityAsync(org, ws, typeId, "third");

        // UUIDv7 ids are time-ordered; sort independently so the assertion is not coupled to UUIDv7
        // monotonicity (mirrors EntityRepositoryTests).
        var expected = new[] { first, second, third }
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToArray();

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(expected, result.Items.Select(entity => entity.Id).ToArray());
    }

    // --- Audience fail-closed: no host-only content, no existence leak --------------

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Audience_and_audit_roles_get_empty_view_even_when_entities_exist(MembershipRole role)
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedEntityAsync(org, ws, typeId, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, role, EntitySearchCriteria.MatchAll, CancellationToken.None);

        // Fail-closed: the audience view is empty (the audience-visible subset is CORE-VIS), and the
        // entities that genuinely exist never leak to a non-host caller (threats T1/T5).
        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.False(result.IsHostView);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Undefined_role_fails_closed_to_empty_audience_view()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        await SeedEntityAsync(org, ws, typeId, "alpha");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, (MembershipRole)999, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Empty(result.Items);
    }

    // --- Name filter (host view) ----------------------------------------------------

    [Fact]
    public async Task Name_filter_matches_a_case_insensitive_substring()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var alpha = await SeedEntityAsync(org, ws, typeId, "alpha");
        var alphabet = await SeedEntityAsync(org, ws, typeId, "Alphabet");
        await SeedEntityAsync(org, ws, typeId, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        // "ALP" matches "alpha" and "Alphabet" case-insensitively, not "beta".
        var result = await service.SearchAsync(
            org, ws, MembershipRole.Host, EntitySearchCriteria.Create("ALP"), CancellationToken.None);

        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == alpha.Id);
        Assert.Contains(result.Items, e => e.Id == alphabet.Id);
    }

    [Fact]
    public async Task Name_filter_with_no_match_returns_an_empty_host_view()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        await SeedEntityAsync(org, ws, typeId, "alpha");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, MembershipRole.Host, EntitySearchCriteria.Create("gamma"), CancellationToken.None);

        // No match is still the HOST view (the caller is host-capable) — just empty. This is
        // distinct from the empty AUDIENCE view, which a non-host role receives regardless of matches.
        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.Empty(result.Items);
    }

    // --- Type filter (host view) ----------------------------------------------------

    [Fact]
    public async Task Type_filter_restricts_to_one_entity_type()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var alpha1 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-1");
        var alpha2 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-2");
        var beta1 = await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "beta-1");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            organization.Id,
            workspace.Id,
            MembershipRole.Owner,
            EntitySearchCriteria.Create(entityTypeId: typeAlpha.Id),
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == alpha1.Id);
        Assert.Contains(result.Items, e => e.Id == alpha2.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == beta1.Id);
    }

    [Fact]
    public async Task Type_and_name_filters_combine()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var match = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "shared-name");
        // Same name, different type -> excluded by the type filter.
        await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "shared-name");
        // Same type, different name -> excluded by the name filter.
        await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "other");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            organization.Id,
            workspace.Id,
            MembershipRole.Owner,
            EntitySearchCriteria.Create("shared", typeAlpha.Id),
            CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(match.Id, result.Items[0].Id);
    }

    // --- Tenant + workspace isolation (host view) -----------------------------------

    [Fact]
    public async Task Host_search_never_returns_another_organizations_entities()
    {
        // Tenant A with an entity, tenant B with its own entity.
        var (orgA, wsA, typeA) = await SeedWorkspaceWithTypeAsync(_organizationSlugA, _workspaceSlugA, "type-alpha");
        var entityA = await SeedEntityAsync(orgA, wsA, typeA, "alpha");
        var (orgB, wsB, typeB) = await SeedWorkspaceWithTypeAsync(_organizationSlugB, _workspaceSlugB, "type-beta");
        var entityB = await SeedEntityAsync(orgB, wsB, typeB, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        // Searching tenant A's workspace returns only A's entity, never B's — even as Owner.
        var resultA = await service.SearchAsync(
            orgA, wsA, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Single(resultA.Items);
        Assert.Equal(entityA.Id, resultA.Items[0].Id);

        // And the reverse: tenant B's workspace returns only B's entity.
        var resultB = await service.SearchAsync(
            orgB, wsB, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Single(resultB.Items);
        Assert.Equal(entityB.Id, resultB.Items[0].Id);

        // Cross-tenant addressing is hidden: tenant B's id with tenant A's workspace returns nothing
        // (the organization boundary is checked before the workspace boundary; threat T5).
        var crossTenant = await service.SearchAsync(
            orgB, wsA, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Empty(crossTenant.Items);
    }

    [Fact]
    public async Task Host_search_never_returns_another_workspaces_entities_in_the_same_organization()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceX = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceY = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeX = await SeedEntityTypeAsync(organization.Id, workspaceX.Id, "type-alpha");
        var typeY = await SeedEntityTypeAsync(organization.Id, workspaceY.Id, "type-beta");
        var entityX = await SeedEntityAsync(organization.Id, workspaceX.Id, typeX.Id, "alpha");
        var entityY = await SeedEntityAsync(organization.Id, workspaceY.Id, typeY.Id, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        var resultX = await service.SearchAsync(
            organization.Id, workspaceX.Id, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(resultX.Items);
        Assert.Equal(entityX.Id, resultX.Items[0].Id);
        Assert.DoesNotContain(resultX.Items, e => e.Id == entityY.Id);
    }

    // --- Guards ---------------------------------------------------------------------

    [Fact]
    public async Task Empty_organization_id_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            Guid.Empty, Guid.NewGuid(), MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None));
        Assert.Equal("organizationId", exception.ParamName);
    }

    [Fact]
    public async Task Empty_workspace_id_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            Guid.NewGuid(), Guid.Empty, MembershipRole.Owner, EntitySearchCriteria.MatchAll, CancellationToken.None));
        Assert.Equal("workspaceId", exception.ParamName);
    }

    [Fact]
    public async Task Null_criteria_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SearchAsync(
            Guid.NewGuid(), Guid.NewGuid(), MembershipRole.Owner, null!, CancellationToken.None));
    }
}
