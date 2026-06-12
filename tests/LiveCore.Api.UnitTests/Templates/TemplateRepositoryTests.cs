using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Templates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Templates;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="TemplateRepository"/> (CORE-ENT-004).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the nullable
/// <c>organization_id</c> foreign key into <c>organizations</c>, the two FILTERED partial unique
/// indexes (global vs organization scope) and the definition-JSON round-trip are exercised on every
/// run without any database server. The behaviors under test (scope-aware lookups, per-scope
/// uniqueness handling the nullable org, tenant isolation and the round-trip) are relational
/// semantics shared with PostgreSQL; provider-specific verification happens against PostgreSQL in
/// the deployment pipeline.
///
/// Mandatory negatives (AGENTS.md; docs/17_DEFINITION_OF_DONE.md; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md): a per-scope duplicate key+version is rejected; the same
/// key+version coexists across scopes (different orgs, and global-vs-org); an org template is never
/// found via another org; empty-id guards.
///
/// THE TEMPLATE BOUNDARY (docs/04): all keys/definitions are GENERIC and NEUTRAL.
/// </summary>
public sealed class TemplateRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";

    private const string _validDefinition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } }
          ]
        }
        """;

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public TemplateRepositoryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

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

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Template> SeedGlobalAsync(string templateKey, int version = 1)
    {
        var template = Template.CreateGlobal(templateKey, version, _validDefinition, _createdAt);
        await using var context = CreateContext();
        var repository = new TemplateRepository(context);
        Assert.Equal(TemplateAddResult.Added, await repository.AddAsync(template, CancellationToken.None));
        return template;
    }

    private async Task<Template> SeedForOrganizationAsync(Guid organizationId, string templateKey, int version = 1)
    {
        var template = Template.CreateForOrganization(organizationId, templateKey, version, _validDefinition, _createdAt);
        await using var context = CreateContext();
        var repository = new TemplateRepository(context);
        Assert.Equal(TemplateAddResult.Added, await repository.AddAsync(template, CancellationToken.None));
        return template;
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task A_global_template_round_trips_with_a_null_organization_and_its_definition()
    {
        var seeded = await SeedGlobalAsync("template-alpha", 2);

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);
        var loaded = await repository.FindGlobalByIdAsync(seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Null(loaded.OrganizationId);
        Assert.True(loaded.IsGlobal);
        Assert.Equal("template-alpha", loaded.TemplateKey);
        Assert.Equal(2, loaded.Version);
        // The definition JSON survives the round-trip verbatim.
        Assert.Equal(_validDefinition, loaded.Definition);
    }

    [Fact]
    public async Task An_org_template_round_trips_with_its_organization()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var seeded = await SeedForOrganizationAsync(organization.Id, "template-alpha");

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);
        var loaded = await repository.FindByOrganizationAndIdAsync(
            organization.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.False(loaded.IsGlobal);
    }

    [Fact]
    public async Task FindByKey_resolves_a_template_by_its_canonical_key_and_version()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var seeded = await SeedForOrganizationAsync(organization.Id, "sample.template", 3);
        await SeedGlobalAsync("sample.template", 3);

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        // Re-cased/padded key still resolves; the org and global pools resolve independently.
        var org = await repository.FindByOrganizationAndKeyAsync(
            organization.Id, "  SAMPLE.TEMPLATE  ", 3, CancellationToken.None);
        var global = await repository.FindGlobalByKeyAsync("sample.template", 3, CancellationToken.None);

        Assert.NotNull(org);
        Assert.Equal(seeded.Id, org.Id);
        Assert.NotNull(global);
        Assert.NotEqual(seeded.Id, global.Id);
    }

    // --- Per-scope uniqueness (handling the nullable org) ----------------------

    [Fact]
    public async Task A_duplicate_global_key_and_version_is_rejected()
    {
        // Two GLOBAL templates cannot share a key+version. This proves the FILTERED unique index
        // works even though SQLite treats NULL organization_id as distinct in a plain unique index.
        var first = await SeedGlobalAsync("template-alpha", 1);

        var duplicate = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new TemplateRepository(context);
            Assert.Equal(TemplateAddResult.Duplicate, await repository.AddAsync(duplicate, CancellationToken.None));
        }

        // Exactly one global template with that key+version exists, and it is the first writer's.
        await using (var context = CreateContext())
        {
            var repository = new TemplateRepository(context);
            var loaded = await repository.FindGlobalByKeyAsync("template-alpha", 1, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(first.Id, loaded.Id);
            Assert.Single(await repository.ListGlobalAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_duplicate_org_key_and_version_is_rejected()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var first = await SeedForOrganizationAsync(organization.Id, "template-alpha", 1);

        var duplicate = Template.CreateForOrganization(organization.Id, "template-alpha", 1, _validDefinition, _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new TemplateRepository(context);
            Assert.Equal(TemplateAddResult.Duplicate, await repository.AddAsync(duplicate, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var repository = new TemplateRepository(context);
            var loaded = await repository.FindByOrganizationAndKeyAsync(
                organization.Id, "template-alpha", 1, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(first.Id, loaded.Id);
            Assert.Single(await repository.ListByOrganizationAsync(organization.Id, CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_same_key_and_version_is_allowed_in_a_different_organization()
    {
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);

        var inA = await SeedForOrganizationAsync(organizationA.Id, "template-alpha", 1);
        var inB = await SeedForOrganizationAsync(organizationB.Id, "template-alpha", 1);

        Assert.NotEqual(inA.Id, inB.Id);
    }

    [Fact]
    public async Task The_same_key_and_version_is_allowed_global_vs_org()
    {
        // A global template and an org template may share a key+version: they are different scopes.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var global = await SeedGlobalAsync("template-alpha", 1);
        var orgScoped = await SeedForOrganizationAsync(organization.Id, "template-alpha", 1);

        Assert.NotEqual(global.Id, orgScoped.Id);
    }

    [Fact]
    public async Task The_same_key_at_a_different_version_coexists_in_one_scope()
    {
        // Versioning: the same key may exist at several versions within one scope.
        var v1 = await SeedGlobalAsync("template-alpha", 1);
        var v2 = await SeedGlobalAsync("template-alpha", 2);

        Assert.NotEqual(v1.Id, v2.Id);

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);
        var all = await repository.ListGlobalAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, all.Select(template => template.Version).ToArray());
    }

    // --- Tenant isolation negatives --------------------------------------------

    [Fact]
    public async Task An_org_template_is_never_found_via_another_organization()
    {
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var inA = await SeedForOrganizationAsync(organizationA.Id, "template-alpha");

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        Assert.NotNull(await repository.FindByOrganizationAndIdAsync(organizationA.Id, inA.Id, CancellationToken.None));
        // Under organization B's id: not found, even with the correct template id.
        Assert.Null(await repository.FindByOrganizationAndIdAsync(organizationB.Id, inA.Id, CancellationToken.None));
        // Via the GLOBAL path: an org template is never a global template.
        Assert.Null(await repository.FindGlobalByIdAsync(inA.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_global_template_is_never_found_via_an_organization_path()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var global = await SeedGlobalAsync("template-alpha");

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        Assert.NotNull(await repository.FindGlobalByIdAsync(global.Id, CancellationToken.None));
        Assert.Null(await repository.FindByOrganizationAndIdAsync(organization.Id, global.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListByOrganization_never_returns_another_tenants_or_global_templates()
    {
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var inA = await SeedForOrganizationAsync(organizationA.Id, "template-alpha");
        await SeedForOrganizationAsync(organizationB.Id, "template-beta");
        await SeedGlobalAsync("template-gamma");

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        var underA = await repository.ListByOrganizationAsync(organizationA.Id, CancellationToken.None);

        Assert.Equal(inA.Id, Assert.Single(underA).Id);
    }

    [Fact]
    public async Task ListGlobal_returns_only_global_templates()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        await SeedForOrganizationAsync(organization.Id, "template-alpha");
        var global = await SeedGlobalAsync("template-beta");

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        var globals = await repository.ListGlobalAsync(CancellationToken.None);
        Assert.Equal(global.Id, Assert.Single(globals).Id);
    }

    // --- Foreign-key enforcement -----------------------------------------------

    [Fact]
    public async Task An_org_template_cannot_reference_an_organization_that_does_not_exist()
    {
        var ghost = Template.CreateForOrganization(
            Guid.CreateVersion7(), "template-alpha", 1, _validDefinition, _createdAt);

        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    // --- Empty-id guards -------------------------------------------------------

    [Fact]
    public async Task Lookups_reject_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new TemplateRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindGlobalByIdAsync(Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByOrganizationAndIdAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByOrganizationAndIdAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByOrganizationAndKeyAsync(
                Guid.Empty, "template-alpha", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByOrganizationAsync(Guid.Empty, CancellationToken.None));
    }
}
