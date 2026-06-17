// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="PlanDefinitionRepository"/> (CORE-ENTL-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, the SQL translation, the unique <c>plan_definitions(key)</c> index, the unique
/// <c>plan_entitlements(plan_definition_id, entitlement_definition_id)</c> grant index, the cascade from the
/// plan to its grants and the RESTRICT on a referenced entitlement definition are exercised on every run
/// without any database server. The plan definition is a GLOBAL catalog (no organization_id), so there is no
/// foreign-tenant axis (and no HTTP route in this story); the fail-closed cases that matter for a plan catalog
/// — the rejected duplicate key, the dangling-grant foreign-key rejection, the cascade and the delete-restrict
/// — are asserted below. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class PlanDefinitionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PlanDefinitionRepositoryTests()
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

    private async Task<EntitlementDefinition> SeedEntitlementAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        Assert.Equal(
            EntitlementDefinitionAddResult.Added,
            await repository.AddAsync(definition, CancellationToken.None));
        return definition;
    }

    private static EntitlementDefinition QuotaEntitlement(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "A generic quota", null, _createdAt);

    private static EntitlementDefinition FlagEntitlement(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "A generic flag", null, _createdAt);

    [Fact]
    public async Task Plan_with_grants_round_trips_through_the_database()
    {
        var quota = await SeedEntitlementAsync(QuotaEntitlement("workspace.active.max"));
        var unlimited = await SeedEntitlementAsync(QuotaEntitlement("session.active.max"));
        var flag = await SeedEntitlementAsync(FlagEntitlement("ads.disabled"));

        var plan = PlanDefinition.Define("standard", "Standard plan", "A generic plan.", _createdAt);
        plan.GrantQuota(quota, 5);
        plan.GrantQuota(unlimited, null); // unlimited / fair-use
        plan.GrantFlag(flag, true);

        await using (var writeContext = CreateContext())
        {
            Assert.Equal(
                PlanDefinitionAddResult.Added,
                await new PlanDefinitionRepository(writeContext).AddAsync(plan, CancellationToken.None));
        }

        await using var context = CreateContext();
        var repository = new PlanDefinitionRepository(context);
        var loaded = await repository.FindByKeyAsync("standard", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(plan.Id, loaded.Id);
        Assert.Equal(3, loaded.Entitlements.Count);

        var quotaGrant = loaded.Entitlements.Single(grant => grant.EntitlementDefinitionId == quota.Id);
        Assert.Equal(EntitlementValueKind.Quota, quotaGrant.ValueKind);
        Assert.Equal(5, quotaGrant.QuotaLimit);

        var unlimitedGrant = loaded.Entitlements.Single(grant => grant.EntitlementDefinitionId == unlimited.Id);
        Assert.True(unlimitedGrant.IsUnlimitedQuota);
        Assert.Null(unlimitedGrant.QuotaLimit);

        var flagGrant = loaded.Entitlements.Single(grant => grant.EntitlementDefinitionId == flag.Id);
        Assert.Equal(EntitlementValueKind.Flag, flagGrant.ValueKind);
        Assert.Equal(true, flagGrant.FlagValue);
    }

    [Fact]
    public async Task FindById_loads_the_plan_with_its_grants()
    {
        var quota = await SeedEntitlementAsync(QuotaEntitlement("workspace.active.max"));
        var plan = PlanDefinition.Define("free", "Free plan", null, _createdAt);
        plan.GrantQuota(quota, 1);

        await using (var writeContext = CreateContext())
        {
            await new PlanDefinitionRepository(writeContext).AddAsync(plan, CancellationToken.None);
        }

        await using var context = CreateContext();
        var loaded = await new PlanDefinitionRepository(context).FindByIdAsync(plan.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Entitlements);
    }

    [Fact]
    public async Task ListAll_returns_plans_in_key_order_with_their_grants()
    {
        var quota = await SeedEntitlementAsync(QuotaEntitlement("workspace.active.max"));

        foreach (var key in new[] { "pro", "free", "standard" })
        {
            var plan = PlanDefinition.Define(key, $"{key} plan", null, _createdAt);
            plan.GrantQuota(quota, 1);
            await using var writeContext = CreateContext();
            await new PlanDefinitionRepository(writeContext).AddAsync(plan, CancellationToken.None);
        }

        await using var context = CreateContext();
        var all = await new PlanDefinitionRepository(context).ListAllAsync(CancellationToken.None);

        Assert.Equal(new[] { "free", "pro", "standard" }, all.Select(plan => plan.Key).ToArray());
        Assert.All(all, plan => Assert.Single(plan.Entitlements));
    }

    [Fact]
    public async Task Adding_a_duplicate_plan_key_is_rejected()
    {
        await using (var writeContext = CreateContext())
        {
            await new PlanDefinitionRepository(writeContext).AddAsync(
                PlanDefinition.Define("standard", "Standard plan", null, _createdAt), CancellationToken.None);
        }

        await using var context = CreateContext();
        var repository = new PlanDefinitionRepository(context);
        var result = await repository.AddAsync(
            PlanDefinition.Define("standard", "Another standard plan", null, _createdAt), CancellationToken.None);

        Assert.Equal(PlanDefinitionAddResult.DuplicateKey, result);
        Assert.Single(await repository.ListAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_grant_for_a_non_existent_entitlement_definition_is_rejected()
    {
        // The entitlement_definition_id foreign key is enforced (PRAGMA foreign_keys = ON): a grant that points
        // at an entitlement definition that does not exist is rejected, so a dangling grant can never be
        // persisted.
        var ghostEntitlement = QuotaEntitlement("workspace.active.max"); // NOT seeded
        var plan = PlanDefinition.Define("standard", "Standard plan", null, _createdAt);
        plan.GrantQuota(ghostEntitlement, 5);

        await using var context = CreateContext();
        var repository = new PlanDefinitionRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(plan, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_plan_cascades_to_its_grants()
    {
        var quota = await SeedEntitlementAsync(QuotaEntitlement("workspace.active.max"));
        var plan = PlanDefinition.Define("standard", "Standard plan", null, _createdAt);
        plan.GrantQuota(quota, 5);

        await using (var writeContext = CreateContext())
        {
            await new PlanDefinitionRepository(writeContext).AddAsync(plan, CancellationToken.None);
        }

        await using (var deleteContext = CreateContext())
        {
            var storedPlan = await deleteContext.PlanDefinitions.SingleAsync(p => p.Id == plan.Id);
            deleteContext.PlanDefinitions.Remove(storedPlan);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        // The plan's grants are removed with it; the entitlement definition it referenced survives.
        Assert.Equal(0, await context.PlanEntitlements.CountAsync(CancellationToken.None));
        Assert.Equal(1, await context.EntitlementDefinitions.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_referenced_entitlement_definition_cannot_be_deleted()
    {
        // The entitlement_definition_id foreign key is RESTRICT on delete: a definition that a plan grants
        // cannot be hard-deleted out from under the grant (it is soft-retired via is_active instead), so a
        // grant can never dangle (docs/10_DATABASE_SCHEMA.md).
        var quota = await SeedEntitlementAsync(QuotaEntitlement("workspace.active.max"));
        var plan = PlanDefinition.Define("standard", "Standard plan", null, _createdAt);
        plan.GrantQuota(quota, 5);

        await using (var writeContext = CreateContext())
        {
            await new PlanDefinitionRepository(writeContext).AddAsync(plan, CancellationToken.None);
        }

        await using var deleteContext = CreateContext();
        var storedEntitlement = await deleteContext.EntitlementDefinitions.SingleAsync(e => e.Id == quota.Id);
        deleteContext.EntitlementDefinitions.Remove(storedEntitlement);

        await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindById_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new PlanDefinitionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, CancellationToken.None));
    }
}
