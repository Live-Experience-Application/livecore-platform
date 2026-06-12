using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for <see cref="SubjectEntitlementAssignmentService"/> (CORE-ENTL-002) — the write
/// side of the subject entitlement assignment and lookup story. The service grants a subject the entitlements of
/// a plan (REUSING the CORE-ENTL-001 plan/entitlement catalog, so the granted values are decided server-side,
/// never by a client; docs/21 "Never trust client-side premium flags") and revokes them again.
///
/// The tests run the service over the real repositories on an in-memory SQLite database, and verify the result
/// by RESOLVING the subject's effective entitlements — so assignment and lookup are exercised end to end. They
/// pin: a plan assigns its grants (positive), a re-assignment converges in place rather than duplicating
/// (idempotent), an unknown or retired plan assigns nothing (fail-closed), a retired entitlement is never newly
/// assigned (fail-closed), and a revocation removes the premium state (negative). All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class SubjectEntitlementAssignmentServiceTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SubjectEntitlementAssignmentServiceTests()
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

    // Each call models its own request scope: a fresh context with the three module repositories over it.
    private async Task<SubjectEntitlementAssignmentResult> AssignFromPlanAsync(
        EntitlementSubjectType subjectType, Guid subjectId, Guid planId, DateTimeOffset at)
    {
        await using var context = CreateContext();
        var service = new SubjectEntitlementAssignmentService(
            new SubjectEntitlementRepository(context),
            new PlanDefinitionRepository(context),
            new EntitlementDefinitionRepository(context));
        return await service.AssignFromPlanAsync(subjectType, subjectId, planId, at, CancellationToken.None);
    }

    private async Task<bool> RevokeAsync(EntitlementSubjectType subjectType, Guid subjectId, Guid entitlementDefinitionId)
    {
        await using var context = CreateContext();
        var service = new SubjectEntitlementAssignmentService(
            new SubjectEntitlementRepository(context),
            new PlanDefinitionRepository(context),
            new EntitlementDefinitionRepository(context));
        return await service.RevokeAsync(subjectType, subjectId, entitlementDefinitionId, _at.AddHours(1), CancellationToken.None);
    }

    private async Task<EffectiveEntitlements> ResolveAsync(EntitlementSubjectType subjectType, Guid subjectId)
    {
        await using var context = CreateContext();
        var resolver = new SubjectEntitlementResolver(new SubjectEntitlementRepository(context));
        return await resolver.ResolveAsync(subjectType, subjectId, CancellationToken.None);
    }

    private async Task<EntitlementDefinition> SeedDefinitionAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    private async Task RetireDefinitionAsync(Guid definitionId)
    {
        await using var context = CreateContext();
        var definition = await context.EntitlementDefinitions.FirstAsync(d => d.Id == definitionId);
        definition.Deactivate(_at.AddMinutes(30));
        await context.SaveChangesAsync();
    }

    private async Task<PlanDefinition> SeedPlanAsync(PlanDefinition plan)
    {
        await using var context = CreateContext();
        var repository = new PlanDefinitionRepository(context);
        Assert.Equal(PlanDefinitionAddResult.Added, await repository.AddAsync(plan, CancellationToken.None));
        return plan;
    }

    private static EntitlementDefinition FlagDefinition(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Flag", null, _at);

    private static EntitlementDefinition QuotaDefinition(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _at);

    /// <summary>Seeds two definitions and a plan that grants the flag (true) and the quota (5).</summary>
    private async Task<(PlanDefinition Plan, EntitlementDefinition Flag, EntitlementDefinition Quota)> SeedStandardPlanAsync()
    {
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        var workspaceLimit = await SeedDefinitionAsync(QuotaDefinition("workspace.active.max"));
        var plan = PlanDefinition.Define("standard", "Standard", null, _at);
        plan.GrantFlag(adFree, true);
        plan.GrantQuota(workspaceLimit, 5);
        await SeedPlanAsync(plan);
        return (plan, adFree, workspaceLimit);
    }

    // --- Positive: assign a plan ------------------------------------------------

    [Fact]
    public async Task AssignFromPlan_grants_every_plan_entitlement_to_the_subject()
    {
        var (plan, adFree, _) = await SeedStandardPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at);

        Assert.Equal(SubjectEntitlementAssignmentOutcome.Assigned, result.Outcome);
        Assert.Equal(2, result.AssignedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);

        var effective = await ResolveAsync(EntitlementSubjectType.User, subjectId);
        Assert.True(effective.IsFlagEnabled("ads.disabled"));
        Assert.True(effective.TryGetQuotaLimit("workspace.active.max", out var limit));
        Assert.Equal(5, limit);

        // The assignment records the plan it was granted from (provenance).
        await using var context = CreateContext();
        var stored = await new SubjectEntitlementRepository(context)
            .FindBySubjectAndEntitlementAsync(EntitlementSubjectType.User, subjectId, adFree.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(plan.Id, stored.SourcePlanDefinitionId);
    }

    // --- Idempotency ------------------------------------------------------------

    [Fact]
    public async Task Re_assigning_the_same_plan_converges_in_place_rather_than_duplicating()
    {
        var (plan, _, _) = await SeedStandardPlanAsync();
        var subjectId = Guid.CreateVersion7();
        await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at);

        var second = await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at.AddHours(1));

        Assert.Equal(SubjectEntitlementAssignmentOutcome.Assigned, second.Outcome);
        Assert.Equal(0, second.AssignedCount);
        Assert.Equal(2, second.UpdatedCount); // updated in place, not duplicated

        await using var context = CreateContext();
        var all = await new SubjectEntitlementRepository(context)
            .ListBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Re_assigning_a_plan_reinstates_a_previously_revoked_entitlement()
    {
        var (plan, adFree, _) = await SeedStandardPlanAsync();
        var subjectId = Guid.CreateVersion7();
        await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at);
        Assert.True(await RevokeAsync(EntitlementSubjectType.User, subjectId, adFree.Id));
        Assert.False((await ResolveAsync(EntitlementSubjectType.User, subjectId)).IsFlagEnabled("ads.disabled"));

        await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at.AddHours(2));

        // The re-grant brings the revoked entitlement back into force.
        Assert.True((await ResolveAsync(EntitlementSubjectType.User, subjectId)).IsFlagEnabled("ads.disabled"));
    }

    // --- Fail-closed: unknown / retired plan ------------------------------------

    [Fact]
    public async Task AssignFromPlan_for_an_unknown_plan_assigns_nothing()
    {
        var subjectId = Guid.CreateVersion7();

        var result = await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, Guid.CreateVersion7(), _at);

        Assert.Equal(SubjectEntitlementAssignmentOutcome.PlanNotFound, result.Outcome);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    [Fact]
    public async Task AssignFromPlan_for_a_retired_plan_assigns_nothing()
    {
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        var plan = PlanDefinition.Define("legacy", "Legacy", null, _at);
        plan.GrantFlag(adFree, true);
        plan.Deactivate(_at.AddMinutes(1)); // retired before assignment
        await SeedPlanAsync(plan);
        var subjectId = Guid.CreateVersion7();

        var result = await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at.AddHours(1));

        Assert.Equal(SubjectEntitlementAssignmentOutcome.PlanInactive, result.Outcome);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    // --- Fail-closed: retired entitlement is never newly assigned ---------------

    [Fact]
    public async Task AssignFromPlan_skips_a_grant_whose_definition_was_retired()
    {
        var (plan, adFree, _) = await SeedStandardPlanAsync();
        // The ad-free entitlement is retired AFTER the plan granted it; the quota stays active.
        await RetireDefinitionAsync(adFree.Id);
        var subjectId = Guid.CreateVersion7();

        var result = await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at.AddHours(1));

        Assert.Equal(SubjectEntitlementAssignmentOutcome.Assigned, result.Outcome);
        Assert.Equal(1, result.AssignedCount); // only the still-active quota
        Assert.Equal(1, result.SkippedCount); // the retired flag is not newly assigned (fail-closed)

        var effective = await ResolveAsync(EntitlementSubjectType.User, subjectId);
        Assert.False(effective.IsFlagEnabled("ads.disabled")); // never assigned
        Assert.True(effective.TryGetQuotaLimit("workspace.active.max", out _));
    }

    // --- Negative: revoke -------------------------------------------------------

    [Fact]
    public async Task Revoke_removes_the_premium_state()
    {
        var (plan, adFree, _) = await SeedStandardPlanAsync();
        var subjectId = Guid.CreateVersion7();
        await AssignFromPlanAsync(EntitlementSubjectType.User, subjectId, plan.Id, _at);
        Assert.True((await ResolveAsync(EntitlementSubjectType.User, subjectId)).IsFlagEnabled("ads.disabled"));

        var revoked = await RevokeAsync(EntitlementSubjectType.User, subjectId, adFree.Id);

        Assert.True(revoked);
        Assert.False((await ResolveAsync(EntitlementSubjectType.User, subjectId)).IsFlagEnabled("ads.disabled"));
    }

    [Fact]
    public async Task Revoke_of_an_entitlement_the_subject_does_not_hold_is_a_no_op()
    {
        var subjectId = Guid.CreateVersion7();
        var unheld = await SeedDefinitionAsync(FlagDefinition("export.enabled"));

        Assert.False(await RevokeAsync(EntitlementSubjectType.User, subjectId, unheld.Id));
    }
}
