using LiveCore.Api.Assets;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="AssetRepository"/> (CORE-AST-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys =
/// ON</c>), so the real model mapping, SQL translation, the foreign keys into
/// <c>organizations</c>/<c>workspaces</c>/<c>users</c>, the optional set-null creator link and the
/// status-name conversion are exercised on every test run without any database server or Docker. The
/// behaviors under test (scoped equality lookups, full isolation between workspaces and between tenants,
/// the lifecycle round-trip, the status stored as its name and the creator anonymization on user
/// deletion) are relational semantics shared with PostgreSQL; provider-specific verification happens
/// against PostgreSQL in the deployment pipeline (livecore-deploy).
///
/// The epic acceptance for Assets is that "assets are private by default and accessed only through
/// authorized signed URLs", with required "Upload/download auth tests and storage adapter tests". At the
/// metadata aggregate + repository level (no HTTP endpoints, no storage adapter and no signed URLs yet —
/// the storage adapter is CORE-AST-002, upload intent CORE-AST-003, signed download CORE-AST-004):
/// <list type="bullet">
///   <item>LIFECYCLE: an asset round-trips through the database and its Pending -&gt; Available confirm
///   transition (with the recorded size and checksum) persists; the status is stored as its stable
///   NAME.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat
///   T1 broken object-level authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked
///   before workspace boundary): an asset in workspace W1 is never returned via workspace W2; an asset in
///   organization A is never resolved via organization B; and the foreign keys reject an asset
///   referencing a non-existent workspace, organization or creating user — so a stored object reference
///   can never escape its tenant/workspace boundary (the foundation of "private by default",
///   threat T4).</item>
///   <item>CREATOR ANONYMIZATION: deleting the creating user sets the asset's <c>created_by</c> to null
///   (the asset survives, anonymized) rather than cascade-deleting the asset.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class AssetRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private const string _storageProvider = "s3";
    private const string _bucket = "livecore-private-assets";
    private const string _contentType = "image/png";
    private const string _checksum = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _confirmedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step still
        // uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK constraints in
        // the model are genuinely exercised.
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

    private async Task<UserProfile> SeedUserAsync(string issuer, string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId),
            _createdAt);
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        Assert.Equal(UserProfileAddResult.Added, await repository.AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<Asset> SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdBy, string objectKey)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, _storageProvider, _bucket, objectKey, _contentType, _createdAt);
        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        Assert.Equal(AssetAddResult.Added, await repository.AddAsync(asset, CancellationToken.None));
        return asset;
    }

    [Fact]
    public async Task Pending_asset_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/key-1.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(user.Id, loaded.CreatedByUserProfileId);
        Assert.Equal(_storageProvider, loaded.StorageProvider);
        Assert.Equal(_bucket, loaded.Bucket);
        Assert.Equal("org/ws/key-1.bin", loaded.ObjectKey);
        Assert.Equal(_contentType, loaded.ContentType);
        Assert.Equal(AssetStatus.Pending, loaded.Status);
        Assert.Null(loaded.SizeBytes);
        Assert.Null(loaded.Checksum);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lifecycle_confirm_persists_through_an_update()
    {
        // Lifecycle test: a pending asset is confirmed (marked available), and the transition with its
        // recorded size and checksum round-trips through the database.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/key-1.bin");

        await using (var context = CreateContext())
        {
            var repository = new AssetRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(AssetStatus.Pending, loaded.Status);

            loaded.MarkAvailable(4096, _checksum, _confirmedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new AssetRepository(context);
            var available = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(available);
            Assert.Equal(AssetStatus.Available, available.Status);
            Assert.Equal(4096, available.SizeBytes);
            Assert.Equal(_checksum, available.Checksum);
            Assert.Equal(_confirmedAt, available.UpdatedAt);
        }
    }

    [Fact]
    public async Task Status_is_stored_as_its_stable_name_in_the_database()
    {
        // The status column persists the stable NAME, not the numeric value (HasConversion<string>), so
        // the stored lifecycle value stays readable. Read the raw column back to assert the stored text.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/key-1.bin");

        await using (var context = CreateContext())
        {
            var repository = new AssetRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.MarkAvailable(4096, _checksum, _confirmedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using var assertionContext = CreateContext();
        var storedStatuses = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT status AS Value FROM assets")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(AssetStatus.Available), Assert.Single(storedStatuses));
    }

    [Fact]
    public async Task FindById_for_a_missing_asset_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task FindByIdInOrganization_returns_the_asset_within_its_tenant_regardless_of_workspace()
    {
        // The org-scoped lookup (CORE-AST-004) addresses an asset by id within a tenant only; the workspace
        // is discovered from the returned row afterwards. An asset in any workspace of the organization is
        // found, and its own workspace id is readable off the row so the caller can authorize against it.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/a.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var loaded = await repository.FindByIdInOrganizationAsync(
            organization.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        // The workspace is discovered from the row (it is not part of the predicate).
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
    }

    [Fact]
    public async Task FindByIdInOrganization_for_a_missing_asset_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var loaded = await repository.FindByIdInOrganizationAsync(
            organization.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindByIdInOrganization_for_an_asset_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5): the org-scoped download lookup must lead with
        // the organization id, so an asset owned by organization A is never returned through organization
        // B's id even though the surrogate asset id is correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedAssetAsync(organizationA.Id, workspaceInA.Id, user.Id, "org/ws/a.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        var underA = await repository.FindByIdInOrganizationAsync(organizationA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdInOrganizationAsync(organizationB.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task FindByIdInOrganization_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdInOrganizationAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdInOrganizationAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_returns_only_the_workspace_assets_in_id_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);

        var first = await SeedAssetAsync(organization.Id, workspace1.Id, user.Id, "org/ws1/a.bin");
        var second = await SeedAssetAsync(organization.Id, workspace1.Id, user.Id, "org/ws1/b.bin");
        // An asset in another workspace must never leak into W1's list.
        await SeedAssetAsync(organization.Id, workspace2.Id, user.Id, "org/ws2/c.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var listed = await repository.ListByWorkspaceAsync(organization.Id, workspace1.Id, CancellationToken.None);

        // Exactly W1's two assets, returned in the repository's deterministic ascending-id order. The two
        // ids are compared as a sorted sequence rather than against insertion order, because UUIDv7 is
        // only coarsely time-ordered (not strictly monotonic within a millisecond).
        var expectedIdsInOrder = new[] { first.Id, second.Id }.OrderBy(id => id).ToList();
        Assert.Equal(expectedIdsInOrder, listed.Select(asset => asset.Id).ToList());
    }

    [Fact]
    public async Task ListByWorkspace_returns_empty_for_a_workspace_with_no_assets()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var listed = await repository.ListByWorkspaceAsync(organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(listed);
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task An_asset_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): an asset exists in workspace W1 only. Looking it
        // up by id under workspace W2 must return null even though both the asset and W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedAssetAsync(organization.Id, workspace1.Id, user.Id, "org/ws1/a.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task An_asset_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md: organization
        // boundary checked before workspace boundary): an asset exists in a workspace owned by
        // organization A. Looking the SAME workspace id and asset id up under organization B's id must
        // return null even though the workspace id and asset id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedAssetAsync(organizationA.Id, workspaceInA.Id, user.Id, "org/ws/a.bin");

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task An_asset_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): an asset for a
        // non-existent workspace is rejected, so a dangling asset can never exist outside a real
        // workspace boundary (threat T5). The organization and user are real to isolate the workspace FK.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = Asset.Create(
            organization.Id, Guid.CreateVersion7(), user.Id, _storageProvider, _bucket, "org/ws/a.bin", _contentType, _createdAt);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task An_asset_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: an asset whose tenant does not exist is rejected
        // even when the workspace exists, so the row can never carry a tenant boundary that is not a real
        // organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = Asset.Create(
            Guid.CreateVersion7(), workspace.Id, user.Id, _storageProvider, _bucket, "org/ws/a.bin", _contentType, _createdAt);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task An_asset_cannot_reference_a_creating_user_that_does_not_exist()
    {
        // The created_by foreign key is enforced: an asset whose creator is not a real user is rejected,
        // so the recorded creator can never be a dangling reference.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Asset.Create(
            organization.Id, workspace.Id, Guid.CreateVersion7(), _storageProvider, _bucket, "org/ws/a.bin", _contentType, _createdAt);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_creating_user_anonymizes_the_asset_rather_than_deleting_it()
    {
        // The created_by foreign key sets null on delete: removing the creating user must NOT delete the
        // asset (other content may link to it). The asset survives with a null creator (anonymized), and
        // its log-safe rendering reflects that.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/a.bin");

        await using (var deleteContext = CreateContext())
        {
            var storedUser = await deleteContext.UserProfiles.SingleAsync(profile => profile.Id == user.Id);
            deleteContext.UserProfiles.Remove(storedUser);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.CreatedByUserProfileId);
        Assert.Contains("createdBy=anonymized", loaded.ToString());
    }

    [Fact]
    public async Task Deleting_the_workspace_cascades_to_its_assets()
    {
        // The workspace_id foreign key cascades on delete: an asset metadata row has no meaning without
        // its workspace, so removing the workspace removes its assets.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/a.bin");

        await using (var deleteContext = CreateContext())
        {
            var storedWorkspace = await deleteContext.Workspaces.SingleAsync(ws => ws.Id == workspace.Id);
            deleteContext.Workspaces.Remove(storedWorkspace);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var remaining = await context.Assets.CountAsync(CancellationToken.None);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task ListExpiredPending_returns_only_pending_assets_older_than_the_threshold()
    {
        // The cleanup sweep (CORE-AST-006) reclaims abandoned, unconfirmed (pending) upload intents older
        // than the grace window. It must return the OLD PENDING asset but never a confirmed (available)
        // asset (real content is never reclaimed, however old) and never a still-fresh pending asset (an
        // upload may still be in flight).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);

        var oldCreatedAt = new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero);
        var freshCreatedAt = new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
        var threshold = new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

        var oldPending = await SeedAssetAtAsync(organization.Id, workspace.Id, user.Id, "org/ws/old-pending.bin", oldCreatedAt);
        // An old but CONFIRMED asset must be excluded: confirmed content is never reclaimed.
        var oldAvailable = await SeedAvailableAssetAtAsync(
            organization.Id, workspace.Id, user.Id, "org/ws/old-available.bin", oldCreatedAt, _confirmedAt);
        // A fresh pending asset must be excluded: it is still within the grace window.
        await SeedAssetAtAsync(organization.Id, workspace.Id, user.Id, "org/ws/fresh-pending.bin", freshCreatedAt);

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var expired = await repository.ListExpiredPendingAsync(threshold, 100, CancellationToken.None);

        Assert.Equal(new[] { oldPending.Id }, expired.Select(asset => asset.Id).ToArray());
        Assert.DoesNotContain(expired, asset => asset.Id == oldAvailable.Id);
        Assert.All(expired, asset => Assert.Equal(AssetStatus.Pending, asset.Status));
    }

    [Fact]
    public async Task ListExpiredPending_spans_tenants_and_workspaces_oldest_first()
    {
        // The sweep is a SYSTEM maintenance job, not a tenant actor: it deliberately spans tenants and
        // workspaces (it returns only contentless pending rows, exclusively for deletion). The order is
        // deterministic — oldest creation time first.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);

        var threshold = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero);
        var olderInA = await SeedAssetAtAsync(
            organizationA.Id, workspaceA.Id, user.Id, "orgA/ws/older.bin",
            new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero));
        var newerInB = await SeedAssetAtAsync(
            organizationB.Id, workspaceB.Id, user.Id, "orgB/ws/newer.bin",
            new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero));

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var expired = await repository.ListExpiredPendingAsync(threshold, 100, CancellationToken.None);

        Assert.Equal(new[] { olderInA.Id, newerInB.Id }, expired.Select(asset => asset.Id).ToArray());
    }

    [Fact]
    public async Task ListExpiredPending_bounds_the_result_to_the_batch_size()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);

        var threshold = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero);
        await SeedAssetAtAsync(organization.Id, workspace.Id, user.Id, "org/ws/p1.bin", new DateTimeOffset(2026, 6, 10, 1, 0, 0, TimeSpan.Zero));
        await SeedAssetAtAsync(organization.Id, workspace.Id, user.Id, "org/ws/p2.bin", new DateTimeOffset(2026, 6, 10, 2, 0, 0, TimeSpan.Zero));
        await SeedAssetAtAsync(organization.Id, workspace.Id, user.Id, "org/ws/p3.bin", new DateTimeOffset(2026, 6, 10, 3, 0, 0, TimeSpan.Zero));

        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        var firstBatch = await repository.ListExpiredPendingAsync(threshold, 2, CancellationToken.None);

        Assert.Equal(2, firstBatch.Count);
    }

    [Fact]
    public async Task ListExpiredPending_rejects_a_non_positive_batch_size()
    {
        await using var context = CreateContext();
        var repository = new AssetRepository(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListExpiredPendingAsync(_createdAt, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListExpiredPendingAsync(_createdAt, -1, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_removes_the_asset_row()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedAssetAsync(organization.Id, workspace.Id, user.Id, "org/ws/a.bin");

        await using (var context = CreateContext())
        {
            var repository = new AssetRepository(context);
            var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            await repository.DeleteAsync(loaded, CancellationToken.None);
        }

        await using var assertionContext = CreateContext();
        var remaining = await assertionContext.Assets.CountAsync(CancellationToken.None);
        Assert.Equal(0, remaining);
    }

    private async Task<Asset> SeedAssetAtAsync(
        Guid organizationId, Guid workspaceId, Guid createdBy, string objectKey, DateTimeOffset createdAt)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, _storageProvider, _bucket, objectKey, _contentType, createdAt);
        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        Assert.Equal(AssetAddResult.Added, await repository.AddAsync(asset, CancellationToken.None));
        return asset;
    }

    private async Task<Asset> SeedAvailableAssetAtAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid createdBy,
        string objectKey,
        DateTimeOffset createdAt,
        DateTimeOffset confirmedAt)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, _storageProvider, _bucket, objectKey, _contentType, createdAt);
        asset.MarkAvailable(4096, _checksum, confirmedAt);
        await using var context = CreateContext();
        var repository = new AssetRepository(context);
        Assert.Equal(AssetAddResult.Added, await repository.AddAsync(asset, CancellationToken.None));
        return asset;
    }
}
