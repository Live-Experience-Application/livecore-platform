using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Templates;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Templates;

/// <summary>
/// Tests for the <see cref="TemplateEntityTypeLoader"/> (CORE-ENT-004, the headline "template-loaded
/// entity types" behavior). They run the loader against a REAL EF Core-backed
/// <see cref="EntityTypeRepository"/> over in-memory SQLite (foreign keys enforced), so the load
/// genuinely creates <c>entity_types</c> rows and the per-workspace key uniqueness genuinely skips
/// duplicates.
///
/// They prove:
/// <list type="bullet">
///   <item>LOADING creates the expected EntityType rows in the target workspace, from the template's
///   entityTypes definitions, with the canonical keys/names/schemas (a 2- and 3-type template both
///   load generically — no per-type branching).</item>
///   <item>DUPLICATE-IN-WORKSPACE is skipped and reported, not fatal: the rest of the template still
///   loads (partial-load-with-report).</item>
///   <item>TENANT SECURITY, BOTH DIRECTIONS (threat T5 in docs/07_SECURITY_THREAT_MODEL.md): an
///   org-A template CANNOT be loaded into an org-B workspace (denied, nothing created); a GLOBAL
///   template CAN be loaded into any organization's workspace.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): every definition/key/name here is GENERIC and NEUTRAL
/// ("sample.template", "type-alpha", "type-beta", "profile", {"fields":[]}); the loader iterates
/// the definitions generically and never branches on a key (AGENTS.md, csv/forbidden_core_terms.csv).
///
/// "visibility tests": the story's required_tests names "Template validation tests and visibility
/// tests". Entity/type VISIBILITY is the later CORE-ENT-005 / Visibility epic; the template registry
/// and the loader expose NO visibility surface (no audience calc, no participant projection). The
/// applicable security coverage for this story is the template-validation tests
/// (<see cref="TemplateDefinitionValidatorTests"/>) and the TENANT-ISOLATION tests here and in
/// <see cref="TemplateRepositoryTests"/>, which is what these cover.
/// </summary>
public sealed class TemplateEntityTypeLoaderTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlug = "summer-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _loadedAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    // A two-type template definition. All keys/names/schemas are generic and neutral.
    private const string _twoTypeDefinition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } },
            { "key": "type-beta", "displayName": "Type Beta", "attributeSchema": { "fields": [ { "name": "label" } ] } }
          ]
        }
        """;

    // A three-type template definition (proves generic iteration scales without per-type branching).
    private const string _threeTypeDefinition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": {} },
            { "key": "type-beta", "displayName": "Type Beta", "attributeSchema": {} },
            { "key": "profile", "displayName": "Profile", "attributeSchema": {} }
          ]
        }
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _timeProvider;

    public TemplateEntityTypeLoaderTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;
        _timeProvider = new FixedTimeProvider(_loadedAt);

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private TemplateEntityTypeLoader CreateLoader(LiveCoreDbContext context)
        => new(new EntityTypeRepository(context), _timeProvider);

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            WorkspaceAddResult.Added,
            await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<IReadOnlyList<EntityType>> ListTypesAsync(Guid organizationId, Guid workspaceId)
    {
        await using var context = CreateContext();
        return await new EntityTypeRepository(context)
            .ListByWorkspaceAsync(organizationId, workspaceId, CancellationToken.None);
    }

    // --- Loading creates the entity types --------------------------------------

    [Fact]
    public async Task Loading_a_global_template_creates_its_entity_types_in_the_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlug);
        var template = Template.CreateGlobal("sample.template", 1, _twoTypeDefinition, _createdAt);

        TemplateLoadResult result;
        await using (var context = CreateContext())
        {
            result = await CreateLoader(context).LoadAsync(
                template, organization.Id, workspace.Id, CancellationToken.None);
        }

        Assert.True(result.Granted);
        Assert.Null(result.DenialReason);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.All(result.Outcomes, outcome => Assert.Equal(TemplateEntityTypeOutcome.Created, outcome.Outcome));

        // The entity_types rows really exist in the workspace, with the template's keys, names and
        // schemas (created at the loader's clock time).
        var types = await ListTypesAsync(organization.Id, workspace.Id);
        Assert.Equal(new[] { "type-alpha", "type-beta" }, types.Select(entityType => entityType.TypeKey).ToArray());

        var alpha = types.Single(entityType => entityType.TypeKey == "type-alpha");
        Assert.Equal("Type Alpha", alpha.DisplayName);
        Assert.Equal(_loadedAt, alpha.CreatedAt);
        Assert.Equal(organization.Id, alpha.OrganizationId);
        Assert.Equal(workspace.Id, alpha.WorkspaceId);
    }

    [Fact]
    public async Task Loading_a_three_type_template_loads_all_three_generically()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlug);
        var template = Template.CreateForOrganization(
            organization.Id, "sample.template", 1, _threeTypeDefinition, _createdAt);

        TemplateLoadResult result;
        await using (var context = CreateContext())
        {
            result = await CreateLoader(context).LoadAsync(
                template, organization.Id, workspace.Id, CancellationToken.None);
        }

        Assert.True(result.Granted);
        Assert.Equal(3, result.CreatedCount);

        var types = await ListTypesAsync(organization.Id, workspace.Id);
        Assert.Equal(
            new[] { "profile", "type-alpha", "type-beta" },
            types.Select(entityType => entityType.TypeKey).ToArray());
    }

    // --- Duplicate-in-workspace is skipped and reported ------------------------

    [Fact]
    public async Task A_type_already_in_the_workspace_is_skipped_and_the_rest_are_created()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlug);

        // Pre-seed one of the template's types directly into the workspace.
        var existing = EntityType.Create(
            organization.Id, workspace.Id, "type-alpha", "Pre-existing Alpha", "{}", _createdAt);
        await using (var context = CreateContext())
        {
            Assert.Equal(
                EntityTypeAddResult.Added,
                await new EntityTypeRepository(context).AddAsync(existing, CancellationToken.None));
        }

        var template = Template.CreateGlobal("sample.template", 1, _twoTypeDefinition, _createdAt);

        TemplateLoadResult result;
        await using (var context = CreateContext())
        {
            result = await CreateLoader(context).LoadAsync(
                template, organization.Id, workspace.Id, CancellationToken.None);
        }

        // The load did NOT fail; type-alpha was skipped, type-beta was created.
        Assert.True(result.Granted);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(
            TemplateEntityTypeOutcome.SkippedDuplicate,
            result.Outcomes.Single(outcome => outcome.Key == "type-alpha").Outcome);
        Assert.Equal(
            TemplateEntityTypeOutcome.Created,
            result.Outcomes.Single(outcome => outcome.Key == "type-beta").Outcome);

        // The pre-existing type was NOT overwritten (its display name is the original).
        var types = await ListTypesAsync(organization.Id, workspace.Id);
        Assert.Equal(2, types.Count);
        Assert.Equal("Pre-existing Alpha", types.Single(entityType => entityType.TypeKey == "type-alpha").DisplayName);
    }

    [Fact]
    public async Task Re_loading_the_same_template_is_idempotent_and_creates_nothing_new()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlug);
        var template = Template.CreateGlobal("sample.template", 1, _twoTypeDefinition, _createdAt);

        await using (var context = CreateContext())
        {
            await CreateLoader(context).LoadAsync(template, organization.Id, workspace.Id, CancellationToken.None);
        }

        TemplateLoadResult second;
        await using (var context = CreateContext())
        {
            second = await CreateLoader(context).LoadAsync(
                template, organization.Id, workspace.Id, CancellationToken.None);
        }

        // The second load skips both (partial-load-with-report converges to nothing new).
        Assert.True(second.Granted);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.Equal(2, (await ListTypesAsync(organization.Id, workspace.Id)).Count);
    }

    // --- TENANT SECURITY, both directions (threat T5) --------------------------

    [Fact]
    public async Task An_org_A_template_cannot_be_loaded_into_an_org_B_workspace()
    {
        // The critical isolation control: an organization-scoped template targeted at a DIFFERENT
        // organization's workspace is DENIED, and NOTHING is created (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlug);

        var orgATemplate = Template.CreateForOrganization(
            organizationA.Id, "sample.template", 1, _twoTypeDefinition, _createdAt);

        TemplateLoadResult result;
        await using (var context = CreateContext())
        {
            result = await CreateLoader(context).LoadAsync(
                orgATemplate, organizationB.Id, workspaceInB.Id, CancellationToken.None);
        }

        Assert.False(result.Granted);
        Assert.Equal(TemplateLoadDenialReason.ScopeMismatch, result.DenialReason);
        Assert.Empty(result.Outcomes);

        // Nothing was created in org B's workspace.
        Assert.Empty(await ListTypesAsync(organizationB.Id, workspaceInB.Id));
    }

    [Fact]
    public async Task An_org_template_loads_into_its_own_organizations_workspace()
    {
        // The other direction: an org template DOES load into a workspace of its own organization.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlug);
        var template = Template.CreateForOrganization(
            organization.Id, "sample.template", 1, _twoTypeDefinition, _createdAt);

        TemplateLoadResult result;
        await using (var context = CreateContext())
        {
            result = await CreateLoader(context).LoadAsync(
                template, organization.Id, workspace.Id, CancellationToken.None);
        }

        Assert.True(result.Granted);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(2, (await ListTypesAsync(organization.Id, workspace.Id)).Count);
    }

    [Fact]
    public async Task A_global_template_loads_into_any_organizations_workspace()
    {
        // A GLOBAL template loads into BOTH organizations' workspaces (threat T5: global is allowed
        // everywhere; this is the permissive half of the isolation control).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlug);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlug);

        var global = Template.CreateGlobal("sample.template", 1, _twoTypeDefinition, _createdAt);

        TemplateLoadResult resultA;
        TemplateLoadResult resultB;
        await using (var context = CreateContext())
        {
            resultA = await CreateLoader(context).LoadAsync(
                global, organizationA.Id, workspaceInA.Id, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            resultB = await CreateLoader(context).LoadAsync(
                global, organizationB.Id, workspaceInB.Id, CancellationToken.None);
        }

        Assert.True(resultA.Granted);
        Assert.True(resultB.Granted);
        Assert.Equal(2, resultA.CreatedCount);
        Assert.Equal(2, resultB.CreatedCount);
        Assert.Equal(2, (await ListTypesAsync(organizationA.Id, workspaceInA.Id)).Count);
        Assert.Equal(2, (await ListTypesAsync(organizationB.Id, workspaceInB.Id)).Count);
    }

    // --- Argument guards -------------------------------------------------------

    [Fact]
    public async Task LoadAsync_rejects_empty_target_ids()
    {
        var template = Template.CreateGlobal("sample.template", 1, _twoTypeDefinition, _createdAt);

        await using var context = CreateContext();
        var loader = CreateLoader(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => loader.LoadAsync(template, Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => loader.LoadAsync(template, Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the loader's created-at timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
