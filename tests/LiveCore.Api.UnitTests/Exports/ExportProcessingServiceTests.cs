using LiveCore.Api.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for <see cref="ExportProcessingService"/> (CORE-JOB-002) — the background export processing job's
/// application service, which processes every queued workspace export job into a workspace export manifest on
/// the worker's cadence, idempotently and tenant-scoped, driving each job through its guarded status
/// transitions. The service is driven over a stateful in-memory fake that plays ALL of its collaborators (the
/// queued-job reader, the export job repository, the workspace inventory reader and the manifest repository), so
/// the tests assert the job's own behavior without persistence: the fake's queued read returns the
/// workspace-scoped, non-terminal jobs and the fake's manifest add reports a duplicate exactly as the real
/// unique <c>export_manifests(export_job_id)</c> index would, modeling the idempotency anchor (verified
/// separately against SQLite).
///
/// The acceptance-criteria invariants are exercised directly:
/// <list type="bullet">
///   <item>PROCESSED TO COMPLETION: a queued job is started, inventoried, completed and given a manifest whose
///   per-kind entries are the workspace inventory.</item>
///   <item>STATUS TRANSITIONS: the job is driven through the guarded Pending -&gt; Running -&gt; Completed state
///   machine and ends Completed.</item>
///   <item>IDEMPOTENT: re-running the sweep processes nothing more once the queued jobs are completed; an
///   already-produced manifest (a duplicate add) leaves the job completed without producing a second manifest.</item>
///   <item>TENANT-SCOPED (threat T5): each job is inventoried and manifested with its OWN tenant/workspace —
///   one workspace's manifest never carries another workspace's counts.</item>
///   <item>SCOPE-BOUNDED (threat T8): a user-data export job is never processed into a workspace manifest.</item>
///   <item>RECOVERY: a job left Running by a crashed worker is finished (its already-applied Start is skipped).</item>
///   <item>BATCH SIZE: the configured batch size bounds a single sweep.</item>
/// </list>
/// The RESILIENCE and DEAD-LETTERING behavior (CORE-RES-002 — a per-job failure increments the attempt counter
/// and leaves the job for the next sweep, a poison job that exhausts its attempts is dead-lettered to Failed and
/// stops consuming a batch slot, and newer jobs progress past it) needs real transaction rollback to be modeled
/// faithfully, so it is exercised against SQLite in <c>ExportProcessingJobTests</c>.
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ExportProcessingServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 13, 11, 0, 0, TimeSpan.Zero);
    private static readonly ExportProcessingOptions _options = new(TimeSpan.FromHours(1), batchSize: 50);

    [Fact]
    public async Task Processes_a_queued_job_to_completion_with_a_manifest_of_the_workspace_inventory()
    {
        var store = new FakeExportStore();
        var job = store.SeedPendingWorkspaceJob();
        store.SetInventory(job.OrganizationId, job.WorkspaceId, Inventory(sessions: 3, scenes: 2, assets: 1));
        var service = CreateService(store);

        var result = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);

        // The job settled into the terminal Completed state.
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(_now, job.UpdatedAt);

        // Exactly one manifest was produced for the job, scoped to the job's own tenant/workspace/scope.
        var manifest = Assert.Single(store.Manifests);
        Assert.Equal(job.Id, manifest.ExportJobId);
        Assert.Equal(job.OrganizationId, manifest.OrganizationId);
        Assert.Equal(job.WorkspaceId, manifest.WorkspaceId);
        Assert.Equal(ExportScope.Workspace, manifest.Scope);
        Assert.Equal(_now, manifest.GeneratedAt);

        // The manifest's per-kind entries are exactly the workspace inventory (counts only, all six kinds).
        Assert.Equal(6, manifest.Entries.Count);
        Assert.Equal(3, EntryCount(manifest, ExportResourceKind.Session));
        Assert.Equal(2, EntryCount(manifest, ExportResourceKind.Scene));
        Assert.Equal(0, EntryCount(manifest, ExportResourceKind.ContentBlock));
        Assert.Equal(1, EntryCount(manifest, ExportResourceKind.Asset));
        Assert.Equal(6, manifest.TotalItemCount);
    }

    [Fact]
    public async Task Is_idempotent_a_second_sweep_processes_nothing_more()
    {
        var store = new FakeExportStore();
        store.SeedPendingWorkspaceJob();
        var service = CreateService(store);

        var first = await service.ProcessQueuedExportsAsync(CancellationToken.None);
        var second = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        // The first sweep processes the one job; the second finds it terminal (no longer queued), so it does
        // nothing — a job is processed exactly once across any number of sweeps.
        Assert.Equal(1, first.Processed);
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Processed);
        Assert.Single(store.Manifests);
    }

    [Fact]
    public async Task An_already_produced_manifest_is_handled_idempotently_without_a_second_manifest()
    {
        // A lost create race / concurrent worker: the unique export_manifests(export_job_id) index already holds
        // a manifest for this job. The manifest add reports a duplicate; the processor treats it as already-done
        // (its own transitions would roll back with the rejected insert in real persistence), never producing a
        // second manifest (threat T5/T8 — one manifest per job).
        var store = new FakeExportStore();
        var job = store.SeedPendingWorkspaceJob();
        store.PretendManifestExistsFor.Add(job.Id);
        var service = CreateService(store);

        var result = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        // No new manifest was stored — the existing one stands.
        Assert.Empty(store.Manifests);
    }

    [Fact]
    public async Task Recovers_a_job_left_running_by_a_crashed_worker_and_completes_it()
    {
        // A worker crashed after starting the job (Pending -> Running, persisted) but before producing the
        // manifest. The next sweep re-surfaces the still-Running job; the processor SKIPS the already-applied
        // Start and finishes it.
        var store = new FakeExportStore();
        var job = store.SeedRunningWorkspaceJob();
        var service = CreateService(store);

        var result = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        // The job completed without error — proving Start was correctly SKIPPED (calling Start on a Running job
        // would throw an invalid-transition exception and abort the sweep).
        Assert.Equal(1, result.Processed);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Single(store.Manifests);
    }

    [Fact]
    public async Task Processes_each_job_scoped_to_its_own_tenant_and_workspace()
    {
        // Tenant isolation (threat T5): two jobs in two different tenants/workspaces, each with its OWN
        // distinct inventory. Each produced manifest must carry its own job's organization/workspace and count
        // ITS OWN workspace's resources — never the other tenant's.
        var store = new FakeExportStore();
        var jobA = store.SeedPendingWorkspaceJob();
        var jobB = store.SeedPendingWorkspaceJob();
        store.SetInventory(jobA.OrganizationId, jobA.WorkspaceId, Inventory(sessions: 5));
        store.SetInventory(jobB.OrganizationId, jobB.WorkspaceId, Inventory(sessions: 9));
        var service = CreateService(store);

        await service.ProcessQueuedExportsAsync(CancellationToken.None);

        var manifestA = Assert.Single(store.Manifests, m => m.ExportJobId == jobA.Id);
        Assert.Equal(jobA.OrganizationId, manifestA.OrganizationId);
        Assert.Equal(jobA.WorkspaceId, manifestA.WorkspaceId);
        Assert.Equal(5, EntryCount(manifestA, ExportResourceKind.Session));

        var manifestB = Assert.Single(store.Manifests, m => m.ExportJobId == jobB.Id);
        Assert.Equal(jobB.OrganizationId, manifestB.OrganizationId);
        Assert.Equal(jobB.WorkspaceId, manifestB.WorkspaceId);
        Assert.Equal(9, EntryCount(manifestB, ExportResourceKind.Session));

        // Neither manifest borrowed the other tenant's counts.
        Assert.NotEqual(EntryCount(manifestA, ExportResourceKind.Session), EntryCount(manifestB, ExportResourceKind.Session));
    }

    [Fact]
    public async Task Never_processes_a_user_data_scoped_job()
    {
        // Scope boundary (threat T8): a UserData export's narrower manifest is a separate, later artifact. The
        // queued reader surfaces only workspace-scoped jobs, so a user-data job is never processed into a
        // workspace manifest and stays Pending.
        var store = new FakeExportStore();
        var workspaceJob = store.SeedPendingWorkspaceJob();
        var userDataJob = store.SeedPendingJob(ExportScope.UserData);
        var service = CreateService(store);

        var result = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Processed);
        Assert.Equal(ExportJobStatus.Completed, workspaceJob.Status);
        // The user-data job was never touched: still Pending, no manifest.
        Assert.Equal(ExportJobStatus.Pending, userDataJob.Status);
        Assert.DoesNotContain(store.Manifests, m => m.ExportJobId == userDataJob.Id);
    }

    [Fact]
    public async Task Passes_the_configured_batch_size_to_the_queued_read()
    {
        var store = new FakeExportStore();
        var options = new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize: 17);
        var service = new ExportProcessingService(
            store, store, store, store, options, new FixedTimeProvider(_now),
            NullLogger<ExportProcessingService>.Instance);

        await service.ProcessQueuedExportsAsync(CancellationToken.None);

        Assert.Equal(17, store.LastMaxCount);
    }

    [Fact]
    public async Task Does_nothing_when_no_job_is_queued()
    {
        var store = new FakeExportStore();
        var service = CreateService(store);

        var result = await service.ProcessQueuedExportsAsync(CancellationToken.None);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Processed);
        Assert.Empty(store.Manifests);
    }

    // The failed-job / dead-letter accounting (CORE-RES-002) needs real transaction rollback to be modeled
    // faithfully (the rolled-back work leaves the in-memory aggregate dirty; the worker reloads the durable row
    // to record the attempt), so the "a poison job is counted failed and the batch continues", "the attempt
    // counter increments and is bounded", and "a structurally-broken export is dead-lettered to Failed" cases
    // are exercised against SQLite in ExportProcessingJobTests rather than over this in-memory fake.

    private static ExportProcessingService CreateService(FakeExportStore store)
        => new(
            store,
            store,
            store,
            store,
            _options,
            new FixedTimeProvider(_now),
            NullLogger<ExportProcessingService>.Instance);

    private static Dictionary<ExportResourceKind, int> Inventory(
        int sessions = 0, int scenes = 0, int contentBlocks = 0, int entities = 0, int participants = 0, int assets = 0)
        => new()
        {
            [ExportResourceKind.Session] = sessions,
            [ExportResourceKind.Scene] = scenes,
            [ExportResourceKind.ContentBlock] = contentBlocks,
            [ExportResourceKind.Entity] = entities,
            [ExportResourceKind.Participant] = participants,
            [ExportResourceKind.Asset] = assets,
        };

    private static int EntryCount(ExportManifest manifest, ExportResourceKind kind)
        => manifest.Entries.Single(entry => entry.Kind == kind).ItemCount;

    /// <summary>
    /// A stateful in-memory fake that plays every collaborator of the processing service: the queued-job
    /// reader, the export job repository, the workspace inventory reader and the manifest repository. The stored
    /// <see cref="ExportJob"/> instances are mutated in place by the service's transitions (exactly as EF tracks
    /// and mutates the loaded aggregate), and the queued read returns the workspace-scoped, non-terminal jobs —
    /// so the idempotency property emerges from the data exactly as it does in production. Only the members the
    /// processing service uses are implemented; the rest throw.
    /// </summary>
    private sealed class FakeExportStore
        : IQueuedExportJobReader, IExportJobRepository, IWorkspaceExportInventoryReader, IExportManifestRepository
    {
        private static readonly Guid _requester = Guid.CreateVersion7();

        private readonly List<ExportJob> _jobs = [];
        private readonly List<ExportManifest> _manifests = [];
        private readonly Dictionary<(Guid Organization, Guid Workspace), IReadOnlyDictionary<ExportResourceKind, int>> _inventories = [];

        public IReadOnlyList<ExportManifest> Manifests => _manifests;

        public HashSet<Guid> FailManifestAddFor { get; } = [];

        public HashSet<Guid> PretendManifestExistsFor { get; } = [];

        public int? LastMaxCount { get; private set; }

        public ExportJob SeedPendingJob(ExportScope scope)
        {
            // A distinct tenant + workspace per job so the tests can assert per-job tenant scoping.
            var job = ExportJob.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), _requester, scope, _createdAt);
            _jobs.Add(job);
            return job;
        }

        public ExportJob SeedPendingWorkspaceJob() => SeedPendingJob(ExportScope.Workspace);

        public ExportJob SeedRunningWorkspaceJob()
        {
            var job = SeedPendingJob(ExportScope.Workspace);
            job.Start(_createdAt);
            return job;
        }

        public void SetInventory(Guid organizationId, Guid workspaceId, IReadOnlyDictionary<ExportResourceKind, int> inventory)
            => _inventories[(organizationId, workspaceId)] = inventory;

        // IQueuedExportJobReader -------------------------------------------------------------------------------

        public Task<IReadOnlyList<QueuedExportJob>> ListQueuedWorkspaceExportsAsync(int maxCount, CancellationToken cancellationToken)
        {
            LastMaxCount = maxCount;
            IReadOnlyList<QueuedExportJob> queued = _jobs
                .Where(job => job.Scope == ExportScope.Workspace && !job.IsTerminal)
                .OrderBy(job => job.Id)
                .Take(maxCount)
                .Select(job => new QueuedExportJob(
                    job.OrganizationId, job.WorkspaceId, job.Id, job.LeaseOwner, job.LeasedUntil))
                .ToList();
            return Task.FromResult(queued);
        }

        // IExportJobRepository (claim) -------------------------------------------------------------------------

        public Task<bool> ClaimAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid id,
            Guid? observedLeaseOwner,
            Guid leaseOwner,
            DateTimeOffset leasedUntil,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (organizationId == Guid.Empty || workspaceId == Guid.Empty || id == Guid.Empty)
            {
                throw new ArgumentException("Ids must not be empty.");
            }

            if (leaseOwner == Guid.Empty)
            {
                throw new ArgumentException("Lease owner must not be empty.", nameof(leaseOwner));
            }

            // Mirror the real ExecuteUpdate compare-and-swap: claim only a non-terminal, free-or-expired job whose
            // current owner is EXACTLY the one observed (null-safe), so two callers can never both win.
            var job = _jobs.SingleOrDefault(candidate => candidate.Identifies(organizationId, workspaceId, id));
            if (job is null || job.LeaseOwner != observedLeaseOwner || !job.CanAcquireLease(now))
            {
                return Task.FromResult(false);
            }

            job.AcquireLease(leaseOwner, leasedUntil, now);
            return Task.FromResult(true);
        }

        // IExportJobRepository ---------------------------------------------------------------------------------

        public Task<ExportJob?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
        {
            if (organizationId == Guid.Empty || workspaceId == Guid.Empty || id == Guid.Empty)
            {
                throw new ArgumentException("Ids must not be empty.");
            }

            var job = _jobs.SingleOrDefault(candidate => candidate.Identifies(organizationId, workspaceId, id));
            return Task.FromResult(job);
        }

        public Task UpdateAsync(ExportJob exportJob, CancellationToken cancellationToken)
            // The HAPPY-PATH processor commits a job's status transitions atomically through the manifest add's
            // unit of work (the repositories share one EF DbContext), so the success path never calls UpdateAsync
            // separately. The dead-letter / failed-attempt accounting (CORE-RES-002) DOES persist through
            // UpdateAsync, but only after a rolled-back work transaction — a path that needs real persistence to
            // model the rollback faithfully, so it is exercised in the SQLite ExportProcessingJobTests, not over
            // this in-memory fake. Throwing here keeps the fake suite asserting only the happy/idempotent/scoping
            // paths and guards against an accidental non-atomic status persist on the success path.
            => throw new NotSupportedException();

        public Task<ExportJob?> ReloadAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            // ReloadAsync is the worker's reload-after-a-rolled-back-attempt primitive (CORE-RES-002). The
            // failed-attempt / dead-letter path it serves needs real transaction rollback to be modeled
            // faithfully, so it is exercised against SQLite in ExportProcessingJobTests, not over this fake.
            => throw new NotSupportedException();

        public Task<ExportJobAddResult> AddAsync(ExportJob exportJob, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ExportJob>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // The export-processing sweep re-resolves a job by its full tenant + workspace scope (FindByIdAsync);
        // the tenant-only by-id lookup is the read/download endpoint's path, never the worker's, so it is
        // unsupported here as a regression guard.
        public Task<ExportJob?> FindByIdInOrganizationAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // The retention sweep (CORE-PRIV-003) is a different worker loop; the export-processing tests never
        // exercise it, so its repository members are unsupported here as a regression guard.
        public Task<IReadOnlyList<ExportJob>> ListCompletedForRetentionAsync(DateTimeOffset createdBefore, int maxCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(ExportJob exportJob, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // IWorkspaceExportInventoryReader ----------------------------------------------------------------------

        public Task<IReadOnlyDictionary<ExportResourceKind, int>> CountWorkspaceResourcesAsync(
            Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
        {
            if (organizationId == Guid.Empty || workspaceId == Guid.Empty)
            {
                throw new ArgumentException("Ids must not be empty.");
            }

            IReadOnlyDictionary<ExportResourceKind, int> inventory =
                _inventories.TryGetValue((organizationId, workspaceId), out var configured)
                    ? configured
                    : Inventory();
            return Task.FromResult(inventory);
        }

        // IExportManifestRepository ----------------------------------------------------------------------------

        public Task<ExportManifestAddResult> AddAsync(ExportManifest manifest, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            if (FailManifestAddFor.Contains(manifest.ExportJobId))
            {
                // A non-duplicate persistence failure (for example the workspace was deleted between the queued
                // read and the manifest insert — a foreign-key violation). In real persistence the failed
                // SaveChanges rolls the job's transitions back too; the processing service counts it failed and
                // leaves the job for the next sweep.
                throw new DbUpdateException($"simulated persistence failure for export job {manifest.ExportJobId}");
            }

            if (PretendManifestExistsFor.Contains(manifest.ExportJobId)
                || _manifests.Any(existing => existing.ExportJobId == manifest.ExportJobId))
            {
                return Task.FromResult(ExportManifestAddResult.DuplicateExportJob);
            }

            _manifests.Add(manifest);
            return Task.FromResult(ExportManifestAddResult.Added);
        }

        // Explicit implementation: IExportManifestRepository.FindByIdAsync shares its signature with
        // IExportJobRepository.FindByIdAsync (differing only by return type), so the manifest read is
        // disambiguated explicitly. The processing service never calls it.
        Task<ExportManifest?> IExportManifestRepository.FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ExportManifest?> FindByExportJobIdAsync(Guid organizationId, Guid workspaceId, Guid exportJobId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each transition and manifest timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
