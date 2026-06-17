// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the NARROW cross-product entitlement revocation (CORE-MON-012, the "Narrow
/// cross-product entitlement over-revocation" story of the "Monetization v1" epic) — the story's required test: "A
/// subject with two products sharing an entitlement: refunding one keeps the entitlement (still granted by the other
/// active product); refunding both revokes it."
///
/// They run the real <see cref="StoreNotificationService"/> WITH its <see cref="PurchaseEntitlementRevocationService"/>
/// collaborator over the real repositories on an in-memory SQLite database (the same harness as
/// <see cref="PurchaseRevocationMonotonicTests"/>), wiring the full grant chain (CORE-MON-002 buyer link +
/// CORE-MON-003 product → plan → entitlement grant) for TWO products of the SAME subject whose plans share an
/// entitlement, so the retention effect of a refund is observable end to end through the same effective-entitlements
/// read the GET /api/v1/me/entitlements endpoint uses.
///
/// Before CORE-MON-012, <see cref="ProductEntitlementGrantService.RevokeForProductAsync(EntitlementSubjectType, Guid,
/// string, DateTimeOffset, CancellationToken)"/> revoked ALL of a refunded product's plan entitlements
/// unconditionally; because a subject holds each entitlement at most once, refunding one of two products sharing an
/// entitlement stripped the shared entitlement the other still-active product legitimately granted. These tests pin
/// the fix: a shared entitlement is RETAINED while another active purchase of the SAME subject still grants it, the
/// refunded product's NON-shared entitlements are still revoked, refunding the last product holding the shared
/// entitlement finally revokes it, and the retention is SUBJECT-scoped (a different subject's active purchase never
/// retains this subject's entitlement — fail-closed isolation, threat T5).
///
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class CrossProductEntitlementRetentionTests : IDisposable
{
    // The shared entitlement both products grant; the per-product distinct entitlements.
    private const string _adFreeKey = "ads.disabled";
    private const string _exportKey = "export.enabled";
    private const string _supportKey = "support.priority";

    // Two products of the same subject whose plans (keyed by the product reference) share the ad-free entitlement.
    private const string _alphaProduct = "product.alpha";
    private const string _betaProduct = "product.beta";

    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _graceEvent = new(2026, 6, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _alphaRefund = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _betaRefund = new(2026, 6, 12, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _receivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public CrossProductEntitlementRetentionTests()
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

    // The full webhook graph: the notification service WITH its revocation collaborator, exactly as the API/worker
    // hosts wire it. Built per call over a fresh context to model a request scope.
    private StoreNotificationService NotificationService(LiveCoreDbContext context)
        => new(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context),
            new PurchaseEntitlementRevocationService(
                new PurchaseTransactionRepository(context),
                new BillingAccountLinkRepository(context),
                ProductGrants(context)));

    private static ProductEntitlementGrantService ProductGrants(LiveCoreDbContext context)
        => new(
            new PlanDefinitionRepository(context),
            new SubjectEntitlementAssignmentService(
                new SubjectEntitlementRepository(context),
                new PlanDefinitionRepository(context),
                new EntitlementDefinitionRepository(context)));

    private static EntitlementDefinition FlagDefinition(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, key, null, _recordedAt);

    /// <summary>
    /// Seeds the entitlement catalog and the two product plans. <c>product.alpha</c> always grants the shared ad-free
    /// flag; <c>product.beta</c> always grants the shared ad-free flag too. When
    /// <paramref name="withDistinctEntitlements"/> is set, each plan additionally grants a product-specific flag
    /// (alpha → export, beta → support) so the test can show the refunded product's NON-shared entitlement is still
    /// revoked while the shared one is retained.
    /// </summary>
    private async Task SeedCatalogAsync(bool withDistinctEntitlements)
    {
        await using var context = CreateContext();

        var adFree = FlagDefinition(_adFreeKey);
        context.EntitlementDefinitions.Add(adFree);

        EntitlementDefinition? export = null;
        EntitlementDefinition? support = null;
        if (withDistinctEntitlements)
        {
            export = FlagDefinition(_exportKey);
            support = FlagDefinition(_supportKey);
            context.EntitlementDefinitions.AddRange(export, support);
        }

        await context.SaveChangesAsync();

        var alpha = PlanDefinition.Define(_alphaProduct, "Alpha", null, _recordedAt);
        alpha.GrantFlag(adFree, true);
        if (export is not null)
        {
            alpha.GrantFlag(export, true);
        }

        var beta = PlanDefinition.Define(_betaProduct, "Beta", null, _recordedAt);
        beta.GrantFlag(adFree, true);
        if (support is not null)
        {
            beta.GrantFlag(support, true);
        }

        var plans = new PlanDefinitionRepository(context);
        Assert.Equal(PlanDefinitionAddResult.Added, await plans.AddAsync(alpha, CancellationToken.None));
        Assert.Equal(PlanDefinitionAddResult.Added, await plans.AddAsync(beta, CancellationToken.None));
    }

    // Records the verified purchase, links the buyer subject and grants the mapped entitlement — the verified,
    // buyer-linked, granted state a purchase is in before a refund (the CORE-MON-002/003 chain).
    private async Task RecordLinkGrantAsync(Guid subjectId, string transactionId, string productReference)
    {
        await using var context = CreateContext();

        var recording = await new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context))
            .RecordVerifiedPurchaseAsync(
                VerifiedPurchase.Create(PurchaseProvider.Apple, transactionId, productReference),
                _recordedAt,
                CancellationToken.None);

        await new BillingAccountLinkService(new BillingAccountLinkRepository(context))
            .LinkBuyerAsync(recording.Transaction.Id, EntitlementSubjectType.User, subjectId, _recordedAt, CancellationToken.None);

        await ProductGrants(context).GrantForProductAsync(
            EntitlementSubjectType.User, subjectId, productReference, _recordedAt, CancellationToken.None);
    }

    private async Task<StoreNotificationProcessingResult> HandleAsync(StoreNotification notification)
    {
        await using var context = CreateContext();
        return await NotificationService(context).HandleAsync(notification, _receivedAt, CancellationToken.None);
    }

    private async Task<EffectiveEntitlements> ResolveAsync(Guid subjectId)
    {
        await using var context = CreateContext();
        return await new SubjectEntitlementResolver(new SubjectEntitlementRepository(context))
            .ResolveAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None);
    }

    private async Task<PurchaseTransactionStatus> StatusAsync(string transactionId)
    {
        await using var context = CreateContext();
        var transaction = await new PurchaseTransactionRepository(context)
            .FindByProviderTransactionAsync(PurchaseProvider.Apple, transactionId, CancellationToken.None);
        Assert.NotNull(transaction);
        return transaction!.Status;
    }

    private static StoreNotification Notification(
        string notificationId, StoreNotificationType type, string transactionId, DateTimeOffset occurredAt)
        => StoreNotification.Create(PurchaseProvider.Apple, notificationId, type, transactionId, occurredAt);

    // --- The required test: refund one keeps the shared entitlement; refund both revokes it ---

    [Fact]
    public async Task Refunding_one_of_two_products_sharing_an_entitlement_keeps_it_then_refunding_both_revokes_it()
    {
        await SeedCatalogAsync(withDistinctEntitlements: false);
        var subjectId = Guid.CreateVersion7();
        await RecordLinkGrantAsync(subjectId, "txn-alpha", _alphaProduct);
        await RecordLinkGrantAsync(subjectId, "txn-beta", _betaProduct);

        // The subject holds the shared entitlement (granted by both products, held once).
        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey));

        // Refund product.alpha: product.beta is still active and also grants the shared entitlement, so it is RETAINED.
        var alpha = await HandleAsync(Notification("ntf-alpha-refund", StoreNotificationType.Refunded, "txn-alpha", _alphaRefund));
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, alpha.Outcome);
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync("txn-alpha"));
        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey)); // kept — still granted by product.beta

        // Refund product.beta: no other active purchase grants the shared entitlement now, so it is finally REVOKED.
        var beta = await HandleAsync(Notification("ntf-beta-refund", StoreNotificationType.Refunded, "txn-beta", _betaRefund));
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, beta.Outcome);
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync("txn-beta"));
        Assert.Equal(0, (await ResolveAsync(subjectId)).Count); // gone — no active purchase grants it
    }

    // --- The refunded product's NON-shared entitlement is still revoked while the shared one is retained ---

    [Fact]
    public async Task Refunding_a_product_revokes_only_its_non_shared_entitlements_and_retains_the_shared_one()
    {
        await SeedCatalogAsync(withDistinctEntitlements: true);
        var subjectId = Guid.CreateVersion7();
        await RecordLinkGrantAsync(subjectId, "txn-alpha", _alphaProduct);
        await RecordLinkGrantAsync(subjectId, "txn-beta", _betaProduct);

        // The subject holds three entitlements: the shared ad-free, alpha's export and beta's support.
        var before = await ResolveAsync(subjectId);
        Assert.Equal(3, before.Count);

        await HandleAsync(Notification("ntf-alpha-refund", StoreNotificationType.Refunded, "txn-alpha", _alphaRefund));

        var after = await ResolveAsync(subjectId);
        Assert.True(after.IsFlagEnabled(_adFreeKey));   // shared — retained (product.beta still grants it)
        Assert.True(after.IsFlagEnabled(_supportKey));  // beta's own — untouched by alpha's refund
        Assert.False(after.IsFlagEnabled(_exportKey));  // alpha's own, non-shared — revoked
        Assert.Equal(2, after.Count);
    }

    // --- A still-active purchase in a grace period (non-revoked) still retains the shared entitlement ---

    [Fact]
    public async Task A_purchase_in_a_grace_period_still_retains_the_shared_entitlement()
    {
        await SeedCatalogAsync(withDistinctEntitlements: false);
        var subjectId = Guid.CreateVersion7();
        await RecordLinkGrantAsync(subjectId, "txn-alpha", _alphaProduct);
        await RecordLinkGrantAsync(subjectId, "txn-beta", _betaProduct);

        // Drive product.beta into an explicit grace period: it is NOT revoked, so it is still an active grantor.
        await HandleAsync(Notification("ntf-beta-grace", StoreNotificationType.GracePeriodStarted, "txn-beta", _graceEvent));
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, await StatusAsync("txn-beta"));

        // Refunding product.alpha must retain the shared entitlement — a grace-period purchase still grants it.
        await HandleAsync(Notification("ntf-alpha-refund", StoreNotificationType.Refunded, "txn-alpha", _alphaRefund));

        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey));
    }

    // --- Subject isolation (fail-closed): a DIFFERENT subject's active purchase never retains this subject's
    //     entitlement (threat T5). ---

    [Fact]
    public async Task A_different_subjects_active_purchase_does_not_retain_this_subjects_entitlement()
    {
        await SeedCatalogAsync(withDistinctEntitlements: false);
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();

        // Subject A buys product.alpha; subject B buys product.beta. Both products grant the same ad-free entitlement,
        // and B's purchase stays active throughout.
        await RecordLinkGrantAsync(subjectA, "txn-a-alpha", _alphaProduct);
        await RecordLinkGrantAsync(subjectB, "txn-b-beta", _betaProduct);
        Assert.True((await ResolveAsync(subjectA)).IsFlagEnabled(_adFreeKey));
        Assert.True((await ResolveAsync(subjectB)).IsFlagEnabled(_adFreeKey));

        // A refunds: B's active purchase of a product that grants the SAME entitlement must NOT retain A's entitlement,
        // because retention is scoped to the SAME subject. A loses it; B is untouched.
        await HandleAsync(Notification("ntf-a-refund", StoreNotificationType.Refunded, "txn-a-alpha", _alphaRefund));

        Assert.Equal(0, (await ResolveAsync(subjectA)).Count);          // A's entitlement is revoked (no A purchase grants it)
        Assert.True((await ResolveAsync(subjectB)).IsFlagEnabled(_adFreeKey)); // B keeps their own
    }

    // --- A second still-active purchase of the SAME product retains everything ---

    [Fact]
    public async Task Refunding_one_of_two_purchases_of_the_same_product_retains_the_entitlement()
    {
        await SeedCatalogAsync(withDistinctEntitlements: false);
        var subjectId = Guid.CreateVersion7();

        // Two separate purchases of the SAME product (distinct provider transaction ids), both granting the ad-free
        // entitlement.
        await RecordLinkGrantAsync(subjectId, "txn-alpha-1", _alphaProduct);
        await RecordLinkGrantAsync(subjectId, "txn-alpha-2", _alphaProduct);
        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey));

        // Refunding the first purchase retains the entitlement: the second purchase of the same product still grants it.
        await HandleAsync(Notification("ntf-alpha-1-refund", StoreNotificationType.Refunded, "txn-alpha-1", _alphaRefund));

        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync("txn-alpha-1"));
        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync("txn-alpha-2"));
        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey));
    }
}
