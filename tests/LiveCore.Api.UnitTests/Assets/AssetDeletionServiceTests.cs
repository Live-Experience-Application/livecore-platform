using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Integration-style tests for the <see cref="AssetDeletionService"/> (CORE-LIFE-006, the "Resource Lifecycle
/// and Deletion" epic), the command behind <c>DELETE /api/v1/assets/{assetId}</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, SQL translation, the explicit removal of the asset's <c>asset_links</c>, the
/// FK cascade and the single transaction are exercised on every run without a database server — exactly like
/// the scene/entity/content-block deletion service tests. A recording fake <see cref="IAssetStorage"/> captures the
/// storage object deletion (the story's required test "Integration with a fake IAssetStorage recording the
/// delete"); one test substitutes the production fail-closed <see cref="UnconfiguredAssetStorage"/> to prove
/// the storage-unconfigured path changes nothing.
///
/// Coverage (the story's "its links are removed and the underlying storage object is deleted via IAssetStorage;
/// authorized and tenant-scoped"):
/// <list type="bullet">
///   <item>CASCADE + STORAGE: deleting an asset removes the asset and ALL of its links and deletes its storage
///   object (recorded by the fake), while another asset, its link and the linked target survive (cascade, not
///   over-reach).</item>
///   <item>ORDER / ATOMICITY: with the fail-closed <see cref="UnconfiguredAssetStorage"/> the delete throws
///   and the whole transaction rolls back — the asset and its links survive and no audit record is written, so
///   no metadata row is ever removed while its object could not be (no dangling row).</item>
///   <item>AUDIT: a successful deletion appends exactly one append-only <see cref="AuditAction.AssetDeleted"/>
///   record naming the actor and the deleted asset, and no state pair.</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling workspace and an id in
///   another tenant all return <see cref="AssetDeletionResult.NotFound"/> and change NOTHING (the
///   sibling/foreign asset and its links survive, no storage delete is recorded and no audit record is
///   written).</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AssetDeletionServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _deleteTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeProvider _time = new FixedTimeProvider(_deleteTime);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetDeletionServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // Enforce foreign keys so the (org/workspace/asset/user) FKs and the asset_links cascade are exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // The service and every repository (and the quota service) it composes MUST share one context instance so
    // the explicit transaction enrols each repository's SaveChanges and the storage-quota release.
    private static AssetDeletionService CreateService(LiveCoreDbContext context, IAssetStorage storage)
        => new(
            new TransactionalUnitOfWork(context),
            new AssetRepository(context),
            new AssetLinkRepository(context),
            storage,
            new AuditLogRepository(context),
            new QuotaEnforcementService(
                new QuotaDefinitionRepository(context),
                new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
                new QuotaUsageRepository(context),
                _time));

    [Fact]
    public async Task Delete_removes_the_asset_and_its_links_and_storage_object_while_unrelated_survive()
    {
        var seeded = await SeedFullGraphAsync();
        var storage = new RecordingAssetStorage();

        await using (var context = CreateContext())
        {
            var service = CreateService(context, storage);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.Deleted, result);
        }

        // The storage object of exactly the deleted asset was deleted via IAssetStorage (the story's "the
        // underlying storage object is deleted via IAssetStorage", recorded by the fake).
        Assert.Equal(new[] { seeded.AssetToDelete }, storage.DeletedAssetIds);
        Assert.Equal(new[] { seeded.AssetToDeleteObjectKey }, storage.DeletedObjectKeys);

        await using var verify = CreateContext();

        // The deleted asset is gone, with both of its links.
        Assert.False(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetToDelete));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Link1));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Link2));

        // The OTHER asset, its link and the linked target content block all survive — the cascade removed only
        // the deleted asset's own rows.
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.OtherAsset));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.OtherLink));
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.ContentBlock));
    }

    [Fact]
    public async Task Delete_appends_one_asset_deleted_audit_record_for_the_actor()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context, new RecordingAssetStorage());
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.AssetDeleted)
            .ToListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(seeded.OrganizationId, entry.OrganizationId);
        Assert.Equal(seeded.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seeded.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(Asset), entry.ResourceType);
        Assert.Equal(seeded.AssetToDelete, entry.ResourceId);
        // A deletion is a removal, not a transition: no before/after state pair.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Delete_deletes_a_pending_asset_too_and_records_the_storage_delete()
    {
        // Until this story an Available asset could not be deleted; the host-initiated delete works for any
        // status. A still-pending asset (object may never have been uploaded) is deleted; DeleteObjectAsync is
        // idempotent, so the storage delete is still issued.
        var seeded = await SeedFullGraphAsync();
        var storage = new RecordingAssetStorage();

        await using (var context = CreateContext())
        {
            var service = CreateService(context, storage);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.PendingAsset, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.Deleted, result);
        }

        Assert.Equal(new[] { seeded.PendingAsset }, storage.DeletedAssetIds);
        await using var verify = CreateContext();
        Assert.False(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.PendingAsset));
    }

    [Fact]
    public async Task Delete_fails_closed_when_storage_is_unconfigured_and_changes_nothing()
    {
        // The storage object is deleted BEFORE the row, so with no configured adapter the delete throws and the
        // whole transaction rolls back: the asset and its links survive and no audit record is written — no
        // metadata row is ever removed while its object could not be (no dangling row, no orphaned object).
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context, new UnconfiguredAssetStorage());
            await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(() => service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetToDelete, seeded.Actor, _deleteTime, CancellationToken.None));
        }

        await using var verify = CreateContext();
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetToDelete));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Link1));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Link2));
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetDeleted));
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_asset_and_records_nothing()
    {
        var seeded = await SeedFullGraphAsync();
        var storage = new RecordingAssetStorage();

        await using (var context = CreateContext())
        {
            var service = CreateService(context, storage);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, Guid.CreateVersion7(), seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.NotFound, result);
        }

        // Nothing was deleted, no storage call was made and nothing was audited.
        Assert.Empty(storage.DeletedAssetIds);
        await using var verify = CreateContext();
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetToDelete));
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetDeleted));
    }

    [Fact]
    public async Task Delete_through_a_sibling_workspace_is_not_found_and_keeps_the_asset()
    {
        // The asset lives in workspace W; addressing it through sibling workspace W2 of the SAME org must never
        // reach it (workspace-scoped lookup; threat T1/T5).
        var seeded = await SeedFullGraphAsync();
        var storage = new RecordingAssetStorage();
        Guid siblingWorkspaceId;
        await using (var seed = CreateContext())
        {
            var sibling = Workspace.Create(seeded.OrganizationId, "sibling-show", "Sibling Show", _seedTime);
            seed.Workspaces.Add(sibling);
            await seed.SaveChangesAsync();
            siblingWorkspaceId = sibling.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context, storage);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, siblingWorkspaceId, seeded.AssetToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.NotFound, result);
        }

        Assert.Empty(storage.DeletedAssetIds);
        await using var verify = CreateContext();
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetToDelete));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Link1));
    }

    [Fact]
    public async Task Delete_through_a_foreign_tenant_is_not_found_and_keeps_the_asset()
    {
        // The asset lives in org A's workspace; addressing it with org B's id must never reach it (organization
        // boundary checked before workspace boundary; threat T5).
        var seeded = await SeedFullGraphAsync();
        var storage = new RecordingAssetStorage();
        Guid orgBId;
        await using (var seed = CreateContext())
        {
            var orgB = Organization.Create(_orgSlugB, _orgSlugB, _seedTime);
            seed.Organizations.Add(orgB);
            await seed.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context, storage);
            var result = await service.DeleteAsync(
                orgBId, seeded.WorkspaceId, seeded.AssetToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.NotFound, result);
        }

        Assert.Empty(storage.DeletedAssetIds);
        await using var verify = CreateContext();
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetToDelete));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Delete_rejects_empty_required_ids(bool org, bool workspace, bool asset, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context, new RecordingAssetStorage());
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            asset ? Guid.Empty : Guid.CreateVersion7(),
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _deleteTime,
            CancellationToken.None));
    }

    [Fact]
    public async Task Delete_releases_the_assets_reserved_storage_bytes_back_to_the_workspace()
    {
        // The asset reserved 4,096 bytes of the workspace's storage quota at upload-intent (recorded as its
        // SizeBytes). The workspace's recorded storage usage is 10,000; deleting the asset releases the 4,096
        // bytes, so the usage drops to 5,904 and the headroom is restored (the story's "deleting an asset
        // restores available bytes").
        var seeded = await SeedAssetWithReservedStorageAsync(reservedBytes: 4096, startingUsage: 10_000);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, new RecordingAssetStorage());
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(AssetDeletionResult.Deleted, result);
        }

        Assert.Equal(10_000 - 4096, await ReadStorageUsageAsync(seeded.WorkspaceId, seeded.QuotaId));
    }

    [Fact]
    public async Task Delete_releases_nothing_for_an_asset_with_no_recorded_size()
    {
        // An asset created before storage enforcement (SizeBytes null) reserved no bytes; deleting it leaves
        // the recorded usage untouched (the release is a clamped, idempotent no-op).
        var seeded = await SeedAssetWithReservedStorageAsync(reservedBytes: null, startingUsage: 10_000);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, new RecordingAssetStorage());
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        Assert.Equal(10_000, await ReadStorageUsageAsync(seeded.WorkspaceId, seeded.QuotaId));
    }

    /// <summary>
    /// Seeds one organization + workspace + actor, a workspace-subject <c>asset.storage.bytes.max</c> quota
    /// granted a generous byte limit with <paramref name="startingUsage"/> bytes already recorded, and one
    /// asset whose declared <paramref name="reservedBytes"/> (or none) is recorded as its
    /// <see cref="Asset.SizeBytes"/>. Returns the ids and the quota definition id.
    /// </summary>
    private async Task<SeededStorageAsset> SeedAssetWithReservedStorageAsync(long? reservedBytes, long startingUsage)
    {
        await using var context = CreateContext();

        var actor = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "host-a"), _seedTime);
        context.UserProfiles.Add(actor);
        var org = Organization.Create(_orgSlugA, _orgSlugA, _seedTime);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        var workspace = Workspace.Create(org.Id, "summer-show", "Summer Show", _seedTime);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var entitlement = EntitlementDefinition.Define(
            QuotaEntitlementKeys.AssetStorageBytesMax, EntitlementValueKind.Quota, "Storage quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.Workspace, QuotaUnit.Bytes, _seedTime);
        context.QuotaDefinitions.Add(quota);
        context.SubjectEntitlements.Add(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.Workspace, workspace.Id, entitlement, 1_000_000, null, _seedTime));
        var usage = QuotaUsage.Start(EntitlementSubjectType.Workspace, workspace.Id, quota, _seedTime);
        usage.Record(startingUsage, _seedTime);
        context.QuotaUsage.Add(usage);

        var asset = Asset.Create(
            org.Id,
            workspace.Id,
            actor.Id,
            "s3",
            "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}",
            "image/png",
            _seedTime,
            reservedBytes);
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        return new SeededStorageAsset
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
            AssetId = asset.Id,
            QuotaId = quota.Id,
        };
    }

    private async Task<long> ReadStorageUsageAsync(Guid workspaceId, Guid quotaDefinitionId)
    {
        await using var context = CreateContext();
        var usage = await new QuotaUsageRepository(context).FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.Workspace, workspaceId, quotaDefinitionId, CancellationToken.None);
        return usage?.UsedAmount ?? 0;
    }

    private sealed class SeededStorageAsset
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
        public Guid AssetId { get; init; }
        public Guid QuotaId { get; init; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Seeds one organization + workspace holding: the actor user; an Available <c>AssetToDelete</c> carrying
    /// two links (to a content block and to an entity-style id); a still-Pending asset; and an unrelated
    /// <c>OtherAsset</c> with its own link to a content block — the rows that must survive the deletion of
    /// <c>AssetToDelete</c>. Returns the ids the tests assert on.
    /// </summary>
    private async Task<SeededGraph> SeedFullGraphAsync()
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

        var assetToDeleteKey = $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}";
        var assetToDelete = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets", assetToDeleteKey, "image/png", _seedTime);
        assetToDelete.MarkAvailable(4096, "abc123", _seedTime);
        var pendingAsset = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}", "image/png", _seedTime);
        var otherAsset = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}", "image/png", _seedTime);
        otherAsset.MarkAvailable(2048, "def456", _seedTime);
        context.Assets.AddRange(assetToDelete, pendingAsset, otherAsset);
        await context.SaveChangesAsync();

        var contentBlock = Guid.CreateVersion7();
        var entityTarget = Guid.CreateVersion7();
        var otherTarget = Guid.CreateVersion7();

        // The deleted asset's two links, plus the other asset's link (which must survive).
        var link1 = AssetLink.Create(
            org.Id, workspace.Id, assetToDelete.Id, AssetLinkTargetType.ContentBlock, contentBlock, actor.Id, _seedTime);
        var link2 = AssetLink.Create(
            org.Id, workspace.Id, assetToDelete.Id, AssetLinkTargetType.Entity, entityTarget, actor.Id, _seedTime);
        var otherLink = AssetLink.Create(
            org.Id, workspace.Id, otherAsset.Id, AssetLinkTargetType.ContentBlock, otherTarget, actor.Id, _seedTime);
        context.AssetLinks.AddRange(link1, link2, otherLink);

        // A content block the deleted asset's link points at — the linked TARGET must survive (only the link
        // row is removed). It needs a scene to satisfy the content_blocks.scene_id foreign key.
        var scene = LiveCore.Api.Scenes.Scene.Create(org.Id, workspace.Id, "Scene", 0, _seedTime);
        context.Scenes.Add(scene);
        await context.SaveChangesAsync();
        var block = LiveCore.Api.Content.ContentBlock.Create(
            org.Id, workspace.Id, scene.Id, LiveCore.Api.Content.ContentBlockType.Text, "body", _seedTime);
        context.ContentBlocks.Add(block);
        await context.SaveChangesAsync();

        return new SeededGraph
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
            AssetToDelete = assetToDelete.Id,
            AssetToDeleteObjectKey = assetToDeleteKey,
            PendingAsset = pendingAsset.Id,
            OtherAsset = otherAsset.Id,
            Link1 = link1.Id,
            Link2 = link2.Id,
            OtherLink = otherLink.Id,
            ContentBlock = block.Id,
        };
    }

    private sealed class SeededGraph
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
        public Guid AssetToDelete { get; init; }
        public string AssetToDeleteObjectKey { get; init; } = string.Empty;
        public Guid PendingAsset { get; init; }
        public Guid OtherAsset { get; init; }
        public Guid Link1 { get; init; }
        public Guid Link2 { get; init; }
        public Guid OtherLink { get; init; }
        public Guid ContentBlock { get; init; }
    }

    /// <summary>
    /// A conforming fake <see cref="IAssetStorage"/> that RECORDS each object deletion (the story's "fake
    /// IAssetStorage recording the delete"). It signs no URLs (the deletion flow never mints one) and can never
    /// serve bytes, so it cannot weaken the private-by-default posture. Not a production adapter.
    /// </summary>
    private sealed class RecordingAssetStorage : IAssetStorage
    {
        public List<Guid> DeletedAssetIds { get; } = [];

        public List<string> DeletedObjectKeys { get; } = [];

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException("The deletion flow never mints an upload URL.");

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException("The deletion flow never mints a download URL.");

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            DeletedAssetIds.Add(asset.Id);
            DeletedObjectKeys.Add(asset.ObjectKey);
            return Task.CompletedTask;
        }
    }
}
