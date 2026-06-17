// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="AssetLinkRepository"/> (CORE-AST-005).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys =
/// ON</c>), so the real model mapping, the foreign keys into
/// <c>assets</c>/<c>organizations</c>/<c>workspaces</c>/<c>users</c>, the per-workspace unique key, the
/// set-null creator link and the target-type-name conversion are exercised on every run without a database
/// server — exactly like the <see cref="AssetRepositoryTests"/>.
///
/// The behaviors under test are the security-relevant ones for the asset-linking story (the epic
/// acceptance "assets are private by default and accessed only through authorized signed URLs", required
/// "Upload/download auth tests and storage adapter tests"):
/// <list type="bullet">
///   <item>OBJECT-LEVEL ISOLATION (threat T5; threat T1; docs/06_AUTHORIZATION_MATRIX.md: organization
///   boundary checked before workspace boundary): a link in workspace W1 is never returned via W2; a link
///   in organization A is never resolved via organization B; the list-by-asset lookup is tenant- and
///   workspace-scoped.</item>
///   <item>UNIQUENESS: the same asset cannot be linked to the same target twice (a duplicate insert is
///   reported, not duplicated).</item>
///   <item>REFERENTIAL INTEGRITY: the foreign keys reject a link referencing a non-existent asset,
///   workspace, organization or creating user; deleting the asset cascades to its links; deleting the
///   creating user anonymizes the link rather than deleting it.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md). All fixtures are generic.
/// </summary>
public sealed class AssetLinkRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetLinkRepositoryTests()
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
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync(string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subjectId), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(UserProfileAddResult.Added, await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<Asset> SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdBy, string objectKey)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, "s3", "livecore-private-assets", objectKey, "image/png", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetAddResult.Added, await new AssetRepository(context).AddAsync(asset, CancellationToken.None));
        return asset;
    }

    private async Task<AssetLink> SeedLinkAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        AssetLinkTargetType targetType,
        Guid targetId,
        Guid createdBy)
    {
        var link = AssetLink.Create(organizationId, workspaceId, assetId, targetType, targetId, createdBy, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetLinkAddResult.Added, await new AssetLinkRepository(context).AddAsync(link, CancellationToken.None));
        return link;
    }

    [Fact]
    public async Task Link_round_trips_through_the_database()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var target = Guid.CreateVersion7();
        var seeded = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, target, user.Id);

        await using var context = CreateContext();
        var loaded = await new AssetLinkRepository(context).FindByIdAsync(org.Id, ws.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(asset.Id, loaded.AssetId);
        Assert.Equal(AssetLinkTargetType.Entity, loaded.TargetType);
        Assert.Equal(target, loaded.TargetId);
        Assert.Equal(user.Id, loaded.CreatedByUserProfileId);
    }

    [Fact]
    public async Task Target_type_is_stored_as_its_stable_name()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var stored = await context.Database
            .SqlQueryRaw<string>("SELECT target_type AS Value FROM asset_links")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(AssetLinkTargetType.ContentBlock), Assert.Single(stored));
    }

    [Fact]
    public async Task ListByAsset_returns_only_the_assets_links_in_id_order()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset1 = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/1.bin");
        var asset2 = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/2.bin");

        var first = await SeedLinkAsync(org.Id, ws.Id, asset1.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
        var second = await SeedLinkAsync(org.Id, ws.Id, asset1.Id, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id);
        // A link of another asset must never leak into asset1's list.
        await SeedLinkAsync(org.Id, ws.Id, asset2.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var listed = await new AssetLinkRepository(context).ListByAssetAsync(org.Id, ws.Id, asset1.Id, CancellationToken.None);

        var expected = new[] { first.Id, second.Id }.OrderBy(id => id).ToList();
        Assert.Equal(expected, listed.Select(link => link.Id).ToList());
    }

    [Fact]
    public async Task ListByAsset_returns_empty_for_an_asset_with_no_links()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");

        await using var context = CreateContext();
        var listed = await new AssetLinkRepository(context).ListByAssetAsync(org.Id, ws.Id, asset.Id, CancellationToken.None);

        Assert.Empty(listed);
    }

    [Fact]
    public async Task A_link_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a link exists in W1 only.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws1 = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var ws2 = await SeedWorkspaceAsync(org.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws1.Id, user.Id, "org/ws1/a.bin");
        var inW1 = await SeedLinkAsync(org.Id, ws1.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var repository = new AssetLinkRepository(context);

        Assert.NotNull(await repository.FindByIdAsync(org.Id, ws1.Id, inW1.Id, CancellationToken.None));
        Assert.Null(await repository.FindByIdAsync(org.Id, ws2.Id, inW1.Id, CancellationToken.None));
        // The by-asset list is also workspace-scoped: asking under W2 yields nothing.
        Assert.Empty(await repository.ListByAssetAsync(org.Id, ws2.Id, asset.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_link_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; org boundary checked before workspace boundary).
        var orgA = await SeedOrganizationAsync(_organizationSlugA);
        var orgB = await SeedOrganizationAsync(_organizationSlugB);
        var wsInA = await SeedWorkspaceAsync(orgA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(orgA.Id, wsInA.Id, user.Id, "org/ws/a.bin");
        var inA = await SeedLinkAsync(orgA.Id, wsInA.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var repository = new AssetLinkRepository(context);

        Assert.NotNull(await repository.FindByIdAsync(orgA.Id, wsInA.Id, inA.Id, CancellationToken.None));
        Assert.Null(await repository.FindByIdAsync(orgB.Id, wsInA.Id, inA.Id, CancellationToken.None));
        Assert.Empty(await repository.ListByAssetAsync(orgB.Id, wsInA.Id, asset.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_asset_cannot_be_linked_to_the_same_target_twice()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var target = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, target, user.Id);

        // A second link for the exact same (asset, target type, target id) is rejected as a duplicate.
        var duplicate = AssetLink.Create(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, target, user.Id, _createdAt);
        await using var context = CreateContext();
        var result = await new AssetLinkRepository(context).AddAsync(duplicate, CancellationToken.None);

        Assert.Equal(AssetLinkAddResult.Duplicate, result);
    }

    [Fact]
    public async Task The_same_asset_may_be_linked_to_the_same_target_id_under_a_different_kind()
    {
        // The natural key includes target_type, so the same id as a content block and as an entity are two
        // distinct links (they will resolve to different resources).
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var target = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, target, user.Id);

        var asEntity = AssetLink.Create(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, target, user.Id, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetLinkAddResult.Added, await new AssetLinkRepository(context).AddAsync(asEntity, CancellationToken.None));
    }

    [Fact]
    public async Task A_link_cannot_reference_an_asset_that_does_not_exist()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var ghost = AssetLink.Create(
            org.Id, ws.Id, Guid.CreateVersion7(), AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id, _createdAt);

        await using var context = CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => new AssetLinkRepository(context).AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_link_cannot_reference_a_creating_user_that_does_not_exist()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var ghost = AssetLink.Create(
            org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), Guid.CreateVersion7(), _createdAt);

        await using var context = CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => new AssetLinkRepository(context).AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_creating_user_anonymizes_the_link_rather_than_deleting_it()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var seeded = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedUser = await deleteContext.UserProfiles.SingleAsync(profile => profile.Id == user.Id);
            deleteContext.UserProfiles.Remove(storedUser);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var loaded = await new AssetLinkRepository(context).FindByIdAsync(org.Id, ws.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.CreatedByUserProfileId);
        Assert.Contains("createdBy=anonymized", loaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_the_asset_cascades_to_its_links()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedAsset = await deleteContext.Assets.SingleAsync(a => a.Id == asset.Id);
            deleteContext.Assets.Remove(storedAsset);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        Assert.Equal(0, await context.AssetLinks.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoveByAsset_removes_only_the_assets_links_and_returns_the_count()
    {
        // CORE-LIFE-006: the asset deletion removes its own links by asset id. Only the named asset's links
        // go; another asset's links in the same workspace survive.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var other = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/b.bin");
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id);
        var survivor = await SeedLinkAsync(org.Id, ws.Id, other.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using (var context = CreateContext())
        {
            var removed = await new AssetLinkRepository(context)
                .RemoveByAssetAsync(org.Id, ws.Id, asset.Id, CancellationToken.None);
            Assert.Equal(2, removed);
        }

        await using var verify = CreateContext();
        var repository = new AssetLinkRepository(verify);
        Assert.Empty(await repository.ListByAssetAsync(org.Id, ws.Id, asset.Id, CancellationToken.None));
        // The other asset's link survives — the removal is asset-scoped, never over-reaching.
        Assert.NotNull(await repository.FindByIdAsync(org.Id, ws.Id, survivor.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveByAsset_is_a_no_op_returning_zero_for_an_asset_with_no_links()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");

        await using var context = CreateContext();
        var removed = await new AssetLinkRepository(context)
            .RemoveByAssetAsync(org.Id, ws.Id, asset.Id, CancellationToken.None);

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task RemoveByAsset_never_removes_links_through_a_foreign_tenant_or_workspace()
    {
        // Mandatory negative isolation test (threat T5/T1): the removal is tenant- and workspace-scoped, so an
        // asset's links are never removed through another tenant's or workspace's id even when the asset id is
        // known.
        var orgA = await SeedOrganizationAsync(_organizationSlugA);
        var orgB = await SeedOrganizationAsync(_organizationSlugB);
        var wsInA = await SeedWorkspaceAsync(orgA.Id, _workspaceSlugA);
        var siblingInA = await SeedWorkspaceAsync(orgA.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(orgA.Id, wsInA.Id, user.Id, "org/ws/a.bin");
        var link = await SeedLinkAsync(orgA.Id, wsInA.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using (var context = CreateContext())
        {
            var repository = new AssetLinkRepository(context);
            // Foreign tenant (org B) and sibling workspace both remove nothing.
            Assert.Equal(0, await repository.RemoveByAssetAsync(orgB.Id, wsInA.Id, asset.Id, CancellationToken.None));
            Assert.Equal(0, await repository.RemoveByAssetAsync(orgA.Id, siblingInA.Id, asset.Id, CancellationToken.None));
        }

        await using var verify = CreateContext();
        Assert.NotNull(await new AssetLinkRepository(verify).FindByIdAsync(orgA.Id, wsInA.Id, link.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_removes_only_the_addressed_link()
    {
        // CORE-LIFE-007: the asset-link removal hard-deletes exactly the one loaded link row. Another link of
        // the SAME asset, and the asset itself, survive — the removal is the inverse of the per-link insert,
        // never a cascade onto the asset.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_subject);
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id, "org/ws/a.bin");
        var toRemove = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
        var survivor = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id);

        await using (var context = CreateContext())
        {
            var repository = new AssetLinkRepository(context);
            var loaded = await repository.FindByIdAsync(org.Id, ws.Id, toRemove.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            await repository.RemoveAsync(loaded, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var verifyRepository = new AssetLinkRepository(verify);
        Assert.Null(await verifyRepository.FindByIdAsync(org.Id, ws.Id, toRemove.Id, CancellationToken.None));
        // The other link of the same asset survives, and so does the asset row itself.
        Assert.NotNull(await verifyRepository.FindByIdAsync(org.Id, ws.Id, survivor.Id, CancellationToken.None));
        Assert.NotNull(await new AssetRepository(verify).FindByIdAsync(org.Id, ws.Id, asset.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_throws_for_a_null_link()
    {
        await using var context = CreateContext();
        var repository = new AssetLinkRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => repository.RemoveAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Lookups_reject_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new AssetLinkRepository(context);
        var id = Guid.CreateVersion7();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(Guid.Empty, id, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(id, Guid.Empty, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(id, id, Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByAssetAsync(Guid.Empty, id, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByAssetAsync(id, Guid.Empty, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByAssetAsync(id, id, Guid.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RemoveByAssetAsync(Guid.Empty, id, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RemoveByAssetAsync(id, Guid.Empty, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.RemoveByAssetAsync(id, id, Guid.Empty, CancellationToken.None));
    }
}
