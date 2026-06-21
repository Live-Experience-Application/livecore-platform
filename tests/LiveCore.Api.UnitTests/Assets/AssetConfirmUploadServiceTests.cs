// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Integration-style tests for the <see cref="AssetConfirmUploadService"/> (CORE-ALC-001, the "Asset Lifecycle
/// and Attachment Completeness" epic), the command behind <c>POST /api/v1/assets/{assetId}/confirm-upload</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, SQL translation, the guarded <see cref="Asset.MarkAvailable"/> transition, the
/// asset update and the single transaction (the asset update + the audit append) are exercised on every run
/// without a database server — exactly like the asset deletion service tests. The confirm flow uses NO object
/// storage, so there is no storage seam to fake.
///
/// Coverage (the story's "the transition is audited; it is the only Pending-to-Available transition and is
/// fail-closed; tenant/workspace-scoped"):
/// <list type="bullet">
///   <item>HAPPY PATH: confirming a pending asset records its uploaded size/checksum and moves it to
///   Available.</item>
///   <item>AUDIT: a successful confirmation appends exactly one append-only
///   <see cref="AuditAction.AssetConfirmed"/> record naming the actor, the asset and the Pending-&gt;Available
///   state pair.</item>
///   <item>FAIL-CLOSED: confirming a non-Pending (already Available) asset returns
///   <see cref="AssetConfirmUploadOutcome.NotPending"/> and changes nothing (no audit, no overwrite of the
///   recorded size/checksum).</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling workspace and an id in
///   another tenant all return <see cref="AssetConfirmUploadOutcome.NotFound"/> and change NOTHING (the
///   asset stays Pending and no audit record is written).</item>
///   <item>ARGUMENT GUARDS: empty required ids, a negative size and an invalid checksum are rejected.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AssetConfirmUploadServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";
    private const long _sizeBytes = 4096;
    private const string _checksum = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _confirmTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetConfirmUploadServiceTests()
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
    private static AssetConfirmUploadService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new AssetRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Confirm_marks_a_pending_asset_available_and_records_size_and_checksum()
    {
        var seeded = await SeedAssetAsync(available: false);

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ConfirmAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor,
                _sizeBytes, _checksum, _confirmTime, CancellationToken.None);

            Assert.Equal(AssetConfirmUploadOutcome.Confirmed, result.Outcome);
            Assert.NotNull(result.Asset);
            Assert.Equal(AssetStatus.Available, result.Asset.Status);
        }

        await using var verify = CreateContext();
        var asset = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
        Assert.Equal(AssetStatus.Available, asset.Status);
        Assert.Equal(_sizeBytes, asset.SizeBytes);
        Assert.Equal(_checksum, asset.Checksum);
        Assert.Equal(_confirmTime, asset.UpdatedAt);
    }

    [Fact]
    public async Task Confirm_appends_one_asset_confirmed_audit_record_with_the_state_transition()
    {
        var seeded = await SeedAssetAsync(available: false);

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.ConfirmAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor,
                _sizeBytes, _checksum, _confirmTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.AssetConfirmed)
            .ToListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(seeded.OrganizationId, entry.OrganizationId);
        Assert.Equal(seeded.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seeded.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(Asset), entry.ResourceType);
        Assert.Equal(seeded.AssetId, entry.ResourceId);
        // A confirmation is a real state transition: it records the before/after status names.
        Assert.Equal(nameof(AssetStatus.Pending), entry.PreviousState);
        Assert.Equal(nameof(AssetStatus.Available), entry.NewState);
    }

    [Fact]
    public async Task Confirm_returns_not_pending_for_an_already_available_asset_and_changes_nothing()
    {
        // An already-confirmed asset has its own recorded size/checksum (set by MarkAvailable in the seed).
        var seeded = await SeedAssetAsync(available: true);

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ConfirmAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor,
                999, "deadbeef", _confirmTime, CancellationToken.None);

            Assert.Equal(AssetConfirmUploadOutcome.NotPending, result.Outcome);
            Assert.Null(result.Asset);
        }

        await using var verify = CreateContext();
        var asset = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
        Assert.Equal(AssetStatus.Available, asset.Status);
        // The rejected re-confirm overwrote nothing.
        Assert.NotEqual(999, asset.SizeBytes);
        Assert.NotEqual("deadbeef", asset.Checksum);
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetConfirmed));
    }

    [Fact]
    public async Task Confirm_returns_not_found_for_an_unknown_asset_and_records_nothing()
    {
        var seeded = await SeedAssetAsync(available: false);

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ConfirmAsync(
                seeded.OrganizationId, seeded.WorkspaceId, Guid.CreateVersion7(), seeded.Actor,
                _sizeBytes, _checksum, _confirmTime, CancellationToken.None);

            Assert.Equal(AssetConfirmUploadOutcome.NotFound, result.Outcome);
        }

        await using var verify = CreateContext();
        // The seeded asset is untouched and nothing was audited.
        var asset = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetConfirmed));
    }

    [Fact]
    public async Task Confirm_through_a_sibling_workspace_is_not_found_and_keeps_the_asset_pending()
    {
        // The asset lives in workspace W; addressing it through sibling workspace W2 of the SAME org must never
        // reach it (workspace-scoped lookup; threat T1/T5).
        var seeded = await SeedAssetAsync(available: false);
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
            var service = CreateService(context);
            var result = await service.ConfirmAsync(
                seeded.OrganizationId, siblingWorkspaceId, seeded.AssetId, seeded.Actor,
                _sizeBytes, _checksum, _confirmTime, CancellationToken.None);

            Assert.Equal(AssetConfirmUploadOutcome.NotFound, result.Outcome);
        }

        await using var verify = CreateContext();
        var asset = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetConfirmed));
    }

    [Fact]
    public async Task Confirm_through_a_foreign_tenant_is_not_found_and_keeps_the_asset_pending()
    {
        // The asset lives in org A's workspace; addressing it with org B's id must never reach it (organization
        // boundary checked before workspace boundary; threat T5).
        var seeded = await SeedAssetAsync(available: false);
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
            var service = CreateService(context);
            var result = await service.ConfirmAsync(
                orgBId, seeded.WorkspaceId, seeded.AssetId, seeded.Actor,
                _sizeBytes, _checksum, _confirmTime, CancellationToken.None);

            Assert.Equal(AssetConfirmUploadOutcome.NotFound, result.Outcome);
        }

        await using var verify = CreateContext();
        var asset = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
        Assert.Equal(AssetStatus.Pending, asset.Status);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Confirm_rejects_empty_required_ids(bool org, bool workspace, bool asset, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            asset ? Guid.Empty : Guid.CreateVersion7(),
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _sizeBytes,
            _checksum,
            _confirmTime,
            CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_rejects_a_negative_size()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ConfirmAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            -1, _checksum, _confirmTime, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a checksum")]
    public async Task Confirm_rejects_an_invalid_checksum(string checksum)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            _sizeBytes, checksum, _confirmTime, CancellationToken.None));
    }

    /// <summary>
    /// Seeds one organization + workspace + actor and one asset (Pending when <paramref name="available"/> is
    /// false, already-confirmed Available otherwise). Returns the ids the tests assert on.
    /// </summary>
    private async Task<SeededAsset> SeedAssetAsync(bool available)
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

        var asset = Asset.Create(
            org.Id,
            workspace.Id,
            actor.Id,
            "s3",
            "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}",
            "image/png",
            _seedTime);
        if (available)
        {
            asset.MarkAvailable(2048, "abc123def456", _seedTime);
        }

        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        return new SeededAsset
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
            AssetId = asset.Id,
        };
    }

    private sealed class SeededAsset
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
        public Guid AssetId { get; init; }
    }
}
