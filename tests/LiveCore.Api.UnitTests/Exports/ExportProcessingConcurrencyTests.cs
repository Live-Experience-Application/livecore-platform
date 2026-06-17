// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// The multi-replica correctness proof for the background export processing job's CLAIM/LEASE (CORE-RES-003) —
/// the story's required "two concurrent workers process disjoint job sets (no double-processing)" and "a lease
/// expires and is reclaimed after a simulated crash".
///
/// It drives the REAL <see cref="ExportProcessingService"/> over the REAL collaborators (the
/// <see cref="QueuedExportJobReader"/>, the <see cref="ExportJobRepository"/> — whose atomic
/// <see cref="ExportJobRepository.ClaimAsync"/> compare-and-swap is the mechanism under test — the
/// <see cref="WorkspaceExportInventoryReader"/> and the <see cref="ExportManifestRepository"/>) against a real
/// database. Like the recap concurrency proof (CORE-RCP-001) the parallel test runs each sweep on its OWN
/// <see cref="LiveCoreDbContext"/> over its own connection to ONE shared temp-file SQLite database, so multiple
/// replicas genuinely contend through SQLite's busy-retry locking (the realistic stand-in for the PostgreSQL
/// behaviour exercised by the Npgsql integration job). Each service instance mints its OWN lease owner, exactly
/// as two deployed worker replicas would.
///
/// Correctness rests on the CLAIM, not the downstream unique <c>export_manifests(export_job_id)</c> index: the
/// disjoint-sets test asserts that the number of jobs each replica reports PROCESSED sums to exactly the job
/// count — if the claim failed to prevent double-processing, a losing replica's manifest insert would still
/// surface as a (duplicate) processed job and push the sum above the count. All fixtures are generic Core
/// language (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ExportProcessingConcurrencyTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _processedAt = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ExportProcessingConcurrencyTests()
    {
        // A temp-file database (not shared-cache in-memory) so independent connections contend through SQLite's
        // normal file locking, which retries on a busy writer rather than failing — the behaviour a true parallel
        // sweep needs (the same harness as RecapGenerationConcurrencyTests).
        _databasePath = Path.Combine(Path.GetTempPath(), $"livecore-export-concurrency-{Guid.NewGuid():N}.db");
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Concurrent_workers_process_disjoint_job_sets_without_double_processing(int replicas)
    {
        var (organizationId, workspaceId) = await SeedWorkspaceAsync();
        var requester = await SeedUserAsync("subject-a");
        const int jobCount = 12;
        for (var index = 0; index < jobCount; index++)
        {
            await SeedPendingJobAsync(organizationId, workspaceId, requester);
        }

        // Fire N replicas at once. Each reads every pending job as a candidate, but the atomic claim leases each
        // row to exactly one replica, so the jobs are PARTITIONED across the replicas — none is processed twice.
        var tasks = Enumerable.Range(0, replicas)
            .Select(_ => Task.Run(RunSweepAsync))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // No double-processing: the processed counts sum to exactly the job count (a double-claim would push it
        // higher via a losing replica's duplicate manifest insert), and exactly one manifest exists per job.
        Assert.Equal(jobCount, results.Sum(result => result.Processed));
        Assert.Equal(0, results.Sum(result => result.Failed));

        await using var assertContext = CreateContext();
        Assert.Equal(jobCount, await assertContext.ExportManifests.CountAsync(CancellationToken.None));

        var jobs = await assertContext.ExportJobs.ToListAsync(CancellationToken.None);
        // Every job completed exactly once and carries the lease of whichever replica claimed it.
        Assert.All(jobs, job =>
        {
            Assert.Equal(ExportJobStatus.Completed, job.Status);
            Assert.NotNull(job.LeaseOwner);
        });
        // The jobs are partitioned among AT MOST the N replicas that ran — disjoint claim sets, never overlapping.
        var distinctOwners = jobs.Select(job => job.LeaseOwner).Distinct().Count();
        Assert.InRange(distinctOwners, 1, replicas);
    }

    [Fact]
    public async Task A_crashed_workers_lease_is_not_stolen_while_live_but_is_reclaimed_after_it_expires()
    {
        var (organizationId, workspaceId) = await SeedWorkspaceAsync();
        var requester = await SeedUserAsync("subject-a");
        var job = await SeedPendingJobAsync(organizationId, workspaceId, requester);

        // Simulate a replica that CLAIMED the job and then crashed mid-job (its lease persists, the work never
        // committed): the row is Pending, leased to a "crashed" owner until shortly after the crash.
        var crashedOwner = Guid.NewGuid();
        var crashTime = _processedAt;
        var crashedLeaseUntil = crashTime + TimeSpan.FromMinutes(1);
        await using (var crashContext = CreateContext())
        {
            Assert.True(await new ExportJobRepository(crashContext).ClaimAsync(
                organizationId, workspaceId, job.Id,
                observedLeaseOwner: null, leaseOwner: crashedOwner,
                crashedLeaseUntil, crashTime, CancellationToken.None));
        }

        // A sweep WHILE the lease is still live must not steal it — the crashed replica might still be working.
        var whileLive = crashTime + TimeSpan.FromSeconds(30);
        await using (var liveContext = CreateContext())
        {
            var result = await CreateService(liveContext, whileLive)
                .ProcessQueuedExportsAsync(CancellationToken.None);
            Assert.Equal(1, result.Examined);
            Assert.Equal(0, result.Processed);
        }

        await using (var midContext = CreateContext())
        {
            var stillPending = await new ExportJobRepository(midContext)
                .FindByIdAsync(organizationId, workspaceId, job.Id, CancellationToken.None);
            Assert.Equal(ExportJobStatus.Pending, stillPending!.Status);
            Assert.Equal(crashedOwner, stillPending.LeaseOwner);
            Assert.Equal(0, await midContext.ExportManifests.CountAsync(CancellationToken.None));
        }

        // A sweep AFTER the lease expires reclaims the job and finishes it — the crash never stranded the work.
        var afterExpiry = crashedLeaseUntil + TimeSpan.FromMinutes(1);
        await using (var reclaimContext = CreateContext())
        {
            var result = await CreateService(reclaimContext, afterExpiry)
                .ProcessQueuedExportsAsync(CancellationToken.None);
            Assert.Equal(1, result.Examined);
            Assert.Equal(1, result.Processed);
        }

        await using (var assertContext = CreateContext())
        {
            var reclaimed = await new ExportJobRepository(assertContext)
                .FindByIdAsync(organizationId, workspaceId, job.Id, CancellationToken.None);
            Assert.Equal(ExportJobStatus.Completed, reclaimed!.Status);
            // The reclaiming replica took the lease over from the crashed owner.
            Assert.NotNull(reclaimed.LeaseOwner);
            Assert.NotEqual(crashedOwner, reclaimed.LeaseOwner);
            var manifest = await new ExportManifestRepository(assertContext)
                .FindByExportJobIdAsync(organizationId, workspaceId, job.Id, CancellationToken.None);
            Assert.NotNull(manifest);
        }
    }

    private async Task<ExportProcessingResult> RunSweepAsync()
    {
        await using var context = CreateContext();
        return await CreateService(context, _processedAt).ProcessQueuedExportsAsync(CancellationToken.None);
    }

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private static ExportProcessingService CreateService(LiveCoreDbContext context, DateTimeOffset now)
        => new(
            new QueuedExportJobReader(context),
            new ExportJobRepository(context),
            new WorkspaceExportInventoryReader(context),
            new ExportManifestRepository(context),
            new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize: 50),
            new FixedTimeProvider(now),
            NullLogger<ExportProcessingService>.Instance);

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync()
    {
        var organization = Organization.Create("northwind-labs", "northwind-labs", _createdAt);
        var workspace = Workspace.Create(organization.Id, "summer-show", "summer-show", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return (organization.Id, workspace.Id);
    }

    private async Task<Guid> SeedUserAsync(string subject)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subject), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(UserProfileAddResult.Added, await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile.Id;
    }

    private async Task<ExportJob> SeedPendingJobAsync(Guid organizationId, Guid workspaceId, Guid requestedBy)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedBy, ExportScope.Workspace, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(ExportJobAddResult.Added, await new ExportJobRepository(context).AddAsync(job, CancellationToken.None));
        return job;
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each sweep's "now" (and the lease expiry it judges) is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
