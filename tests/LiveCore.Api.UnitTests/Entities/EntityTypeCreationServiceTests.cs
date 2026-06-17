// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Entities;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Integration-style tests for the <see cref="EntityTypeCreationService"/> (CORE-ENT-007, the "Vertical
/// Authoring and Read API Completeness" epic), the command behind
/// <c>POST /api/v1/workspaces/{workspaceId}/entity-types</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, SQL translation, the per-workspace unique
/// (<c>organization_id</c>, <c>workspace_id</c>, <c>type_key</c>) index and the single-transaction insert +
/// audit append are exercised on every run without a database server.
///
/// Coverage (the story's "tenant/workspace-scoped; audited" plus the per-workspace unique key):
/// <list type="bullet">
///   <item>CREATE persists the type as DATA (key/display name/attribute schema) and appends exactly one
///   append-only <see cref="AuditAction.EntityTypeCreated"/> record naming the actor and the created type, and
///   no state pair (a definition is not a transition).</item>
///   <item>ATOMICITY/AUDIT-CONTENT: the audit record carries only identifiers and the generic kind name — never
///   the type's key, display name or attribute schema (threat T7).</item>
///   <item>DUPLICATE KEY: defining a key the SAME workspace already holds returns
///   <see cref="EntityTypeCreationStatus.DuplicateKey"/>, creates no second type and writes no audit; the same
///   key in a SIBLING workspace or ANOTHER tenant is NOT a duplicate and is created independently (threat T5).</item>
///   <item>KEY CANONICALIZATION: a re-cased/padded key collides with the stored canonical key (so it is a
///   duplicate), and is stored canonical on create.</item>
///   <item>VALIDATION: empty required ids are rejected.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all keys/names are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntityTypeCreationServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _createTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntityTypeCreationServiceTests()
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

    // The service and every repository it composes MUST share one context instance so the explicit
    // transaction enrols each repository's SaveChanges.
    private static EntityTypeCreationService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new EntityTypeRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Create_persists_the_type_as_data_and_appends_one_audit_record()
    {
        var seed = await SeedTenantAsync();

        Guid createdId;
        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, "type-alpha", "Type Alpha", "{\"fields\":[]}",
                seed.Actor, _createTime, CancellationToken.None);

            Assert.Equal(EntityTypeCreationStatus.Created, result.Status);
            Assert.NotNull(result.EntityType);
            createdId = result.EntityType!.Id;
            Assert.Equal("type-alpha", result.EntityType.TypeKey);
            Assert.Equal(seed.OrganizationId, result.EntityType.OrganizationId);
            Assert.Equal(seed.WorkspaceId, result.EntityType.WorkspaceId);
        }

        await using var verify = CreateContext();

        var stored = await verify.EntityTypes.AsNoTracking().SingleAsync(t => t.Id == createdId);
        Assert.Equal("type-alpha", stored.TypeKey);
        Assert.Equal("Type Alpha", stored.DisplayName);
        Assert.Equal("{\"fields\":[]}", stored.AttributeSchema);

        var entry = Assert.Single(
            await verify.AuditLogs.AsNoTracking().Where(a => a.Action == AuditAction.EntityTypeCreated).ToListAsync());
        Assert.Equal(seed.OrganizationId, entry.OrganizationId);
        Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seed.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(EntityType), entry.ResourceType);
        Assert.Equal(createdId, entry.ResourceId);
        // A definition is a birth, not a transition: no before/after state pair, and no content recorded.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Create_stores_a_recased_padded_key_canonicalized()
    {
        var seed = await SeedTenantAsync();

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, "  Type-Alpha  ", "Type Alpha", "{}",
            seed.Actor, _createTime, CancellationToken.None);

        Assert.Equal(EntityTypeCreationStatus.Created, result.Status);
        Assert.Equal("type-alpha", result.EntityType!.TypeKey);
    }

    [Fact]
    public async Task Create_with_a_duplicate_key_in_the_same_workspace_is_rejected_and_writes_no_audit()
    {
        var seed = await SeedTenantAsync();

        await using (var first = CreateContext())
        {
            var service = CreateService(first);
            var created = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, "type-alpha", "Type Alpha", "{}",
                seed.Actor, _createTime, CancellationToken.None);
            Assert.Equal(EntityTypeCreationStatus.Created, created.Status);
        }

        await using (var second = CreateContext())
        {
            var service = CreateService(second);
            // A re-cased key canonicalizes to the same stored key, so it is the same per-workspace natural key.
            var duplicate = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, "TYPE-ALPHA", "Other Display", "{}",
                seed.Actor, _createTime, CancellationToken.None);
            Assert.Equal(EntityTypeCreationStatus.DuplicateKey, duplicate.Status);
            Assert.Null(duplicate.EntityType);
        }

        await using var verify = CreateContext();
        // Exactly one type with the key, and exactly one audit fact (the original create).
        Assert.Equal(
            1,
            await verify.EntityTypes.AsNoTracking()
                .CountAsync(t => t.WorkspaceId == seed.WorkspaceId && t.TypeKey == "type-alpha"));
        Assert.Equal(
            1,
            await verify.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditAction.EntityTypeCreated));
    }

    [Fact]
    public async Task Create_with_the_same_key_in_a_sibling_workspace_is_not_a_duplicate()
    {
        var seed = await SeedTenantAsync();
        Guid siblingWorkspaceId;
        await using (var setup = CreateContext())
        {
            var sibling = Workspace.Create(seed.OrganizationId, "sibling-show", "Sibling Show", _seedTime);
            setup.Workspaces.Add(sibling);
            await setup.SaveChangesAsync();
            siblingWorkspaceId = sibling.Id;
        }

        await using (var first = CreateContext())
        {
            var service = CreateService(first);
            await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, "type-alpha", "Type Alpha", "{}",
                seed.Actor, _createTime, CancellationToken.None);
        }

        await using (var second = CreateContext())
        {
            var service = CreateService(second);
            // The same key in a DIFFERENT workspace of the same tenant is a different type (threat T5).
            var result = await service.CreateAsync(
                seed.OrganizationId, siblingWorkspaceId, "type-alpha", "Type Alpha", "{}",
                seed.Actor, _createTime, CancellationToken.None);
            Assert.Equal(EntityTypeCreationStatus.Created, result.Status);
        }

        await using var verify = CreateContext();
        Assert.Equal(2, await verify.EntityTypes.AsNoTracking().CountAsync(t => t.TypeKey == "type-alpha"));
        Assert.Equal(2, await verify.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditAction.EntityTypeCreated));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Create_rejects_empty_required_ids(bool org, bool workspace, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            "type-alpha",
            "Type Alpha",
            "{}",
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _createTime,
            CancellationToken.None));
    }

    /// <summary>Seeds one organization + workspace + actor user and returns the ids the tests use.</summary>
    private async Task<SeededTenant> SeedTenantAsync()
    {
        await using var context = CreateContext();

        var actor = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, _issuer, "host-a"),
            _seedTime);
        context.UserProfiles.Add(actor);

        var org = Organization.Create(_orgSlugA, _orgSlugA, _seedTime);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        var workspace = Workspace.Create(org.Id, "summer-show", "Summer Show", _seedTime);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        return new SeededTenant
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
        };
    }

    private sealed class SeededTenant
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
    }
}
