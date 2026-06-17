// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="ExportManifestRepository"/> (CORE-AUD-003).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys =
/// ON</c>), so the real model mapping, SQL translation, the foreign keys into
/// <c>export_jobs</c>/<c>workspaces</c>/<c>organizations</c>, the one-to-many mapping to the
/// <c>export_manifest_entries</c> child rows, the per-job unique index and the scope/kind name conversions
/// are exercised on every run without any database server or Docker. The behaviors under test (scoped
/// equality lookups with the inventory loaded, full isolation between workspaces and between tenants, the
/// one-manifest-per-job uniqueness, the enums stored as their names and the cascade-on-delete from the
/// producing job and workspace) are relational semantics shared with PostgreSQL; provider-specific
/// verification happens against PostgreSQL in the deployment pipeline (livecore-deploy).
///
/// The epic acceptance is that "Audit/export/recap are generic and authorized". At the model + repository
/// level (no HTTP endpoints yet — the export endpoint is a later Exports story, exactly as CORE-AUD-002
/// deferred it):
/// <list type="bullet">
///   <item>ROUND-TRIP: a manifest and its per-kind inventory round-trip through the database; the scope and
///   kinds are stored as their stable NAMES.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 tenant isolation; threat T1 broken object-level
///   authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before workspace
///   boundary): a manifest in workspace W1 is never returned via workspace W2; a manifest in organization A
///   is never resolved via organization B; and the foreign keys reject a manifest referencing a non-existent
///   job — so a manifest can never escape its tenant/workspace boundary.</item>
///   <item>ONE MANIFEST PER JOB: a second manifest for the same job is rejected as a duplicate.</item>
///   <item>CASCADE: deleting the producing job or the workspace removes the manifest and its entries.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md). All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class ExportManifestRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _completedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<ExportResourceKind, int> _inventory =
        new Dictionary<ExportResourceKind, int>
        {
            [ExportResourceKind.Scene] = 3,
            [ExportResourceKind.ContentBlock] = 7,
            [ExportResourceKind.Asset] = 1,
        };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ExportManifestRepositoryTests()
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

    /// <summary>
    /// Seeds a COMPLETED workspace-scoped export job (the only state a manifest is produced from) and returns
    /// the persisted, completed aggregate so a manifest can be built from it.
    /// </summary>
    private async Task<ExportJob> SeedCompletedJobAsync(Guid organizationId, Guid workspaceId, Guid requestedBy)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedBy, ExportScope.Workspace, _createdAt);
        job.Start(_createdAt);
        job.Complete(_completedAt);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        Assert.Equal(ExportJobAddResult.Added, await repository.AddAsync(job, CancellationToken.None));
        return job;
    }

    private async Task<ExportManifest> SeedManifestAsync(ExportJob completedJob)
    {
        var manifest = ExportManifest.ForWorkspaceExport(completedJob, _inventory, _generatedAt);
        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);
        Assert.Equal(ExportManifestAddResult.Added, await repository.AddAsync(manifest, CancellationToken.None));
        return manifest;
    }

    [Fact]
    public async Task Manifest_and_its_inventory_round_trip_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        var seeded = await SeedManifestAsync(job);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(job.Id, loaded.ExportJobId);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(ExportScope.Workspace, loaded.Scope);
        Assert.Equal(ExportManifest.CurrentManifestVersion, loaded.ManifestVersion);
        Assert.Equal(_generatedAt, loaded.GeneratedAt);
        Assert.Equal(11, loaded.TotalItemCount);

        // The per-kind inventory round-trips with the manifest.
        Assert.Equal(3, loaded.Entries.Count);
        Assert.Equal(3, loaded.Entries.Single(entry => entry.Kind == ExportResourceKind.Scene).ItemCount);
        Assert.Equal(7, loaded.Entries.Single(entry => entry.Kind == ExportResourceKind.ContentBlock).ItemCount);
        Assert.Equal(1, loaded.Entries.Single(entry => entry.Kind == ExportResourceKind.Asset).ItemCount);
    }

    [Fact]
    public async Task FindByExportJobId_returns_the_manifest_with_its_inventory()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        var seeded = await SeedManifestAsync(job);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);
        var loaded = await repository.FindByExportJobIdAsync(
            organization.Id, workspace.Id, job.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(3, loaded.Entries.Count);
    }

    [Fact]
    public async Task Scope_and_kinds_are_stored_as_their_stable_names_in_the_database()
    {
        // The scope and kind columns persist the stable NAME, not the numeric value (HasConversion<string>).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedManifestAsync(job);

        await using var assertionContext = CreateContext();
        var storedScopes = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT scope AS Value FROM export_manifests")
            .ToListAsync(CancellationToken.None);
        var storedKinds = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT kind AS Value FROM export_manifest_entries ORDER BY kind")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(ExportScope.Workspace), Assert.Single(storedScopes));
        Assert.Equal(
            new[]
            {
                nameof(ExportResourceKind.Asset),
                nameof(ExportResourceKind.ContentBlock),
                nameof(ExportResourceKind.Scene),
            },
            storedKinds);
    }

    [Fact]
    public async Task FindById_for_a_missing_manifest_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task FindByExportJobId_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByExportJobIdAsync(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByExportJobIdAsync(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByExportJobIdAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_manifest_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a manifest exists in workspace W1 only. Looking it
        // up by id under workspace W2 must return null even though both the manifest and W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace1.Id, user.Id);
        var inW1 = await SeedManifestAsync(job);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);

        var foundInW1 = await repository.FindByIdAsync(organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);
        var byJobViaW2 = await repository.FindByExportJobIdAsync(organization.Id, workspace2.Id, job.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
        Assert.Null(byJobViaW2);
    }

    [Fact]
    public async Task A_manifest_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md: organization
        // boundary checked before workspace boundary): a manifest exists in a workspace owned by
        // organization A. Looking the SAME workspace id and manifest id up under organization B's id must
        // return null even though the workspace id and manifest id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organizationA.Id, workspaceInA.Id, user.Id);
        var inA = await SeedManifestAsync(job);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);

        var underA = await repository.FindByIdAsync(organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var byJobUnderB = await repository.FindByExportJobIdAsync(organizationB.Id, workspaceInA.Id, job.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
        Assert.Null(byJobUnderB);
    }

    [Fact]
    public async Task A_second_manifest_for_the_same_job_is_rejected_as_a_duplicate()
    {
        // Exactly one manifest per export job (the unique export_manifests(export_job_id) index): a second
        // add for the same job returns DuplicateExportJob and creates nothing.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedManifestAsync(job);

        var second = ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);
        var result = await repository.AddAsync(second, CancellationToken.None);

        Assert.Equal(ExportManifestAddResult.DuplicateExportJob, result);

        await using var assertionContext = CreateContext();
        Assert.Equal(1, await assertionContext.ExportManifests.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_manifest_cannot_reference_a_job_that_does_not_exist()
    {
        // The export_job_id foreign key is enforced (PRAGMA foreign_keys = ON): a manifest for a non-existent
        // job is rejected, so a dangling manifest can never exist outside a real export job (threats T1/T5).
        // The organization and workspace are real to isolate the job FK; the completed job is NOT persisted.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var unpersistedJob = ExportJob.Create(organization.Id, workspace.Id, Guid.CreateVersion7(), ExportScope.Workspace, _createdAt);
        unpersistedJob.Start(_createdAt);
        unpersistedJob.Complete(_completedAt);
        var ghost = ExportManifest.ForWorkspaceExport(unpersistedJob, _inventory, _generatedAt);

        await using var context = CreateContext();
        var repository = new ExportManifestRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_producing_job_cascades_to_the_manifest_and_its_entries()
    {
        // The export_job_id foreign key cascades on delete: a manifest has no meaning without its job, so
        // removing the job removes the manifest and its inventory entries.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedManifestAsync(job);

        await using (var deleteContext = CreateContext())
        {
            var storedJob = await deleteContext.ExportJobs.SingleAsync(j => j.Id == job.Id);
            deleteContext.ExportJobs.Remove(storedJob);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        Assert.Equal(0, await context.ExportManifests.CountAsync(CancellationToken.None));
        Assert.Equal(0, await context.ExportManifestEntries.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_workspace_cascades_to_the_manifest_and_its_entries()
    {
        // The workspace_id foreign key cascades on delete: removing the workspace removes its manifests (and
        // their entries, and the producing jobs).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var job = await SeedCompletedJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedManifestAsync(job);

        await using (var deleteContext = CreateContext())
        {
            var storedWorkspace = await deleteContext.Workspaces.SingleAsync(ws => ws.Id == workspace.Id);
            deleteContext.Workspaces.Remove(storedWorkspace);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        Assert.Equal(0, await context.ExportManifests.CountAsync(CancellationToken.None));
        Assert.Equal(0, await context.ExportManifestEntries.CountAsync(CancellationToken.None));
    }
}
