using LiveCore.Api.Audit;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Templates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Templates;

/// <summary>
/// Integration-style tests for the <see cref="TemplateCreationService"/> (CORE-TMPL-001, the "Vertical Authoring
/// and Read API Completeness" epic), the command behind
/// <c>POST /api/v1/organizations/{organizationSlug}/templates</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so
/// the real model mapping, SQL translation, the filtered per-scope unique
/// (<c>organization_id</c>, <c>template_key</c>, <c>version</c>) index and the single-transaction insert + audit
/// append are exercised on every run without a database server.
///
/// Coverage (the story's "tenant-scoped; audited" plus the per-scope unique key and the global/organization
/// boundary):
/// <list type="bullet">
///   <item>CREATE persists the template as DATA (key/version/definition) ALWAYS organization-scoped (never
///   global) and appends exactly one append-only <see cref="AuditAction.TemplateCreated"/> record naming the
///   actor and the created template, ORGANIZATION-level (NO workspace) and with no state pair (a creation is not
///   a transition).</item>
///   <item>AUDIT-CONTENT: the audit record carries only identifiers and the generic kind name — never the
///   template's key or definition content (threat T7).</item>
///   <item>DUPLICATE: creating a key+version the SAME organization already holds returns
///   <see cref="TemplateCreationStatus.Duplicate"/>, creates no second template and writes no audit; the same
///   key+version in ANOTHER tenant, or the same key at a DIFFERENT version, is NOT a duplicate and is created
///   independently (threat T5).</item>
///   <item>KEY CANONICALIZATION: a re-cased/padded key is stored canonical on create.</item>
///   <item>VALIDATION: empty required ids are rejected.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all keys/definitions are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class TemplateCreationServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    // A generic, well-formed, NEUTRAL template definition (the template boundary, docs/04): a top-level
    // templateKey plus a non-empty entityTypes array of valid entries.
    private const string _definition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } }
          ]
        }
        """;

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _createTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public TemplateCreationServiceTests()
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

    // The service and every repository it composes MUST share one context instance so the explicit transaction
    // enrols each repository's SaveChanges.
    private static TemplateCreationService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new TemplateRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Create_persists_the_template_as_data_and_appends_one_org_level_audit_record()
    {
        var seed = await SeedTenantAsync(_orgSlugA);

        Guid createdId;
        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.CreateAsync(
                seed.OrganizationId, "sample.template", 1, _definition, seed.Actor, _createTime,
                CancellationToken.None);

            Assert.Equal(TemplateCreationStatus.Created, result.Status);
            Assert.NotNull(result.Template);
            createdId = result.Template!.Id;
            Assert.Equal("sample.template", result.Template.TemplateKey);
            Assert.Equal(1, result.Template.Version);
            Assert.Equal(seed.OrganizationId, result.Template.OrganizationId);
            // ALWAYS organization-scoped, never global.
            Assert.False(result.Template.IsGlobal);
        }

        await using var verify = CreateContext();

        var stored = await verify.Templates.AsNoTracking().SingleAsync(t => t.Id == createdId);
        Assert.Equal("sample.template", stored.TemplateKey);
        Assert.Equal(seed.OrganizationId, stored.OrganizationId);
        Assert.Equal(_definition.Trim(), stored.Definition);

        var entry = Assert.Single(
            await verify.AuditLogs.AsNoTracking().Where(a => a.Action == AuditAction.TemplateCreated).ToListAsync());
        Assert.Equal(seed.OrganizationId, entry.OrganizationId);
        // A template is an organization-level registry resource, so the fact records NO workspace.
        Assert.Null(entry.WorkspaceId);
        Assert.Equal(seed.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(Template), entry.ResourceType);
        Assert.Equal(createdId, entry.ResourceId);
        // A creation is a birth, not a transition: no before/after state pair, and no content recorded.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Create_stores_a_recased_padded_key_canonicalized()
    {
        var seed = await SeedTenantAsync(_orgSlugA);

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.CreateAsync(
            seed.OrganizationId, "  Sample.Template  ", 1, _definition, seed.Actor, _createTime,
            CancellationToken.None);

        Assert.Equal(TemplateCreationStatus.Created, result.Status);
        Assert.Equal("sample.template", result.Template!.TemplateKey);
    }

    [Fact]
    public async Task Create_with_a_duplicate_key_and_version_in_the_same_org_is_rejected_and_writes_no_audit()
    {
        var seed = await SeedTenantAsync(_orgSlugA);

        await using (var first = CreateContext())
        {
            var service = CreateService(first);
            var created = await service.CreateAsync(
                seed.OrganizationId, "sample.template", 1, _definition, seed.Actor, _createTime,
                CancellationToken.None);
            Assert.Equal(TemplateCreationStatus.Created, created.Status);
        }

        await using (var second = CreateContext())
        {
            var service = CreateService(second);
            // A re-cased key canonicalizes to the same stored key, so it is the same per-scope natural key.
            var duplicate = await service.CreateAsync(
                seed.OrganizationId, "SAMPLE.TEMPLATE", 1, _definition, seed.Actor, _createTime,
                CancellationToken.None);
            Assert.Equal(TemplateCreationStatus.Duplicate, duplicate.Status);
            Assert.Null(duplicate.Template);
        }

        await using var verify = CreateContext();
        Assert.Equal(
            1,
            await verify.Templates.AsNoTracking()
                .CountAsync(t => t.OrganizationId == seed.OrganizationId && t.TemplateKey == "sample.template"));
        Assert.Equal(
            1,
            await verify.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditAction.TemplateCreated));
    }

    [Fact]
    public async Task Create_with_the_same_key_at_a_different_version_in_the_same_org_is_not_a_duplicate()
    {
        var seed = await SeedTenantAsync(_orgSlugA);

        await using (var first = CreateContext())
        {
            var service = CreateService(first);
            await service.CreateAsync(
                seed.OrganizationId, "sample.template", 1, _definition, seed.Actor, _createTime,
                CancellationToken.None);
        }

        await using (var second = CreateContext())
        {
            var service = CreateService(second);
            // The version is part of the per-scope natural key, so the same key at v2 is a different template.
            var result = await service.CreateAsync(
                seed.OrganizationId, "sample.template", 2, _definition, seed.Actor, _createTime,
                CancellationToken.None);
            Assert.Equal(TemplateCreationStatus.Created, result.Status);
        }

        await using var verify = CreateContext();
        Assert.Equal(
            2,
            await verify.Templates.AsNoTracking()
                .CountAsync(t => t.OrganizationId == seed.OrganizationId && t.TemplateKey == "sample.template"));
        Assert.Equal(
            2,
            await verify.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditAction.TemplateCreated));
    }

    [Fact]
    public async Task Create_with_the_same_key_and_version_in_another_tenant_is_not_a_duplicate()
    {
        var seedA = await SeedTenantAsync(_orgSlugA);
        var seedB = await SeedTenantAsync(_orgSlugB);

        await using (var first = CreateContext())
        {
            var service = CreateService(first);
            await service.CreateAsync(
                seedA.OrganizationId, "sample.template", 1, _definition, seedA.Actor, _createTime,
                CancellationToken.None);
        }

        await using (var second = CreateContext())
        {
            var service = CreateService(second);
            // The same key+version in a DIFFERENT tenant is a different template (threat T5).
            var result = await service.CreateAsync(
                seedB.OrganizationId, "sample.template", 1, _definition, seedB.Actor, _createTime,
                CancellationToken.None);
            Assert.Equal(TemplateCreationStatus.Created, result.Status);
        }

        await using var verify = CreateContext();
        Assert.Equal(2, await verify.Templates.AsNoTracking().CountAsync(t => t.TemplateKey == "sample.template"));
        Assert.Equal(2, await verify.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditAction.TemplateCreated));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Create_rejects_empty_required_ids(bool org, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            "sample.template",
            1,
            _definition,
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _createTime,
            CancellationToken.None));
    }

    /// <summary>Seeds one organization + actor user and returns the ids the tests use.</summary>
    private async Task<SeededTenant> SeedTenantAsync(string slug)
    {
        await using var context = CreateContext();

        var actor = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, _issuer, $"admin-{slug}"),
            _seedTime);
        context.UserProfiles.Add(actor);

        var org = Organization.Create(slug, slug, _seedTime);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        return new SeededTenant
        {
            OrganizationId = org.Id,
            Actor = actor.Id,
        };
    }

    private sealed class SeededTenant
    {
        public Guid OrganizationId { get; init; }
        public Guid Actor { get; init; }
    }
}
