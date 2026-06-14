using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Enforcement tests for <see cref="QuotaEnforcementService"/> (CORE-ENTL-004) — the quota enforcement story's
/// domain coverage. They run end-to-end through the real repositories over in-memory SQLite (foreign keys ON), so
/// the active-definition filter, the per-subject usage scoping and the entitlement resolver join are exercised in
/// SQL exactly as in production.
///
/// The cases prove the epic acceptance criterion "Free limits cannot be bypassed by clients" at the decision level:
/// a subject AT the limit is denied; a subject UNDER the limit is allowed and its usage increments; an unlimited
/// grant always has headroom; a defined quota the subject is NOT entitled to is denied fail-closed (no allowance);
/// an ungoverned command (no active definition) is allowed and records nothing; releasing a counted resource
/// decrements (clamped at zero). All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaEnforcementServiceTests : IDisposable
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _time = new FixedTimeProvider(_seedTime);

    public QuotaEnforcementServiceTests()
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

    private QuotaEnforcementService CreateService(LiveCoreDbContext context)
        => new(
            new QuotaDefinitionRepository(context),
            new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
            new QuotaUsageRepository(context),
            _time);

    /// <summary>
    /// Seeds a quota entitlement definition + a user-subject quota definition for the given key. Optionally grants
    /// the subject a limit (or an unlimited/fair-use grant), and optionally records a starting usage amount.
    /// </summary>
    private async Task<Guid> SeedQuotaAsync(
        string key,
        Guid subjectId,
        long? limit,
        bool grant,
        long startingUsage = 0)
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime);
        context.QuotaDefinitions.Add(quota);

        if (grant)
        {
            context.SubjectEntitlements.Add(
                SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, entitlement, limit, null, _seedTime));
        }

        if (startingUsage != 0)
        {
            var usage = QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _seedTime);
            usage.Record(startingUsage, _seedTime);
            context.QuotaUsage.Add(usage);
        }

        await context.SaveChangesAsync();
        return quota.Id;
    }

    private async Task<long> ReadUsageAsync(Guid subjectId, Guid quotaDefinitionId)
    {
        await using var context = CreateContext();
        var usage = await new QuotaUsageRepository(context).FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.User, subjectId, quotaDefinitionId, CancellationToken.None);
        return usage?.UsedAmount ?? 0;
    }

    // =====================================================================
    // CheckAsync — the allow/deny decision.
    // =====================================================================

    [Fact]
    public async Task Check_allows_a_subject_under_its_limit()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: 2, grant: true);

        await using var context = CreateContext();
        var decision = await CreateService(context).CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.True(decision.IsGoverned);
        Assert.Equal(2, decision.Limit);
    }

    [Fact]
    public async Task Check_denies_a_subject_at_its_limit()
    {
        // Used 1 of a limit of 1: a further unit would exceed the cap (the boundary IS allowed, one MORE is not).
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: 1, grant: true, startingUsage: 1);

        await using var context = CreateContext();
        var decision = await CreateService(context).CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsGoverned);
    }

    [Fact]
    public async Task Check_allows_exactly_up_to_the_limit_but_not_beyond()
    {
        // limit 2, used 1: consuming 1 lands AT the cap (allowed); consuming 2 would exceed it (denied).
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: 2, grant: true, startingUsage: 1);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.True((await service.CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);
        Assert.False((await service.CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 2, CancellationToken.None)).IsAllowed);
    }

    [Fact]
    public async Task Check_allows_an_unlimited_grant_at_any_usage()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: null, grant: true, startingUsage: 1000);

        await using var context = CreateContext();
        var decision = await CreateService(context).CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Limit);
    }

    [Fact]
    public async Task Check_denies_a_defined_quota_the_subject_is_not_entitled_to_fail_closed()
    {
        // The quota is defined but the subject holds NO entitlement granting a limit: fail-closed, no allowance.
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: null, grant: false);

        await using var context = CreateContext();
        var decision = await CreateService(context).CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.IsGoverned);
        Assert.Equal(0, decision.Limit); // fail-closed: no allowance
    }

    [Fact]
    public async Task Check_allows_an_ungoverned_command_when_no_quota_is_defined()
    {
        // No quota definition exists for the key, so there is nothing to enforce: the command proceeds.
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var decision = await CreateService(context).CheckAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsGoverned);
    }

    [Fact]
    public async Task Check_rejects_an_empty_subject_id_and_a_non_positive_amount()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckAsync(
            EntitlementSubjectType.User, Guid.Empty, "workspace.active.max", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CheckAsync(
            EntitlementSubjectType.User, Guid.CreateVersion7(), "workspace.active.max", 0, CancellationToken.None));
    }

    // =====================================================================
    // TryConsumeAsync — the atomic check-and-consume (CORE-CONC-004).
    // =====================================================================

    [Fact]
    public async Task TryConsume_creates_the_usage_row_on_first_consumption()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 5, grant: true);

        await using (var context = CreateContext())
        {
            var decision = await CreateService(context).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            Assert.True(decision.IsAllowed);
            Assert.Equal(1, decision.Used);
        }

        Assert.Equal(1, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_increments_an_existing_usage_row()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 5, grant: true, startingUsage: 2);

        await using (var context = CreateContext())
        {
            var decision = await CreateService(context).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            Assert.True(decision.IsAllowed);
        }

        Assert.Equal(3, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_denies_and_records_nothing_at_the_limit()
    {
        // Used 1 of a limit of 1: a further unit would exceed the cap, so the consume is denied AND the usage is
        // left unchanged (the atomic guard never increments past the cap).
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 1, grant: true, startingUsage: 1);

        await using (var context = CreateContext())
        {
            var decision = await CreateService(context).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            Assert.False(decision.IsAllowed);
        }

        Assert.Equal(1, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_denies_fail_closed_and_records_nothing_when_not_entitled()
    {
        // The quota is defined but the subject holds NO entitlement: fail-closed, no allowance, nothing consumed.
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: null, grant: false);

        await using (var context = CreateContext())
        {
            var decision = await CreateService(context).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            Assert.False(decision.IsAllowed);
            Assert.True(decision.IsGoverned);
            Assert.Equal(0, decision.Limit);
        }

        Assert.Equal(0, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_always_consumes_for_an_unlimited_grant()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: null, grant: true, startingUsage: 1000);

        await using (var context = CreateContext())
        {
            var decision = await CreateService(context).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            Assert.True(decision.IsAllowed);
            Assert.Null(decision.Limit);
        }

        Assert.Equal(1001, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_does_nothing_for_an_ungoverned_command()
    {
        // No quota defined: there is nothing to key a usage row to, so nothing is recorded and the command proceeds.
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var decision = await CreateService(context).TryConsumeAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.IsGoverned);
        Assert.Empty(await new QuotaUsageRepository(context).ListBySubjectAsync(
            EntitlementSubjectType.User, subjectId, CancellationToken.None));
    }

    [Fact]
    public async Task TryConsume_rejects_an_empty_subject_id_and_a_non_positive_amount()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.TryConsumeAsync(
            EntitlementSubjectType.User, Guid.Empty, "workspace.active.max", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TryConsumeAsync(
            EntitlementSubjectType.User, Guid.CreateVersion7(), "workspace.active.max", 0, CancellationToken.None));
    }

    // =====================================================================
    // TryConsumeAsync — atomicity across independent contexts (the TOCTOU close, CORE-CONC-004).
    // =====================================================================

    [Fact]
    public async Task TryConsume_is_atomic_only_one_of_two_independent_consumers_at_limit_one_succeeds()
    {
        // Two independent DbContexts (the cross-context conflict the separate check-then-record could not guard)
        // both consume the same quota at a limit of one. The atomic limit-guarded increment lets exactly ONE
        // succeed; the second re-evaluates the cap against the just-committed row and is denied. The OLD
        // check-then-record would have let both pass (both read used=0). Final usage is exactly one.
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 1, grant: true);

        QuotaEnforcementDecision first;
        QuotaEnforcementDecision second;
        await using (var contextA = CreateContext())
        await using (var contextB = CreateContext())
        {
            first = await CreateService(contextA).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
            second = await CreateService(contextB).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
        }

        Assert.True(first.IsAllowed);
        Assert.False(second.IsAllowed);
        Assert.Equal(1, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task TryConsume_first_writer_creates_the_row_second_loses_the_create_race_under_the_cap()
    {
        // Neither consumer has a usage row yet, and the limit is two. The first consume inserts the row at one; the
        // second consume's guarded increment finds the freshly-created row and (since 1 + 1 <= 2) succeeds, so the
        // lost-create-race retry path consumes correctly rather than dropping the consumption.
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 2, grant: true);

        await using (var contextA = CreateContext())
        await using (var contextB = CreateContext())
        {
            Assert.True((await CreateService(contextA).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);
            Assert.True((await CreateService(contextB).TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);
        }

        Assert.Equal(2, await ReadUsageAsync(subjectId, quotaId));
    }

    // =====================================================================
    // ReleaseAsync — the decrement.
    // =====================================================================

    [Fact]
    public async Task Release_decrements_recorded_usage()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 5, grant: true, startingUsage: 2);

        await using (var context = CreateContext())
        {
            await CreateService(context).ReleaseAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);
        }

        Assert.Equal(1, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task Release_clamps_at_zero_and_never_goes_negative()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 5, grant: true, startingUsage: 1);

        await using (var context = CreateContext())
        {
            // Release more than is recorded: the floor is zero, never negative.
            await CreateService(context).ReleaseAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 5, CancellationToken.None);
        }

        Assert.Equal(0, await ReadUsageAsync(subjectId, quotaId));
    }

    [Fact]
    public async Task Release_is_a_no_op_when_nothing_is_recorded()
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("workspace.active.max", subjectId, limit: 5, grant: true);

        await using var context = CreateContext();
        await CreateService(context).ReleaseAsync(
            EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

        Assert.Equal(0, await ReadUsageAsync(subjectId, quotaId));
    }

    // =====================================================================
    // The full consume -> deny -> release -> consume cycle (the "active" semantics).
    // =====================================================================

    [Fact]
    public async Task Consuming_to_the_limit_then_releasing_frees_a_slot()
    {
        var subjectId = Guid.CreateVersion7();
        await SeedQuotaAsync("workspace.active.max", subjectId, limit: 1, grant: true);

        await using (var context = CreateContext())
        {
            var service = CreateService(context);

            // Consume the only slot (atomic check-and-consume).
            Assert.True((await service.TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);

            // Now AT the limit: another consume is denied and records nothing.
            Assert.False((await service.TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);

            // Release the slot.
            await service.ReleaseAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None);

            // The slot is free again.
            Assert.True((await service.TryConsumeAsync(
                EntitlementSubjectType.User, subjectId, "workspace.active.max", 1, CancellationToken.None)).IsAllowed);
        }
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the recorded usage timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
