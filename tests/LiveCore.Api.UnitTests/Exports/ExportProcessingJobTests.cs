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
///   <item>MAX-ATTEMPT + DEAD-LETTER (CORE-RES-002): a structurally-broken export (its manifest insert always
///   fails) increments its attempt counter on each sweep, is dead-lettered to the terminal <c>Failed</c> state
///   once it reaches the configured maximum (its <c>Fail()</c> reason recorded, content-free), then stops being
///   queued; a permanently-failing low-id job no longer blocks newer work in its batch; and a failed job in a
///   batch is counted without aborting the rest of the run. The dead-letter path is exercised here, against real
///   persistence, because it depends on a real rolled-back work transaction.</item>
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

    // ---- CORE-RES-002: max-attempt counting and dead-lettering -------------------------------------------------

    [Fact]
    public async Task A_structurally_broken_export_increments_its_attempt_counter_and_is_dead_lettered_to_failed()
    {
        // A structurally-broken export: its manifest insert ALWAYS fails (modeled by a manifest repository that
        // throws for this job over the real, shared per-sweep context, so the work transaction really rolls
        // back). It must not be retried forever: each sweep records one failed attempt durably, and once the
        // attempt count reaches the configured maximum the worker dead-letters the job — ExportJob.Fail() is
        // invoked so it ends terminal Failed (not Pending forever) and surfaces as failed to its requester.
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync("subject-a");
        var job = await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await RunFailingSweepAsync(maxAttempts, batchSize: 50, poisonJobId: job.Id);

            Assert.Equal(1, result.Examined);
            Assert.Equal(0, result.Processed);
            if (attempt < maxAttempts)
            {
                // Below the bound: counted as a retryable failure, still queued.
                Assert.Equal(1, result.Failed);
                Assert.Equal(0, result.DeadLettered);
            }
            else
            {
                // The attempt that reaches the bound dead-letters the job.
                Assert.Equal(0, result.Failed);
                Assert.Equal(1, result.DeadLettered);
            }

            // The attempt counter increments by exactly one per sweep and stays non-terminal until the bound.
            await using var checkContext = CreateContext();
            var current = await new ExportJobRepository(checkContext)
                .FindByIdAsync(organization.Id, workspace.Id, job.Id, CancellationToken.None);
            Assert.NotNull(current);
            Assert.Equal(attempt, current.AttemptCount);
            Assert.Equal(
                attempt < maxAttempts ? ExportJobStatus.Pending : ExportJobStatus.Failed,
                current.Status);
        }

        // Dead-lettered: terminal Failed with a generic, content-free failure reason (never the requester or any
        // exported content; threats T7/T8).
        await using (var assertContext = CreateContext())
        {
            var deadLettered = await new ExportJobRepository(assertContext)
                .FindByIdAsync(organization.Id, workspace.Id, job.Id, CancellationToken.None);
            Assert.NotNull(deadLettered);
            Assert.Equal(ExportJobStatus.Failed, deadLettered.Status);
            Assert.Equal(maxAttempts, deadLettered.AttemptCount);
            Assert.False(string.IsNullOrWhiteSpace(deadLettered.FailureReason));
            Assert.DoesNotContain("subject-a", deadLettered.FailureReason!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("summer-show", deadLettered.FailureReason!, StringComparison.OrdinalIgnoreCase);
        }

        // BOUNDED: a dead-lettered (terminal) job is no longer queued, so a further sweep examines nothing — it
        // has stopped consuming a batch slot and the counter never climbs past the maximum.
        var afterDeadLetter = await RunFailingSweepAsync(maxAttempts, batchSize: 50, poisonJobId: job.Id);
        Assert.Equal(0, afterDeadLetter.Examined);

        await using (var finalContext = CreateContext())
        {
            var final = await new ExportJobRepository(finalContext)
                .FindByIdAsync(organization.Id, workspace.Id, job.Id, CancellationToken.None);
            Assert.Equal(maxAttempts, final!.AttemptCount);
        }
    }

    [Fact]
    public async Task A_dead_lettered_poison_job_stops_consuming_a_batch_slot_so_newer_work_progresses()
    {
        // A batch size of one means a single sweep processes only the lowest-id queued job. A poison job that
        // fails forever used to occupy that one slot every sweep, starving newer work; once it is dead-lettered
        // it drops out of the queued read and the newer job is finally processed — newer work progresses past
        // the poison item.
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync("subject-a");
        await SeedSessionsAsync(organization.Id, workspace.Id, count: 1);
        await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);

        // The poison is whichever job sorts FIRST by the time-ordered surrogate id — the exact order the bounded
        // queued read uses — so with a batch size of one it is the job read until it is dead-lettered. Determined
        // by the database's own ordering (not a .NET Guid comparison, which differs from SQL ordering).
        Guid poisonId;
        Guid newerId;
        await using (var idContext = CreateContext())
        {
            var ordered = await idContext.ExportJobs
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .ToListAsync(CancellationToken.None);
            poisonId = ordered[0];
            newerId = ordered[1];
        }

        const int maxAttempts = 2;

        // Sweeps 1 and 2 read only the poison (the single batch slot); it fails, then dead-letters.
        var sweep1 = await RunFailingSweepAsync(maxAttempts, batchSize: 1, poisonJobId: poisonId);
        Assert.Equal(1, sweep1.Examined);
        Assert.Equal(1, sweep1.Failed);

        var sweep2 = await RunFailingSweepAsync(maxAttempts, batchSize: 1, poisonJobId: poisonId);
        Assert.Equal(1, sweep2.Examined);
        Assert.Equal(1, sweep2.DeadLettered);

        // While the poison still held the slot the newer job was never touched.
        await using (var midContext = CreateContext())
        {
            var newer = await new ExportJobRepository(midContext)
                .FindByIdAsync(organization.Id, workspace.Id, newerId, CancellationToken.None);
            Assert.Equal(ExportJobStatus.Pending, newer!.Status);
            Assert.Equal(0, await midContext.ExportManifests.CountAsync(CancellationToken.None));
        }

        // Sweep 3: the poison is terminal, so the queued read now surfaces the NEWER job and processes it to
        // completion — it progressed past the poison item that no longer consumes the slot.
        var sweep3 = await RunFailingSweepAsync(maxAttempts, batchSize: 1, poisonJobId: poisonId);
        Assert.Equal(1, sweep3.Examined);
        Assert.Equal(1, sweep3.Processed);

        await using (var assertContext = CreateContext())
        {
            var jobs = new ExportJobRepository(assertContext);
            var newer = await jobs.FindByIdAsync(organization.Id, workspace.Id, newerId, CancellationToken.None);
            Assert.Equal(ExportJobStatus.Completed, newer!.Status);
            var poison = await jobs.FindByIdAsync(organization.Id, workspace.Id, poisonId, CancellationToken.None);
            Assert.Equal(ExportJobStatus.Failed, poison!.Status);
            var manifest = await new ExportManifestRepository(assertContext)
                .FindByExportJobIdAsync(organization.Id, workspace.Id, newerId, CancellationToken.None);
            Assert.NotNull(manifest);
        }
    }

    [Fact]
    public async Task A_failed_job_in_a_batch_is_counted_and_the_rest_of_the_batch_still_completes()
    {
        // A persistence failure for one job must NOT abort the sweep nor corrupt the others: the failing job is
        // counted (and its attempt recorded), and the healthy job in the same batch still completes with its
        // manifest. This also guards the shared per-sweep context: the failing job's rolled-back transitions must
        // not leak into the healthy job's commit (the worker reloads the durable row to record the attempt).
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync("subject-a");
        await SeedSessionsAsync(organization.Id, workspace.Id, count: 2);
        await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);
        await SeedPendingJobAsync(organization.Id, workspace.Id, user.Id);

        // The poison is the lower-id job so it is processed BEFORE the healthy one in the same sweep (the queued
        // read is ordered by id), which is the order that would expose any leak of its rolled-back state.
        Guid poisonId;
        Guid healthyId;
        await using (var idContext = CreateContext())
        {
            var ordered = await idContext.ExportJobs
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .ToListAsync(CancellationToken.None);
            poisonId = ordered[0];
            healthyId = ordered[1];
        }

        var result = await RunFailingSweepAsync(maxAttempts: 3, batchSize: 50, poisonJobId: poisonId);

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.DeadLettered);

        await using var assertContext = CreateContext();
        var jobs = new ExportJobRepository(assertContext);

        // The healthy job completed with its manifest.
        var healthy = await jobs.FindByIdAsync(organization.Id, workspace.Id, healthyId, CancellationToken.None);
        Assert.Equal(ExportJobStatus.Completed, healthy!.Status);
        var manifest = await new ExportManifestRepository(assertContext)
            .FindByExportJobIdAsync(organization.Id, workspace.Id, healthyId, CancellationToken.None);
        Assert.NotNull(manifest);

        // The poison job stayed non-terminal with exactly one recorded attempt and produced no manifest — its
        // rolled-back transitions did not leak into the healthy job's commit.
        var poison = await jobs.FindByIdAsync(organization.Id, workspace.Id, poisonId, CancellationToken.None);
        Assert.Equal(ExportJobStatus.Pending, poison!.Status);
        Assert.Equal(1, poison.AttemptCount);
        var poisonManifest = await new ExportManifestRepository(assertContext)
            .FindByExportJobIdAsync(organization.Id, workspace.Id, poisonId, CancellationToken.None);
        Assert.Null(poisonManifest);
    }

    /// <summary>
    /// Runs one export processing sweep over a fresh per-sweep context (as a production DI scope would) with the
    /// manifest insert forced to FAIL for <paramref name="poisonJobId"/> — modeling a structurally-broken export
    /// whose manifest can never be produced — while every other collaborator is the real EF-backed implementation.
    /// </summary>
    private async Task<ExportProcessingResult> RunFailingSweepAsync(int maxAttempts, int batchSize, Guid poisonJobId)
    {
        await using var runContext = CreateContext();
        var manifests = new FailingExportManifestRepository(new ExportManifestRepository(runContext), poisonJobId);
        var service = new ExportProcessingService(
            new QueuedExportJobReader(runContext),
            new ExportJobRepository(runContext),
            new WorkspaceExportInventoryReader(runContext),
            manifests,
            new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize, maxAttempts),
            new FixedTimeProvider(_processedAt),
            NullLogger<ExportProcessingService>.Instance);
        return await service.ProcessQueuedExportsAsync(CancellationToken.None);
    }

    private static int EntryCount(ExportManifest manifest, ExportResourceKind kind)
        => manifest.Entries.Single(entry => entry.Kind == kind).ItemCount;

    /// <summary>
    /// A manifest repository decorator that throws a <see cref="DbUpdateException"/> from
    /// <see cref="AddAsync"/> for a single designated export job id (a structurally-broken export whose manifest
    /// can never be persisted), delegating every other call — and every other job's manifest add — to the real
    /// repository over the same context. It throws BEFORE adding the manifest, so the in-flight work transaction
    /// never commits and the job's transitions are left to roll back, exactly as a real persistence failure
    /// would (CORE-RES-002).
    /// </summary>
    private sealed class FailingExportManifestRepository : IExportManifestRepository
    {
        private readonly IExportManifestRepository _inner;
        private readonly Guid _poisonJobId;

        public FailingExportManifestRepository(IExportManifestRepository inner, Guid poisonJobId)
        {
            _inner = inner;
            _poisonJobId = poisonJobId;
        }

        public Task<ExportManifestAddResult> AddAsync(ExportManifest manifest, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (manifest.ExportJobId == _poisonJobId)
            {
                throw new DbUpdateException($"simulated structural persistence failure for export job {manifest.ExportJobId}");
            }

            return _inner.AddAsync(manifest, cancellationToken);
        }

        public Task<ExportManifest?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => _inner.FindByIdAsync(organizationId, workspaceId, id, cancellationToken);

        public Task<ExportManifest?> FindByExportJobIdAsync(Guid organizationId, Guid workspaceId, Guid exportJobId, CancellationToken cancellationToken)
            => _inner.FindByExportJobIdAsync(organizationId, workspaceId, exportJobId, cancellationToken);
    }

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
