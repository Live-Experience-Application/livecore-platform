// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Integration-style tests for <see cref="AssetUploadIntentService"/> (CORE-AST-003 + the CORE-MON-006
/// storage-quota gate) — the upload-intent command that registers a PENDING <see cref="Asset"/> with
/// server-minted storage coordinates, atomically consumes the client-declared object size against the
/// workspace's <c>asset.storage.bytes.max</c> quota and mints its short-lived signed upload URL through the
/// <see cref="IAssetStorage"/> adapter port (CORE-AST-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, the atomic limit-guarded quota UPSERT and the single transaction are exercised on
/// every run without a database server — exactly like the asset deletion service tests. A conforming fake
/// <see cref="IAssetStorage"/> stands in for the deployment-supplied S3-compatible signer; one test substitutes
/// the production fail-closed <see cref="UnconfiguredAssetStorage"/>.
///
/// Coverage (the story's required "Upload over quota rejected; under quota succeeds"):
/// <list type="bullet">
///   <item>UNDER QUOTA: a declared size that fits the workspace's storage grant -&gt; the pending asset is
///   registered with the declared size and the workspace's recorded storage usage is incremented by exactly
///   that many bytes.</item>
///   <item>OVER QUOTA: a declared size that would exceed the grant -&gt; QuotaExceeded, NO asset is persisted
///   and NO bytes are consumed (the fail-closed free-tier gate).</item>
///   <item>FAIL-CLOSED STORAGE + ATOMICITY: with storage unconfigured the command throws and the whole
///   transaction rolls back — no asset persisted AND no bytes consumed (no orphan, no leaked quota).</item>
///   <item>UNGOVERNED / UNLIMITED / NO-GRANT: an upload proceeds when no storage quota governs the deployment
///   and under an unlimited (fair-use) grant; it is DENIED fail-closed when the quota is defined but the
///   workspace holds no grant, and a grant to ANOTHER workspace confers no headroom here (isolation).</item>
///   <item>Input guards reject an invalid content type, a non-positive declared size and empty ids.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class AssetUploadIntentServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";

    private static readonly DateTimeOffset _now = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly AssetStorageLocation _location = new("s3", "livecore-private-assets");
    private static readonly TimeProvider _time = new FixedTimeProvider(_now);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetUploadIntentServiceTests()
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

    // The service and every repository/quota dependency it composes MUST share one context instance so the
    // explicit transaction enrols each SaveChanges and the guarded consume. The allowlist/ceiling policy
    // (CORE-AST-007) defaults to the test-only Unrestricted policy so tests about another aspect of the command
    // are not coupled to the production default acceptance policy; the CORE-AST-007 tests pass a restrictive one.
    private static AssetUploadIntentService CreateService(
        LiveCoreDbContext context,
        IAssetStorage storage,
        AssetUploadConstraints? constraints = null)
        => new(
            new AssetRepository(context),
            storage,
            _location,
            constraints ?? AssetUploadConstraints.Unrestricted,
            new QuotaEnforcementService(
                new QuotaDefinitionRepository(context),
                new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
                new QuotaUsageRepository(context),
                _time),
            new TransactionalUnitOfWork(context));

    [Fact]
    public async Task CreateAsync_under_quota_registers_a_pending_asset_with_the_declared_size_and_consumes_bytes()
    {
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 1_000_000, startingUsage: 100_000);
        const long declaredBytes = 250_000;

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", declaredBytes, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.Created, result.Outcome);
        var asset = result.Intent!.Asset;
        Assert.Equal(seed.OrganizationId, asset.OrganizationId);
        Assert.Equal(seed.WorkspaceId, asset.WorkspaceId);
        Assert.Equal(seed.UserId, asset.CreatedByUserProfileId);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        // The declared/reserved size is recorded so the deletion can release exactly it.
        Assert.Equal(declaredBytes, asset.SizeBytes);
        Assert.StartsWith($"assets/{seed.OrganizationId}/{seed.WorkspaceId}/", asset.ObjectKey, StringComparison.Ordinal);

        // The pending asset is persisted, and the workspace's storage usage grew by exactly the declared bytes.
        await using var verify = CreateContext();
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == asset.Id));
        Assert.Equal(100_000 + declaredBytes, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_mints_a_short_lived_upload_url_for_the_assets_own_object()
    {
        var seed = await SeedWorkspaceAsync();
        await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 1_000_000, startingUsage: 0);

        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage());
        var result = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "application/pdf", 512, _now, CancellationToken.None);

        var intent = result.Intent!;
        var url = intent.UploadUrl;
        Assert.Equal(AssetStorageOperation.Upload, url.Operation);
        Assert.True(url.Url.IsAbsoluteUri);
        Assert.False(url.IsExpired(_now));
        Assert.True(url.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
        // Signed for THIS asset's own coordinates, not an arbitrary bucket/object (threats T5/T1).
        Assert.Contains(intent.Asset.Bucket, url.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(intent.Asset.ObjectKey, url.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_over_quota_is_rejected_and_persists_and_consumes_nothing()
    {
        // The workspace is near its cap (limit 1,000,000; already used 900,000). A 250,000-byte upload would
        // overrun it: rejected, with no asset persisted and no bytes consumed (the fail-closed free-tier gate).
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 1_000_000, startingUsage: 900_000);

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 250_000, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.QuotaExceeded, result.Outcome);
        Assert.Null(result.Intent);
        Assert.Equal(QuotaEntitlementKeys.AssetStorageBytesMax, result.QuotaDenial!.EntitlementKey);

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
        // The usage is unchanged: an over-quota upload reserves nothing.
        Assert.Equal(900_000, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_fails_closed_when_storage_is_unconfigured_and_rolls_back_the_consume()
    {
        // Storage is unconfigured. The consume runs first (there is headroom), then the URL mint throws inside
        // the transaction, so the whole unit of work rolls back: no asset persisted AND no bytes consumed.
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 1_000_000, startingUsage: 0);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, new UnconfiguredAssetStorage());
            await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(() => service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 250_000, _now, CancellationToken.None));
        }

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
        // No leaked quota: the consume was rolled back with the rest of the transaction.
        Assert.Equal(0, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_proceeds_and_records_nothing_when_no_storage_quota_governs_the_deployment()
    {
        // No asset.storage.bytes.max quota defined: the upload is ungoverned and proceeds, recording no usage.
        var seed = await SeedWorkspaceAsync();

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 9_999_999, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.Created, result.Outcome);
        await using var verify = CreateContext();
        Assert.Single(await verify.Assets.AsNoTracking().ToListAsync());
        // Nothing is recorded when no quota governs the command.
        Assert.Empty(await verify.QuotaUsage.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_admits_any_size_under_an_unlimited_fair_use_grant()
    {
        // An unlimited (null-limit) grant always has headroom: a huge declared size is admitted and the
        // increment is unconditional.
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: null, startingUsage: 0);

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 50_000_000_000, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.Created, result.Outcome);
        Assert.Equal(50_000_000_000, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_denies_fail_closed_when_the_quota_is_defined_but_the_workspace_holds_no_grant()
    {
        // The quota exists but THIS workspace has no grant -> no allowance -> denied fail-closed, nothing
        // persisted (the same fail-closed contract as session.active.max).
        var seed = await SeedWorkspaceAsync();
        await SeedStorageQuotaDefinitionAsync();

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 1, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.QuotaExceeded, result.Outcome);
        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_does_not_borrow_another_workspaces_storage_grant()
    {
        // A storage grant to a DIFFERENT workspace confers NO headroom here: the quota is keyed by the
        // (Workspace, workspaceId) subject, so this workspace (with no grant of its own) is denied (isolation).
        var seed = await SeedWorkspaceAsync();
        await SeedStorageQuotaDefinitionAsync();
        var otherWorkspaceId = Guid.CreateVersion7();
        await GrantStorageEntitlementAsync(otherWorkspaceId, limit: 1_000_000);

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage());
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 1, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.QuotaExceeded, result.Outcome);
        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_mints_distinct_object_keys_for_two_intents()
    {
        var seed = await SeedWorkspaceAsync();
        await SeedStorageQuotaAsync(seed.WorkspaceId, limit: null, startingUsage: 0);

        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage());
        var first = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 10, _now, CancellationToken.None);
        var second = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 10, _now, CancellationToken.None);

        Assert.NotEqual(first.Intent!.Asset.ObjectKey, second.Intent!.Asset.ObjectKey);
        Assert.NotEqual(first.Intent.Asset.Id, second.Intent.Asset.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a content type")]
    public async Task CreateAsync_rejects_an_invalid_content_type(string contentType)
    {
        var seed = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, contentType, 10, _now, CancellationToken.None));

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateAsync_rejects_a_non_positive_declared_size(long sizeBytes)
    {
        var seed = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", sizeBytes, _now, CancellationToken.None));

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_identity_inputs()
    {
        var seed = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            Guid.Empty, seed.WorkspaceId, seed.UserId, "image/png", 10, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            seed.OrganizationId, Guid.Empty, seed.UserId, "image/png", 10, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, Guid.Empty, "image/png", 10, _now, CancellationToken.None));
    }

    // =====================================================================
    // CORE-AST-007 — configurable MIME allowlist + absolute per-object size ceiling, enforced before any quota
    // is consumed and before any signed upload URL is minted (fail-closed; the privacy model is unchanged).
    // =====================================================================

    /// <summary>A restrictive policy the hardening tests enforce against: only image/png, ceiling 1,000,000 bytes.</summary>
    private static readonly AssetUploadConstraints _restrictedConstraints =
        new(new[] { "image/png" }, maxObjectSizeBytes: 1_000_000);

    [Fact]
    public async Task CreateAsync_rejects_a_content_type_not_on_the_allowlist_and_persists_and_consumes_nothing()
    {
        // The workspace has ample headroom, but application/zip is not on the allowlist -> rejected before any
        // quota is consumed or any URL is minted. Nothing is persisted and the recorded usage is unchanged.
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 1_000_000, startingUsage: 100_000);

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage(), _restrictedConstraints);
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "application/zip", 10, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.ContentTypeNotAllowed, result.Outcome);
        Assert.Null(result.Intent);

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
        // The allowlist check precedes the quota consume: the recorded usage is untouched.
        Assert.Equal(100_000, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_allows_a_content_type_on_the_allowlist_case_insensitively()
    {
        // The allowlist match is case-insensitive on the declared MIME token, so IMAGE/PNG is admitted.
        var seed = await SeedWorkspaceAsync();

        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage(), _restrictedConstraints);
        var result = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "IMAGE/PNG", 500, _now, CancellationToken.None);

        Assert.Equal(AssetUploadIntentOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_rejects_an_object_over_the_ceiling_and_persists_and_consumes_nothing()
    {
        // The declared size (2,000,000) exceeds the absolute per-object ceiling (1,000,000), independent of the
        // workspace quota -> rejected before any quota is consumed or any URL is minted. The result carries the
        // (non-sensitive) ceiling so the endpoint can phrase the 413.
        var seed = await SeedWorkspaceAsync();
        var quotaId = await SeedStorageQuotaAsync(seed.WorkspaceId, limit: 100_000_000, startingUsage: 100_000);

        AssetUploadIntentResult result;
        await using (var context = CreateContext())
        {
            var service = CreateService(context, new FakeSignedUrlAssetStorage(), _restrictedConstraints);
            result = await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 2_000_000, _now, CancellationToken.None);
        }

        Assert.Equal(AssetUploadIntentOutcome.ObjectTooLarge, result.Outcome);
        Assert.Equal(1_000_000, result.SizeCeilingBytes);
        Assert.Null(result.Intent);

        await using var verify = CreateContext();
        Assert.Empty(await verify.Assets.AsNoTracking().ToListAsync());
        // The ceiling check precedes the quota consume: the recorded usage is untouched.
        Assert.Equal(100_000, await ReadUsageAsync(seed.WorkspaceId, quotaId));
    }

    [Fact]
    public async Task CreateAsync_admits_an_allowed_in_limit_upload_under_a_restrictive_policy()
    {
        // image/png is allowed and 1,000,000 is exactly at the ceiling (inclusive): the intent is created.
        var seed = await SeedWorkspaceAsync();

        await using var context = CreateContext();
        var service = CreateService(context, new FakeSignedUrlAssetStorage(), _restrictedConstraints);
        var result = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 1_000_000, _now, CancellationToken.None);

        Assert.Equal(AssetUploadIntentOutcome.Created, result.Outcome);
        Assert.Equal(1_000_000, result.Intent!.Asset.SizeBytes);
    }

    [Fact]
    public async Task CreateAsync_does_not_reach_the_storage_adapter_when_the_content_type_is_disallowed()
    {
        // A disallowed content type is rejected BEFORE the unit of work opens, so the storage adapter is never
        // called: substituting the fail-closed UnconfiguredAssetStorage yields the rejection outcome, not a
        // thrown AssetStorageNotConfiguredException. This proves no URL is minted for a rejected intent.
        var seed = await SeedWorkspaceAsync();

        await using var context = CreateContext();
        var service = CreateService(context, new UnconfiguredAssetStorage(), _restrictedConstraints);
        var result = await service.CreateAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.UserId, "application/zip", 10, _now, CancellationToken.None);

        Assert.Equal(AssetUploadIntentOutcome.ContentTypeNotAllowed, result.Outcome);
    }

    // =====================================================================
    // CORE-AST-008 — the command hands the storage adapter the DECLARED content type and size, so the adapter
    // can bind them into the signed upload URL (the binding itself is covered by S3CompatibleAssetStorageTests).
    // =====================================================================

    [Fact]
    public async Task CreateAsync_hands_the_storage_adapter_the_declared_content_type_and_size_to_bind()
    {
        // The S3 adapter binds the DECLARED content type and size into the signed upload URL (CORE-AST-008), so
        // the command must hand it an asset carrying exactly those declared values — the same ones the
        // CORE-AST-007 acceptance policy and the CORE-MON-006 quota already saw — so the bound storage conditions
        // match what was authorized and a declare-small / upload-large bypass is impossible.
        var seed = await SeedWorkspaceAsync();
        await SeedStorageQuotaAsync(seed.WorkspaceId, limit: null, startingUsage: 0);

        var capturing = new CapturingAssetStorage();
        await using (var context = CreateContext())
        {
            var service = CreateService(context, capturing);
            await service.CreateAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.UserId, "image/png", 4096, _now, CancellationToken.None);
        }

        var signedAsset = Assert.Single(capturing.UploadAssets);
        Assert.Equal("image/png", signedAsset.ContentType);
        Assert.Equal(4096, signedAsset.SizeBytes);
    }

    // =====================================================================
    // Seeding helpers.
    // =====================================================================

    private async Task<Seed> SeedWorkspaceAsync()
    {
        await using var context = CreateContext();
        var user = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "host-a"), _now);
        context.UserProfiles.Add(user);
        var org = Organization.Create("northwind-labs", "Northwind Labs", _now);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        var workspace = Workspace.Create(org.Id, "summer-show", "Summer Show", _now);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        return new Seed(org.Id, workspace.Id, user.Id);
    }

    /// <summary>Seeds the storage quota definition (Workspace subject, Bytes unit) and returns it.</summary>
    private async Task<QuotaDefinition> SeedStorageQuotaDefinitionAsync()
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(
            QuotaEntitlementKeys.AssetStorageBytesMax, EntitlementValueKind.Quota, "Storage quota", null, _now);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.Workspace, QuotaUnit.Bytes, _now);
        context.QuotaDefinitions.Add(quota);
        await context.SaveChangesAsync();
        return quota;
    }

    /// <summary>
    /// Seeds the storage quota definition plus a grant to the given workspace (a numeric byte limit, or null
    /// for an unlimited/fair-use grant) and an optional starting usage. Returns the quota definition id.
    /// </summary>
    private async Task<Guid> SeedStorageQuotaAsync(Guid workspaceId, long? limit, long startingUsage)
    {
        var quota = await SeedStorageQuotaDefinitionAsync();
        await GrantStorageEntitlementAsync(workspaceId, limit);

        if (startingUsage != 0)
        {
            await using var context = CreateContext();
            var usage = QuotaUsage.Start(EntitlementSubjectType.Workspace, workspaceId, quota, _now);
            usage.Record(startingUsage, _now);
            context.QuotaUsage.Add(usage);
            await context.SaveChangesAsync();
        }

        return quota.Id;
    }

    /// <summary>Grants the (already-defined) storage entitlement to a workspace subject at the given limit.</summary>
    private async Task GrantStorageEntitlementAsync(Guid workspaceId, long? limit)
    {
        await using var context = CreateContext();
        var entitlement = await context.EntitlementDefinitions
            .SingleAsync(e => e.Key == QuotaEntitlementKeys.AssetStorageBytesMax);
        context.SubjectEntitlements.Add(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.Workspace, workspaceId, entitlement, limit, null, _now));
        await context.SaveChangesAsync();
    }

    private async Task<long> ReadUsageAsync(Guid workspaceId, Guid quotaDefinitionId)
    {
        await using var context = CreateContext();
        var usage = await new QuotaUsageRepository(context).FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.Workspace, workspaceId, quotaDefinitionId, CancellationToken.None);
        return usage?.UsedAmount ?? 0;
    }

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid UserId);

    /// <summary>
    /// A conforming in-memory <see cref="IAssetStorage"/> standing in for the deployment-supplied
    /// S3-compatible adapter: it mints a short-lived signed URL for the asset's own coordinates and can never
    /// produce a public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that unrepresentable.
    /// Not a production signer.
    /// </summary>
    private sealed class FakeSignedUrlAssetStorage : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=put&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Upload, _now, _lifetime));
        }

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=get&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Download, _now, _lifetime));
        }

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A conforming <see cref="IAssetStorage"/> that RECORDS the assets it is asked to mint an upload URL for,
    /// so a test can assert the command handed the adapter the declared content type and size the adapter binds
    /// into the signed URL (CORE-AST-008). It mints a valid short-lived signed URL like
    /// <see cref="FakeSignedUrlAssetStorage"/>; it is not a production signer.
    /// </summary>
    private sealed class CapturingAssetStorage : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

        public List<Asset> UploadAssets { get; } = [];

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            UploadAssets.Add(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=put&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Upload, _now, _lifetime));
        }

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
