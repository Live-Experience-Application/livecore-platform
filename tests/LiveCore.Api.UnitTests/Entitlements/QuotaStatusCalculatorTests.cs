using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// QUOTA CALCULATION tests for <see cref="QuotaStatusCalculator"/> (CORE-ENTL-003) — the required tests of the
/// quota definition and quota status story, exercising the calculator that combines a subject's active quota
/// definitions, its granted limits (resolved from its entitlements) and its recorded usage into the subject's
/// quota status. The pure <see cref="QuotaStatusCalculator.Combine"/> path covers the cross-quota boundary matrix
/// without a database; the async path runs end-to-end through the real repositories over in-memory SQLite, so the
/// active-definition filter, the per-subject usage scoping and the resolver join are exercised in SQL exactly as in
/// production. All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaStatusCalculatorTests : IDisposable
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public QuotaStatusCalculatorTests()
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

    private static EntitlementDefinition QuotaEntitlement(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _seedTime);

    private static QuotaDefinition QuotaFor(EntitlementDefinition entitlement, EntitlementSubjectType subjectType)
        => QuotaDefinition.Define(entitlement, subjectType, QuotaUnit.Count, _seedTime);

    // =====================================================================
    // Pure combination — the cross-quota boundary matrix.
    // =====================================================================

    [Fact]
    public void Combine_computes_a_status_for_every_active_quota_with_the_right_boundary()
    {
        var subjectId = Guid.CreateVersion7();

        // Four quotas exercising the matrix: capped-under, capped-at (boundary), unlimited, and one the subject is
        // NOT entitled to.
        var capped = QuotaEntitlement("workspace.active.max");
        var atLimit = QuotaEntitlement("session.active.max");
        var unlimited = QuotaEntitlement("session.participant.max");
        var notEntitled = QuotaEntitlement("asset.storage.bytes.max");

        var definitions = new[]
        {
            QuotaFor(capped, EntitlementSubjectType.User),
            QuotaFor(atLimit, EntitlementSubjectType.User),
            QuotaFor(unlimited, EntitlementSubjectType.User),
            QuotaFor(notEntitled, EntitlementSubjectType.User),
        };

        // Granted limits: capped at 5, at-limit capped at 1, unlimited (null). The not-entitled quota has NO
        // assignment, so the subject is fail-closed for it.
        var entitlements = EffectiveEntitlements.FromActiveAssignments(
        [
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, capped, 5, null, _seedTime),
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, atLimit, 1, null, _seedTime),
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, unlimited, null, null, _seedTime),
        ]);

        // Recorded usage: capped used 2 (no row for the rest -> read as zero, except at-limit which is at 1).
        var usage = new[]
        {
            Usage(EntitlementSubjectType.User, subjectId, definitions[0], 2),
            Usage(EntitlementSubjectType.User, subjectId, definitions[1], 1),
        };

        var status = QuotaStatusCalculator.Combine(
            EntitlementSubjectType.User, subjectId, definitions, entitlements, usage);

        // Ordered by key: asset.storage.bytes.max, session.active.max, session.participant.max, workspace.active.max
        Assert.Equal(
            new[] { "asset.storage.bytes.max", "session.active.max", "session.participant.max", "workspace.active.max" },
            status.Quotas.Select(q => q.Key).ToArray());

        var byKey = status.Quotas.ToDictionary(q => q.Key);

        // Not entitled -> fail-closed (no allowance).
        Assert.False(byKey["asset.storage.bytes.max"].IsEntitled);
        Assert.Equal(0, byKey["asset.storage.bytes.max"].Limit);

        // Capped at 1, used 1 -> at the boundary, not exceeded.
        Assert.True(byKey["session.active.max"].IsEntitled);
        Assert.Equal(0, byKey["session.active.max"].Remaining);
        Assert.False(byKey["session.active.max"].IsExceeded);

        // Unlimited.
        Assert.True(byKey["session.participant.max"].IsUnlimited);
        Assert.Null(byKey["session.participant.max"].Remaining);

        // Capped at 5, used 2 -> remaining 3.
        Assert.Equal(2, byKey["workspace.active.max"].Used);
        Assert.Equal(3, byKey["workspace.active.max"].Remaining);
    }

    [Fact]
    public void Combine_reads_a_missing_usage_row_as_zero()
    {
        var subjectId = Guid.CreateVersion7();
        var entitlement = QuotaEntitlement("workspace.active.max");
        var definition = QuotaFor(entitlement, EntitlementSubjectType.User);
        var entitlements = EffectiveEntitlements.FromActiveAssignments(
        [
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, entitlement, 3, null, _seedTime),
        ]);

        var status = QuotaStatusCalculator.Combine(
            EntitlementSubjectType.User, subjectId, [definition], entitlements, []);

        var quota = Assert.Single(status.Quotas);
        Assert.Equal(0, quota.Used);
        Assert.Equal(3, quota.Remaining);
    }

    [Fact]
    public void Combine_with_no_quota_definitions_yields_an_empty_status()
    {
        var status = QuotaStatusCalculator.Combine(
            EntitlementSubjectType.User, Guid.CreateVersion7(), [], EffectiveEntitlements.Empty, []);

        Assert.Empty(status.Quotas);
    }

    [Fact]
    public void Combine_rejects_duplicate_usage_for_one_quota()
    {
        var subjectId = Guid.CreateVersion7();
        var entitlement = QuotaEntitlement("workspace.active.max");
        var definition = QuotaFor(entitlement, EntitlementSubjectType.User);

        var duplicate = new[]
        {
            Usage(EntitlementSubjectType.User, subjectId, definition, 1),
            Usage(EntitlementSubjectType.User, subjectId, definition, 2),
        };

        Assert.Throws<ArgumentException>(() => QuotaStatusCalculator.Combine(
            EntitlementSubjectType.User, subjectId, [definition], EffectiveEntitlements.Empty, duplicate));
    }

    // =====================================================================
    // Async end-to-end — through the real repositories over SQLite.
    // =====================================================================

    [Fact]
    public async Task CalculateForSubject_computes_status_from_persisted_catalog_entitlements_and_usage()
    {
        var subjectId = Guid.CreateVersion7();
        Guid quotaDefinitionId;

        await using (var context = CreateContext())
        {
            var entitlement = QuotaEntitlement("workspace.active.max");
            context.EntitlementDefinitions.Add(entitlement);
            var quota = QuotaFor(entitlement, EntitlementSubjectType.User);
            context.QuotaDefinitions.Add(quota);
            quotaDefinitionId = quota.Id;
            context.SubjectEntitlements.Add(
                SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, entitlement, 1, null, _seedTime));
            var usage = QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _seedTime);
            usage.Record(1, _seedTime);
            context.QuotaUsage.Add(usage);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var status = await CreateCalculator(context).CalculateForSubjectAsync(
                EntitlementSubjectType.User, subjectId, CancellationToken.None);

            var quota = Assert.Single(status.Quotas);
            Assert.Equal("workspace.active.max", quota.Key);
            Assert.True(quota.IsEntitled);
            Assert.Equal(1, quota.Used);
            Assert.Equal(1, quota.Limit);
            Assert.Equal(0, quota.Remaining);
            Assert.False(quota.IsExceeded); // used == limit is the boundary
        }

        Assert.NotEqual(Guid.Empty, quotaDefinitionId);
    }

    [Fact]
    public async Task CalculateForSubject_excludes_a_retired_quota_definition()
    {
        var subjectId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            var entitlement = QuotaEntitlement("workspace.active.max");
            context.EntitlementDefinitions.Add(entitlement);
            var quota = QuotaFor(entitlement, EntitlementSubjectType.User);
            quota.Deactivate(_seedTime.AddHours(1));
            context.QuotaDefinitions.Add(quota);
            context.SubjectEntitlements.Add(
                SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, entitlement, 1, null, _seedTime));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            // The quota definition is retired, so it is not reported even though the subject is entitled.
            var status = await CreateCalculator(context).CalculateForSubjectAsync(
                EntitlementSubjectType.User, subjectId, CancellationToken.None);

            Assert.Empty(status.Quotas);
        }
    }

    [Fact]
    public async Task CalculateForSubject_does_not_cross_subject_types()
    {
        // A user-subject quota is reported for the user but not for a workspace subject sharing the guid.
        var sharedId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            var entitlement = QuotaEntitlement("workspace.active.max");
            context.EntitlementDefinitions.Add(entitlement);
            context.QuotaDefinitions.Add(QuotaFor(entitlement, EntitlementSubjectType.User));
            context.SubjectEntitlements.Add(
                SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, sharedId, entitlement, 1, null, _seedTime));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var calculator = CreateCalculator(context);

            var asUser = await calculator.CalculateForSubjectAsync(
                EntitlementSubjectType.User, sharedId, CancellationToken.None);
            var asWorkspace = await calculator.CalculateForSubjectAsync(
                EntitlementSubjectType.Workspace, sharedId, CancellationToken.None);

            Assert.Single(asUser.Quotas);
            // The workspace subject kind has no quota definitions: nothing to report.
            Assert.Empty(asWorkspace.Quotas);
        }
    }

    [Fact]
    public async Task CalculateForSubject_reports_a_defined_quota_the_subject_is_not_entitled_to_fail_closed()
    {
        var subjectId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            var entitlement = QuotaEntitlement("workspace.active.max");
            context.EntitlementDefinitions.Add(entitlement);
            // The quota is defined, but the subject holds NO entitlement granting a limit.
            context.QuotaDefinitions.Add(QuotaFor(entitlement, EntitlementSubjectType.User));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            var status = await CreateCalculator(context).CalculateForSubjectAsync(
                EntitlementSubjectType.User, subjectId, CancellationToken.None);

            var quota = Assert.Single(status.Quotas);
            Assert.False(quota.IsEntitled);
            Assert.Equal(0, quota.Limit); // fail-closed: no allowance
        }
    }

    [Fact]
    public async Task CalculateForSubject_rejects_an_empty_subject_id()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCalculator(context).CalculateForSubjectAsync(
            EntitlementSubjectType.User, Guid.Empty, CancellationToken.None));
    }

    private QuotaStatusCalculator CreateCalculator(LiveCoreDbContext context)
        => new(
            new QuotaDefinitionRepository(context),
            new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
            new QuotaUsageRepository(context));

    private static QuotaUsage Usage(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        long used)
    {
        var usage = QuotaUsage.Start(subjectType, subjectId, definition, _seedTime);
        if (used != 0)
        {
            usage.Record(used, _seedTime);
        }

        return usage;
    }
}
