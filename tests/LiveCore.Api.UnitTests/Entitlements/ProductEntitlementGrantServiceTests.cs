using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for <see cref="ProductEntitlementGrantService"/> (CORE-MON-003) — the purchase-to-
/// entitlement grant chain. The service maps a verified purchase's product reference to a generic plan by the
/// plan's stable key and REUSES the CORE-ENTL-002 <see cref="SubjectEntitlementAssignmentService"/> to assign the
/// plan's grants (no new table, no parallel assignment path; docs/21 / docs/24).
///
/// The tests run the service over the real repositories on an in-memory SQLite database and verify the result by
/// RESOLVING the subject's effective entitlements — so the product -> plan -> entitlement chain is exercised end
/// to end through the same read (<see cref="SubjectEntitlementResolver"/>) the GET /api/v1/me/entitlements endpoint
/// uses. They pin the story's behavior: a mapped product grants exactly the plan's entitlements (positive), a
/// repeat converges in place rather than double-granting (idempotent on (purchase, entitlement)), and an unmapped
/// product, an invalid product reference, a retired plan and a blank reference all grant nothing (fail-closed).
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class ProductEntitlementGrantServiceTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private const string _productReference = "product.premium";
    private const string _adsDisabledKey = "ads.disabled";
    private const string _workspaceLimitKey = "workspace.active.max";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ProductEntitlementGrantServiceTests()
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

    // Each call models its own request scope: a fresh context with the plan repository and the (reused) assignment
    // service over it.
    private async Task<ProductEntitlementGrantResult> GrantForProductAsync(
        EntitlementSubjectType subjectType, Guid subjectId, string productReference, DateTimeOffset at)
    {
        await using var context = CreateContext();
        var service = new ProductEntitlementGrantService(
            new PlanDefinitionRepository(context),
            new SubjectEntitlementAssignmentService(
                new SubjectEntitlementRepository(context),
                new PlanDefinitionRepository(context),
                new EntitlementDefinitionRepository(context)));
        return await service.GrantForProductAsync(subjectType, subjectId, productReference, at, CancellationToken.None);
    }

    private async Task<EffectiveEntitlements> ResolveAsync(EntitlementSubjectType subjectType, Guid subjectId)
    {
        await using var context = CreateContext();
        var resolver = new SubjectEntitlementResolver(new SubjectEntitlementRepository(context));
        return await resolver.ResolveAsync(subjectType, subjectId, CancellationToken.None);
    }

    private async Task<int> SubjectEntitlementCountAsync(EntitlementSubjectType subjectType, Guid subjectId)
    {
        await using var context = CreateContext();
        return await context.SubjectEntitlements
            .AsNoTracking()
            .CountAsync(e => e.SubjectType == subjectType && e.SubjectId == subjectId);
    }

    private async Task<EntitlementDefinition> SeedDefinitionAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
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

    /// <summary>
    /// Seeds two entitlement definitions and a plan keyed by the product reference, granting the flag (true) and
    /// the quota (5) — the plan a verified purchase of <see cref="_productReference"/> maps to.
    /// </summary>
    private async Task<PlanDefinition> SeedProductPlanAsync(string planKey = _productReference)
    {
        var adFree = await SeedDefinitionAsync(FlagDefinition(_adsDisabledKey));
        var workspaceLimit = await SeedDefinitionAsync(QuotaDefinition(_workspaceLimitKey));
        var plan = PlanDefinition.Define(planKey, "Premium", null, _at);
        plan.GrantFlag(adFree, true);
        plan.GrantQuota(workspaceLimit, 5);
        await SeedPlanAsync(plan);
        return plan;
    }

    // --- Positive: a mapped product grants exactly the plan's entitlements ------

    [Fact]
    public async Task A_mapped_product_grants_exactly_the_plans_entitlements_to_the_buyer()
    {
        var plan = await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, _productReference, _at);

        Assert.Equal(ProductEntitlementGrantOutcome.Granted, result.Outcome);
        Assert.Equal(plan.Id, result.PlanDefinitionId);
        Assert.NotNull(result.Assignment);
        Assert.Equal(2, result.Assignment.AssignedCount);

        // The grant shows up in the effective-entitlements read — exactly the two mapped entitlements.
        var effective = await ResolveAsync(EntitlementSubjectType.User, subjectId);
        Assert.Equal(2, effective.Count);
        Assert.True(effective.IsFlagEnabled(_adsDisabledKey));
        Assert.True(effective.TryGetQuotaLimit(_workspaceLimitKey, out var limit));
        Assert.Equal(5, limit);
    }

    [Fact]
    public async Task The_product_reference_is_canonicalized_to_the_plan_key()
    {
        // A differently-cased / whitespace-padded product reference resolves to the same plan key.
        await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, "  Product.Premium  ", _at);

        Assert.Equal(ProductEntitlementGrantOutcome.Granted, result.Outcome);
        Assert.True((await ResolveAsync(EntitlementSubjectType.User, subjectId)).IsFlagEnabled(_adsDisabledKey));
    }

    // --- Idempotency: a duplicate purchase does not double-grant ----------------

    [Fact]
    public async Task Granting_the_same_product_twice_converges_and_does_not_double_grant()
    {
        await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        await GrantForProductAsync(EntitlementSubjectType.User, subjectId, _productReference, _at);
        var second = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, _productReference, _at.AddHours(1));

        Assert.Equal(ProductEntitlementGrantOutcome.Granted, second.Outcome);
        Assert.NotNull(second.Assignment);
        Assert.Equal(0, second.Assignment.AssignedCount); // nothing newly created
        Assert.Equal(2, second.Assignment.UpdatedCount); // updated in place, not duplicated

        // Still exactly two assignments — the duplicate purchase did not double-grant.
        Assert.Equal(2, await SubjectEntitlementCountAsync(EntitlementSubjectType.User, subjectId));
        Assert.Equal(2, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    // --- Fail-closed: unmapped / invalid / retired / blank ----------------------

    [Fact]
    public async Task An_unknown_product_grants_nothing()
    {
        await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, "product.unmapped", _at);

        Assert.Equal(ProductEntitlementGrantOutcome.ProductNotMapped, result.Outcome);
        Assert.Null(result.PlanDefinitionId);
        Assert.Null(result.Assignment);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    [Fact]
    public async Task A_product_reference_that_is_not_a_valid_plan_key_grants_nothing()
    {
        // A reference that is not a valid plan-key shape can name no plan; it grants nothing rather than throwing.
        await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, "Not A Valid Key!", _at);

        Assert.Equal(ProductEntitlementGrantOutcome.ProductNotMapped, result.Outcome);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    [Fact]
    public async Task A_retired_plan_grants_nothing()
    {
        var adFree = await SeedDefinitionAsync(FlagDefinition(_adsDisabledKey));
        var plan = PlanDefinition.Define(_productReference, "Premium", null, _at);
        plan.GrantFlag(adFree, true);
        plan.Deactivate(_at.AddMinutes(1)); // retired before any purchase maps to it
        await SeedPlanAsync(plan);
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, _productReference, _at.AddHours(1));

        Assert.Equal(ProductEntitlementGrantOutcome.ProductNotMapped, result.Outcome);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_product_reference_grants_nothing(string productReference)
    {
        await SeedProductPlanAsync();
        var subjectId = Guid.CreateVersion7();

        var result = await GrantForProductAsync(EntitlementSubjectType.User, subjectId, productReference, _at);

        Assert.Equal(ProductEntitlementGrantOutcome.ProductNotMapped, result.Outcome);
        Assert.Equal(0, (await ResolveAsync(EntitlementSubjectType.User, subjectId)).Count);
    }

    // --- Guards -----------------------------------------------------------------

    [Fact]
    public async Task An_empty_subject_id_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GrantForProductAsync(EntitlementSubjectType.User, Guid.Empty, _productReference, _at));
    }
}
