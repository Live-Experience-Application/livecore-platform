using LiveCore.Api.Assets;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// End-to-end integration-style test for the background export processing job (CORE-JOB-002). Unlike the
/// per-component tests, this wires the REAL <see cref="ExportProcessingService"/> over the REAL collaborators —
/// the <see cref="QueuedExportJobReader"/>, the <see cref="ExportJobRepository"/>, the
/// <see cref="WorkspaceExportInventoryReader"/> and the <see cref="ExportManifestRepository"/> — all sharing one
/// <see cref="LiveCoreDbContext"/> over an in-memory SQLite database with foreign keys enforced, exactly as the
/// worker's per-sweep dependency-injection scope wires them (the same in-memory SQLite harness the other
/// Exports data-layer tests use; provider-specific verification happens against PostgreSQL in the deployment
/// pipeline). It exercises the worker job's acceptance criteria against real persistence and the real
/// status-transition + manifest pipeline:
/// <list type="bullet">
///   <item>PROCESSED TO COMPLETION: a queued <c>Pending</c> workspace export job round-trips to <c>Completed</c>
///   and a workspace export manifest is persisted whose per-kind entries are the workspace's real inventory.</item>
///   <item>STATUS TRANSITIONS: the job ends in the terminal <c>Completed</c> state.</item>
///   <item>IDEMPOTENT: a second sweep produces nothing more (the job is terminal; the unique
///   <c>export_manifests(export_job_id)</c> index admits one manifest per job).</item>
///   <item>NEGATIVE TENANT ISOLATION (threat T5): a job in tenant A only ever counts tenant A's workspace
///   resources — never tenant B's — even when both tenants are swept together.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ExportProcessingJobTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _processedAt = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ExportProcessingJobTests()
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

    private ExportProcessingService CreateService(LiveCoreDbContext context)
        => new(
            new QueuedExportJobReader(context),
            new ExportJobRepository(context),
            new WorkspaceExportInventoryReader(context),
            new ExportManifestRepository(context),
            new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize: 50),
            new FixedTimeProvider(_processedAt),
            NullLogger<ExportProcessingService>.Instance);

    [Fact]
    public async Task Processes_a_queued_workspace_export_job_to_completion_with_its_inventory_manifest()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync("subject-a");
        await SeedSessionsAsync(organization.Id, workspace.Id, count: 3);
        await SeedAssetAsync(organization.Id, workspace.Id, user.Id);
        var job = await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var runContext = CreateContext())
        {
            var result = await CreateService(runContext).ProcessQueuedExportsAsync(CancellationToken.None);
            Assert.Equal(1, result.Examined);
            Assert.Equal(1, result.Processed);
            Assert.Equal(0, result.Failed);
        }

        // The job round-tripped to the terminal Completed state.
        await using (var assertContext = CreateContext())
        {
            var completed = await new ExportJobRepository(assertContext)
                .FindByIdAsync(organization.Id, workspace.Id, job.Id, CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal(ExportJobStatus.Completed, completed.Status);
            Assert.Equal(_processedAt, completed.UpdatedAt);

            // Exactly one manifest was produced, scoped to the job's tenant/workspace, inventorying the real
            // workspace resources (3 sessions, 1 asset; the other kinds 0). Counts only — never content.
            var manifest = await new ExportManifestRepository(assertContext)
                .FindByExportJobIdAsync(organization.Id, workspace.Id, job.Id, CancellationToken.None);
            Assert.NotNull(manifest);
            Assert.Equal(ExportScope.Workspace, manifest.Scope);
            Assert.Equal(_processedAt, manifest.GeneratedAt);
            Assert.Equal(6, manifest.Entries.Count);
            Assert.Equal(3, EntryCount(manifest, ExportResourceKind.Session));
            Assert.Equal(1, EntryCount(manifest, ExportResourceKind.Asset));
            Assert.Equal(0, EntryCount(manifest, ExportResourceKind.Scene));
            Assert.Equal(4, manifest.TotalItemCount);
        }
    }

    [Fact]
    public async Task A_second_sweep_produces_nothing_more()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync("subject-a");
        await SeedSessionsAsync(organization.Id, workspace.Id, count: 1);
        await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var firstContext = CreateContext())
        {
            var first = await CreateService(firstContext).ProcessQueuedExportsAsync(CancellationToken.None);
            Assert.Equal(1, first.Processed);
        }

        await using (var secondContext = CreateContext())
        {
            var second = await CreateService(secondContext).ProcessQueuedExportsAsync(CancellationToken.None);
            // The job is terminal, so it is no longer queued and nothing else is produced.
            Assert.Equal(0, second.Examined);
            Assert.Equal(0, second.Processed);
        }

        await using var assertContext = CreateContext();
        Assert.Equal(1, await assertContext.ExportManifests.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_jobs_manifest_only_counts_its_own_tenants_resources()
    {
        // Negative tenant isolation (threat T5): tenant A's workspace has 2 sessions; tenant B's workspace has 5.
        // Each tenant has its own queued workspace export job. Sweeping both at once must produce a manifest for
        // A counting 2 (never B's 5) and a manifest for B counting 5 (never A's 2).
        var organizationA = await SeedOrganizationAsync("northwind-labs");
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, "summer-show");
        var userA = await SeedUserAsync("subject-a");
        await SeedSessionsAsync(organizationA.Id, workspaceA.Id, count: 2);
        var jobA = await SeedPendingJobAsync(organizationA.Id, workspaceA.Id, userA.Id);

        var organizationB = await SeedOrganizationAsync("acme-co");
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, "winter-show");
        var userB = await SeedUserAsync("subject-b");
        await SeedSessionsAsync(organizationB.Id, workspaceB.Id, count: 5);
        var jobB = await SeedPendingJobAsync(organizationB.Id, workspaceB.Id, userB.Id);

        await using (var runContext = CreateContext())
        {
            var result = await CreateService(runContext).ProcessQueuedExportsAsync(CancellationToken.None);
            Assert.Equal(2, result.Examined);
            Assert.Equal(2, result.Processed);
        }

        await using var assertContext = CreateContext();
        var manifests = new ExportManifestRepository(assertContext);

        var manifestA = await manifests.FindByExportJobIdAsync(organizationA.Id, workspaceA.Id, jobA.Id, CancellationToken.None);
        Assert.NotNull(manifestA);
        Assert.Equal(2, EntryCount(manifestA, ExportResourceKind.Session));

        var manifestB = await manifests.FindByExportJobIdAsync(organizationB.Id, workspaceB.Id, jobB.Id, CancellationToken.None);
        Assert.NotNull(manifestB);
        Assert.Equal(5, EntryCount(manifestB, ExportResourceKind.Session));

        // A's manifest is not addressable under B's tenant (the cross-tenant lookup returns nothing).
        var crossTenant = await manifests.FindByExportJobIdAsync(organizationB.Id, workspaceA.Id, jobA.Id, CancellationToken.None);
        Assert.Null(crossTenant);
    }

    private static int EntryCount(ExportManifest manifest, ExportResourceKind kind)
        => manifest.Entries.Single(entry => entry.Kind == kind).ItemCount;

    // Seeding helpers --------------------------------------------------------------------------------------------

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

    private async Task<UserProfile> SeedUserAsync(string subject)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subject), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            UserProfileAddResult.Added,
            await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task SeedSessionsAsync(Guid organizationId, Guid workspaceId, int count)
    {
        await using var context = CreateContext();
        for (var index = 0; index < count; index++)
        {
            context.Sessions.Add(Session.Create(organizationId, workspaceId, $"session-{index}", _createdAt));
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdByUserProfileId)
    {
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            "s3",
            "private-bucket",
            "exports/object-key",
            "application/octet-stream",
            _createdAt);
        await using var context = CreateContext();
        context.Assets.Add(asset);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ExportJob> SeedPendingJobAsync(Guid organizationId, Guid workspaceId, Guid requestedBy)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedBy, ExportScope.Workspace, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            ExportJobAddResult.Added,
            await new ExportJobRepository(context).AddAsync(job, CancellationToken.None));
        return job;
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each transition and manifest timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
