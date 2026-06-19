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
/// Integration-style tests for the EF Core-backed <see cref="QueuedExportJobReader"/> (CORE-JOB-002, extended by
/// CORE-EXP-002) — the system read that finds the export jobs the processing job should pick up (jobs of any
/// scope that are not yet terminal).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// the same harness as <see cref="ExportJobRepositoryTests"/>, so the real model mapping and the scope/status
/// name conversions are exercised on every run without a database server. The behaviors under test are the
/// job's eligibility rules at the data layer:
/// <list type="bullet">
///   <item>ELIGIBILITY: NON-TERMINAL (Pending or Running) jobs of ANY scope are returned — a Completed/Failed
///   job is never re-processed, but a user-data export IS now surfaced so it has a producer (CORE-EXP-002); the
///   processor branches on the loaded job's scope.</item>
///   <item>RECOVERY: a Running job (left behind by a crashed worker) is still returned, so the next sweep
///   finishes it.</item>
///   <item>TENANT-SCOPED (threat T5): the sweep spans tenants (a system job) but each returned entry carries
///   its own job's organization, workspace and id.</item>
///   <item>BOUNDED + ORDERED: the result honors the batch size and is ordered by the time-ordered surrogate
///   job id (oldest first), so repeated sweeps cover everything.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class QueuedExportJobReaderTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _finishedAt = new(2026, 6, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public QueuedExportJobReaderTests()
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

    [Fact]
    public async Task Returns_non_terminal_jobs_of_any_scope()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync(_subject);

        var pending = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);
        var running = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Running);
        // Included: a user-data export now has a producer (CORE-EXP-002), so a non-terminal user-data job is
        // surfaced exactly like a workspace one; the processor branches on the loaded job's scope.
        var userData = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.UserData, ExportJobStatus.Pending);
        // Excluded: terminal jobs are never re-processed, regardless of scope.
        await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Completed);
        await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Failed);
        await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.UserData, ExportJobStatus.Completed);

        await using var context = CreateContext();
        var reader = new QueuedExportJobReader(context);
        var queued = await reader.ListQueuedExportsAsync(50, CancellationToken.None);

        // Exactly the non-terminal jobs (both workspace AND user-data), each carrying its own coordinates.
        Assert.Equal(
            new[] { pending.Id, running.Id, userData.Id }.OrderBy(id => id),
            queued.Select(item => item.ExportJobId).OrderBy(id => id));
        Assert.All(queued, item =>
        {
            Assert.Equal(organization.Id, item.OrganizationId);
            Assert.Equal(workspace.Id, item.WorkspaceId);
        });
    }

    [Fact]
    public async Task Spans_tenants_returning_each_jobs_own_coordinates()
    {
        // The sweep is a system job, so it returns queued jobs across tenants — but each entry carries its own
        // tenant/workspace/job, so the manifest produced from it is scoped to that tenant alone (threat T5).
        var organizationA = await SeedOrganizationAsync("northwind-labs");
        var organizationB = await SeedOrganizationAsync("acme-co");
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, "summer-show");
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, "winter-show");
        var user = await SeedUserAsync(_subject);
        var jobA = await SeedJobAsync(organizationA.Id, workspaceA.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);
        var jobB = await SeedJobAsync(organizationB.Id, workspaceB.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);

        await using var context = CreateContext();
        var queued = await new QueuedExportJobReader(context).ListQueuedExportsAsync(50, CancellationToken.None);

        var fromA = Assert.Single(queued, item => item.ExportJobId == jobA.Id);
        Assert.Equal(organizationA.Id, fromA.OrganizationId);
        Assert.Equal(workspaceA.Id, fromA.WorkspaceId);

        var fromB = Assert.Single(queued, item => item.ExportJobId == jobB.Id);
        Assert.Equal(organizationB.Id, fromB.OrganizationId);
        Assert.Equal(workspaceB.Id, fromB.WorkspaceId);
    }

    [Fact]
    public async Task Honors_the_batch_size_and_orders_oldest_first()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync(_subject);
        var first = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);
        var second = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);
        var third = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);

        // The reader orders by the surrogate job id. UUIDv7 is only coarsely time-ordered (not strictly
        // monotonic within a millisecond), so the expected order is the ids sorted, not the insertion order
        // (the same approach ExportJobRepositoryTests uses).
        var expectedOrder = new[] { first.Id, second.Id, third.Id }.OrderBy(id => id).ToArray();

        await using var context = CreateContext();
        var reader = new QueuedExportJobReader(context);

        // The batch is bounded and surfaces the smallest ids first.
        var firstBatch = await reader.ListQueuedExportsAsync(2, CancellationToken.None);
        Assert.Equal(
            expectedOrder.Take(2).ToArray(),
            firstBatch.Select(item => item.ExportJobId).ToArray());

        var fullBatch = await reader.ListQueuedExportsAsync(50, CancellationToken.None);
        Assert.Equal(
            expectedOrder,
            fullBatch.Select(item => item.ExportJobId).ToArray());
    }

    [Fact]
    public async Task Rejects_a_non_positive_batch_size()
    {
        await using var context = CreateContext();
        var reader = new QueuedExportJobReader(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListQueuedExportsAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListQueuedExportsAsync(-1, CancellationToken.None));
    }

    [Fact]
    public async Task Surfaces_each_jobs_lease_state_so_the_processor_can_skip_or_reclaim_it()
    {
        // CORE-RES-003: the read does not filter on the lease (a DateTimeOffset cannot be compared in SQL on
        // SQLite, and an expired lease must still be surfaced to be reclaimed). A leased job is returned WITH its
        // lease owner and expiry so the processor decides claimability — and reclaim — in memory; an unleased job
        // is returned with both null.
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var user = await SeedUserAsync(_subject);
        var unleased = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);
        var leasedJob = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.Workspace, ExportJobStatus.Pending);

        var leaseOwner = Guid.NewGuid();
        var leasedUntil = _startedAt + TimeSpan.FromMinutes(5);
        await using (var claimContext = CreateContext())
        {
            Assert.True(await new ExportJobRepository(claimContext).ClaimAsync(
                organization.Id, workspace.Id, leasedJob.Id,
                observedLeaseOwner: null, leaseOwner, leasedUntil, _startedAt, CancellationToken.None));
        }

        await using var context = CreateContext();
        var queued = await new QueuedExportJobReader(context).ListQueuedExportsAsync(50, CancellationToken.None);

        var unleasedEntry = Assert.Single(queued, item => item.ExportJobId == unleased.Id);
        Assert.Null(unleasedEntry.LeaseOwner);
        Assert.Null(unleasedEntry.LeasedUntil);
        Assert.True(unleasedEntry.IsClaimable(_startedAt));

        var leasedEntry = Assert.Single(queued, item => item.ExportJobId == leasedJob.Id);
        Assert.Equal(leaseOwner, leasedEntry.LeaseOwner);
        Assert.Equal(leasedUntil, leasedEntry.LeasedUntil);
        // Not claimable while the lease is live, but reclaimable once it has expired.
        Assert.False(leasedEntry.IsClaimable(_startedAt));
        Assert.True(leasedEntry.IsClaimable(leasedUntil + TimeSpan.FromMinutes(1)));
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

    /// <summary>Creates an export job, drives its in-memory aggregate to the requested status, and persists it.</summary>
    private async Task<ExportJob> SeedJobAsync(
        Guid organizationId, Guid workspaceId, Guid requestedBy, ExportScope scope, ExportJobStatus status)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedBy, scope, _createdAt);
        switch (status)
        {
            case ExportJobStatus.Pending:
                break;
            case ExportJobStatus.Running:
                job.Start(_startedAt);
                break;
            case ExportJobStatus.Completed:
                job.Start(_startedAt);
                job.Complete(_finishedAt);
                break;
            case ExportJobStatus.Failed:
                job.Fail("generic failure reason", _finishedAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status in the fixture.");
        }

        await using var context = CreateContext();
        Assert.Equal(
            ExportJobAddResult.Added,
            await new ExportJobRepository(context).AddAsync(job, CancellationToken.None));
        return job;
    }
}
