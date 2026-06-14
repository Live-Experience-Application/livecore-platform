using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="QuotaUsageRepository"/> (CORE-ENTL-003). They run
/// against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so the real
/// model mapping, the unique per-subject index, the subject-type string conversion and the restricted
/// quota-definition foreign key are exercised on every run. A usage row is a PER-SUBJECT record keyed by the
/// (subject_type, subject_id) pair, so the cases that matter are NEGATIVE LOOKUP and ISOLATION: one subject's usage
/// is never returned through another subject's id, a user subject and a workspace subject sharing a guid never
/// collide, and a duplicate usage row is rejected. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class QuotaUsageRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public QuotaUsageRepositoryTests()
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

    private async Task<QuotaDefinition> SeedQuotaAsync(string key = "workspace.active.max")
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime);
        context.QuotaDefinitions.Add(quota);
        await context.SaveChangesAsync();
        return quota;
    }

    [Fact]
    public async Task Usage_round_trips_and_updates_in_place()
    {
        var quota = await SeedQuotaAsync();
        var subjectId = Guid.CreateVersion7();
        var usage = QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _seedTime);
        usage.Record(3, _seedTime);

        await using (var context = CreateContext())
        {
            var repository = new QuotaUsageRepository(context);
            Assert.Equal(QuotaUsageAddResult.Added, await repository.AddAsync(usage, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var repository = new QuotaUsageRepository(context);
            var loaded = await repository.FindBySubjectAndQuotaAsync(
                EntitlementSubjectType.User, subjectId, quota.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded.UsedAmount);

            loaded.Record(7, _seedTime.AddHours(1));
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new QuotaUsageRepository(context);
            var reloaded = await repository.FindBySubjectAndQuotaAsync(
                EntitlementSubjectType.User, subjectId, quota.Id, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(7, reloaded.UsedAmount); // updated in place; one row per (subject, quota)
            Assert.Single(await repository.ListBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_second_usage_row_for_the_same_subject_and_quota_is_rejected()
    {
        var quota = await SeedQuotaAsync();
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);
        Assert.Equal(
            QuotaUsageAddResult.Added,
            await repository.AddAsync(
                QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _seedTime), CancellationToken.None));

        var result = await repository.AddAsync(
            QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _seedTime), CancellationToken.None);

        Assert.Equal(QuotaUsageAddResult.DuplicateUsage, result);
    }

    [Fact]
    public async Task One_subjects_usage_is_never_returned_through_another_subjects_id()
    {
        var quota = await SeedQuotaAsync();
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);
        await repository.AddAsync(
            QuotaUsage.Start(EntitlementSubjectType.User, subjectA, quota, _seedTime), CancellationToken.None);

        Assert.Null(await repository.FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.User, subjectB, quota.Id, CancellationToken.None));
        Assert.Empty(await repository.ListBySubjectAsync(EntitlementSubjectType.User, subjectB, CancellationToken.None));
    }

    [Fact]
    public async Task A_user_subject_and_a_workspace_subject_with_the_same_guid_do_not_collide()
    {
        var quota = await SeedQuotaAsync();
        var sharedId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        // The unique key includes subject_type, so the SAME guid as a user and as a workspace are two distinct
        // subjects: both usage rows are accepted, each resolves only to its own.
        var userUsage = QuotaUsage.Start(EntitlementSubjectType.User, sharedId, quota, _seedTime);
        userUsage.Record(2, _seedTime);
        var workspaceUsage = QuotaUsage.Start(EntitlementSubjectType.Workspace, sharedId, quota, _seedTime);
        workspaceUsage.Record(5, _seedTime);
        Assert.Equal(QuotaUsageAddResult.Added, await repository.AddAsync(userUsage, CancellationToken.None));
        Assert.Equal(QuotaUsageAddResult.Added, await repository.AddAsync(workspaceUsage, CancellationToken.None));

        var asUser = await repository.FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.User, sharedId, quota.Id, CancellationToken.None);
        var asWorkspace = await repository.FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.Workspace, sharedId, quota.Id, CancellationToken.None);

        Assert.Equal(2, asUser!.UsedAmount);
        Assert.Equal(5, asWorkspace!.UsedAmount);
    }

    [Fact]
    public async Task Recording_usage_against_a_quota_that_does_not_exist_is_rejected()
    {
        // The quota definition is built but NEVER persisted, so the restricted foreign key rejects the insert.
        var entitlement = EntitlementDefinition.Define("workspace.active.max", EntitlementValueKind.Quota, "Quota", null, _seedTime);
        var orphanQuota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime);

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(
            QuotaUsage.Start(EntitlementSubjectType.User, Guid.CreateVersion7(), orphanQuota, _seedTime),
            CancellationToken.None));
    }

    [Fact]
    public async Task ListBySubject_rejects_an_empty_subject_id()
    {
        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySubjectAsync(
            EntitlementSubjectType.User, Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task TryConsume_creates_then_increments_a_capped_row_only_within_the_limit()
    {
        // The limit-guarded increment (CORE-CONC-004): first consume inserts the row at one, the second lands at the
        // cap of two, and the third would exceed it and is rejected with the recorded usage left at two.
        var quota = await SeedQuotaAsync();
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        Assert.Equal(QuotaUsageConsumeResult.Consumed, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 1, limit: 2, _seedTime, CancellationToken.None));
        Assert.Equal(QuotaUsageConsumeResult.Consumed, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 1, limit: 2, _seedTime, CancellationToken.None));
        Assert.Equal(QuotaUsageConsumeResult.LimitExceeded, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 1, limit: 2, _seedTime, CancellationToken.None));

        Assert.Equal(2, await repository.GetUsedAmountAsync(
            EntitlementSubjectType.User, subjectId, quota.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TryConsume_rejects_a_first_consumption_that_alone_exceeds_the_limit()
    {
        // No row exists and the requested amount alone overruns the cap, so nothing is inserted.
        var quota = await SeedQuotaAsync();
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        Assert.Equal(QuotaUsageConsumeResult.LimitExceeded, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 5, limit: 2, _seedTime, CancellationToken.None));
        Assert.Equal(0, await repository.GetUsedAmountAsync(
            EntitlementSubjectType.User, subjectId, quota.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TryConsume_always_consumes_under_an_unlimited_grant()
    {
        var quota = await SeedQuotaAsync();
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        Assert.Equal(QuotaUsageConsumeResult.Consumed, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 10, limit: null, _seedTime, CancellationToken.None));
        Assert.Equal(QuotaUsageConsumeResult.Consumed, await repository.TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, quota, 10, limit: null, _seedTime, CancellationToken.None));

        Assert.Equal(20, await repository.GetUsedAmountAsync(
            EntitlementSubjectType.User, subjectId, quota.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetUsedAmount_returns_zero_when_no_usage_is_recorded()
    {
        var quota = await SeedQuotaAsync();

        await using var context = CreateContext();
        var repository = new QuotaUsageRepository(context);

        Assert.Equal(0, await repository.GetUsedAmountAsync(
            EntitlementSubjectType.User, Guid.CreateVersion7(), quota.Id, CancellationToken.None));
    }
}
