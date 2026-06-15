using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Real WRITE-CONCURRENCY coverage for the money paths on PostgreSQL (CORE-TST-006). The duplicate /
/// race-resolution branches in the billing repositories — the <c>DbUpdateException -&gt; re-read -&gt; resolve</c>
/// recovery in <see cref="BillingAccountLinkRepository"/>, <see cref="PurchaseTransactionRepository"/> and
/// <see cref="SubjectEntitlementRepository"/>, plus the <c>xmin</c>
/// <see cref="DbUpdateConcurrencyException"/> guard on a purchase-status change — were until now only REASONED,
/// never exercised by two genuinely concurrent writers: the other store/entitlement integration tests run on a
/// single shared SQLite connection that serializes every write, and the cross-subject tests are sequential
/// A-then-B, so the loser's re-read branch never fired. These tests close that gap on the CI
/// <c>integration-postgres</c> leg.
///
/// <para>
/// Each test races TWO writers, each on its OWN <see cref="LiveCoreDbContext"/> (its own DI scope, and so its own
/// Npgsql connection on the PostgreSQL leg), at the same colliding key:
/// <list type="bullet">
///   <item>two writers claim the same purchase with a <c>billing_account_link</c> (different subjects) — the
///   unique <c>billing_account_links(purchase_transaction_id)</c> index lets exactly one win, and the loser's
///   re-read resolves to a duplicate rather than a second claim;</item>
///   <item>two writers grant the same <c>(subject, entitlement)</c> — the unique per-subject index lets exactly
///   one win, the loser resolves to a duplicate, and the subject ends with exactly ONE grant (no double-grant);</item>
///   <item>two writers record the same <c>(provider, provider_transaction_id)</c> purchase — exactly one is
///   persisted, the loser resolves to the canonical row (idempotent recording);</item>
///   <item>two writers apply the same purchase-status change — the <c>xmin</c> token makes exactly one win and
///   the loser conflict LOUDLY (a <see cref="DbUpdateConcurrencyException"/>) instead of a silent double-apply;
///   the loser re-reads the single committed change and converges (a convergent retry is an idempotent no-op).</item>
/// </list>
/// </para>
///
/// <para>
/// The conflict semantics are PostgreSQL-specific (the <c>xmin</c> row-version token is mapped only on Npgsql, and
/// a unique-violation rolls back only an in-flight transaction — see <see cref="LiveCoreDbContext"/>), so the
/// truly-concurrent race runs only when the suite is on PostgreSQL (<see cref="PostgresTestDatabase.IsConfigured"/>).
/// On the default SQLite path the same writers run SEQUENTIALLY (the shared connection cannot truly overlap): the
/// second insert still collides with the first's committed unique row, so the same re-read branch is exercised —
/// only without the true concurrency the PostgreSQL leg adds — keeping every test green on both providers. This
/// mirrors <see cref="OptimisticConcurrencyTokenTests"/> and <see cref="RemainingAggregatesOptimisticConcurrencyTokenTests"/>.
/// </para>
///
/// <para>
/// The final test races the whole money path over real HTTP. Two buyers submit the SAME receipt concurrently to
/// the Apple verification endpoint, whose record + link + grant run inside ONE explicit unit-of-work transaction.
/// On PostgreSQL a unique violation aborts that transaction, so the in-transaction re-read cannot recover; the
/// duplicate therefore surfaces EITHER as a clean 409 (when the two requests serialize and the loser's find-first
/// sees the committed link) OR as a 500 that succeeds on retry (when the inserts truly collide and abort the
/// loser's transaction) — but NEVER as a double-grant. Whatever the timing, exactly one buyer wins the receipt and
/// is granted, the other gets nothing: one billing link, one buyer's worth of entitlements, and no cross-subject
/// leak (threat T5). This is the documented confirmation the story asks for. All fixtures are generic
/// Store/Entitlements vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </para>
/// </summary>
public sealed class MoneyPathWriteConcurrencyTests
{
    private const string _issuer = "https://issuer.test";
    private const string _appleRoute = "/api/v1/purchases/apple/transactions";
    private const string _premiumProduct = "product.premium";
    private const string _adsDisabledKey = "ads.disabled";
    private const string _workspaceLimitKey = "workspace.active.max";

    private static readonly DateTimeOffset _at = TestData.SeedTime.AddMinutes(5);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // --- billing_account_link: two writers race the same purchase --------------

    [Fact]
    public async Task Two_concurrent_writers_racing_one_billing_account_link_resolve_to_a_single_link()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var purchaseId = await SeedPurchaseAsync(factory, PurchaseProvider.Apple, "txn-race-link", _premiumProduct);

        // Two DIFFERENT subjects each try to claim the SAME purchase at the same time — the cross-subject race the
        // existing sequential A-then-B test never fires (its find-first short-circuits before the insert).
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var (resultA, resultB) = await RaceAsync(
            () => new BillingAccountLinkRepository(contextA).AddAsync(
                BillingAccountLink.Link(purchaseId, EntitlementSubjectType.User, subjectA, _at),
                CancellationToken.None),
            () => new BillingAccountLinkRepository(contextB).AddAsync(
                BillingAccountLink.Link(purchaseId, EntitlementSubjectType.User, subjectB, _at),
                CancellationToken.None));

        // Exactly one writer won (Added); the loser's DbUpdateException -> re-read -> resolve branch fired and
        // reported the duplicate — never a second link, never a double-claim.
        var outcomes = new[] { resultA, resultB };
        Assert.Single(outcomes, outcome => outcome == BillingAccountLinkAddResult.Added);
        Assert.Single(outcomes, outcome => outcome == BillingAccountLinkAddResult.DuplicatePurchase);

        // One purchase, one link.
        Assert.Equal(1, await BillingLinkCountAsync(factory, purchaseId));
    }

    // --- subject entitlement: two writers race the same (subject, entitlement) grant ---

    [Fact]
    public async Task Two_concurrent_writers_racing_one_subject_entitlement_grant_resolve_to_a_single_grant()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var definition = await SeedFlagDefinitionAsync(factory, _adsDisabledKey);
        var subjectId = Guid.CreateVersion7();

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // Two writers grant the SAME entitlement to the SAME subject at the same time — the lost assignment race
        // the assignment service reasons about (a duplicate verified purchase / retry hitting the grant chain).
        var (resultA, resultB) = await RaceAsync(
            () => new SubjectEntitlementRepository(contextA).AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, definition, true, null, _at),
                CancellationToken.None),
            () => new SubjectEntitlementRepository(contextB).AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, definition, true, null, _at),
                CancellationToken.None));

        // Exactly one writer won; the loser's re-read resolved to the duplicate — so the subject is granted the
        // entitlement EXACTLY ONCE, never twice.
        var outcomes = new[] { resultA, resultB };
        Assert.Single(outcomes, outcome => outcome == SubjectEntitlementAddResult.Added);
        Assert.Single(outcomes, outcome => outcome == SubjectEntitlementAddResult.DuplicateAssignment);

        Assert.Equal(1, await SubjectEntitlementRowCountAsync(factory, subjectId, definition.Id));
    }

    // --- purchase transaction: two writers race the same recording --------------

    [Fact]
    public async Task Two_concurrent_writers_racing_one_purchase_recording_resolve_to_a_single_transaction()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        const string providerTransactionId = "txn-race-record";

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // Two writers record the SAME (provider, provider transaction id) purchase at the same time — a replayed
        // proof / duplicate notification racing the first recording.
        var (resultA, resultB) = await RaceAsync(
            () => new PurchaseTransactionRepository(contextA).AddAsync(
                PurchaseTransaction.Record(
                    VerifiedPurchase.Create(PurchaseProvider.Apple, providerTransactionId, _premiumProduct), _at),
                CancellationToken.None),
            () => new PurchaseTransactionRepository(contextB).AddAsync(
                PurchaseTransaction.Record(
                    VerifiedPurchase.Create(PurchaseProvider.Apple, providerTransactionId, _premiumProduct), _at),
                CancellationToken.None));

        // Exactly one writer persisted the purchase; the loser's re-read resolved to the duplicate — exactly one
        // transaction row, idempotent recording, never a second money row.
        var outcomes = new[] { resultA, resultB };
        Assert.Single(outcomes, outcome => outcome == PurchaseTransactionAddResult.Added);
        Assert.Single(outcomes, outcome => outcome == PurchaseTransactionAddResult.DuplicateTransaction);

        Assert.Equal(1, await PurchaseRowCountAsync(factory, PurchaseProvider.Apple, providerTransactionId));
    }

    // --- purchase transaction: two writers race the same status change ----------

    [Fact]
    public async Task Two_concurrent_writers_racing_one_purchase_status_change_make_exactly_one_conflict_and_converge()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var purchaseId = await SeedPurchaseAsync(factory, PurchaseProvider.Apple, "txn-race-status", _premiumProduct);

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // Two writers each load the SAME Active purchase, so both capture the same xmin before either commits, then
        // both drive the SAME refund (the duplicate refund/chargeback notification race the reconciliation reasons
        // about) — only the persistence token can stop the second write from a silent second application.
        var first = await contextA.PurchaseTransactions.SingleAsync(transaction => transaction.Id == purchaseId);
        var second = await contextB.PurchaseTransactions.SingleAsync(transaction => transaction.Id == purchaseId);
        Assert.True(first.ChangeStatus(PurchaseTransactionStatus.Refunded, _at));
        Assert.True(second.ChangeStatus(PurchaseTransactionStatus.Refunded, _at));

        if (PostgresTestDatabase.IsConfigured)
        {
            // Truly concurrent: the stale xmin no longer matches for the loser, so exactly one SaveChanges wins and
            // exactly one fails loudly (a DbUpdateConcurrencyException, which UpdateAsync detaches-and-rethrows) —
            // never a silent double-apply.
            var committed = await Task.WhenAll(
                Task.Run(() => AttemptStatusChangeAsync(contextA, first)),
                Task.Run(() => AttemptStatusChangeAsync(contextB, second)));

            Assert.Single(committed, didCommit => didCommit);
            Assert.Single(committed, didCommit => !didCommit);
        }
        else
        {
            // SQLite carries no xmin token, so the interleaving is last-write-wins and does not throw — confirming
            // the Npgsql-only mapping does not break the default test provider.
            await new PurchaseTransactionRepository(contextA).UpdateAsync(first, CancellationToken.None);
            await new PurchaseTransactionRepository(contextB).UpdateAsync(second, CancellationToken.None);
        }

        // Whatever the provider, the status changed EXACTLY ONCE: a fresh reader sees the single committed refund,
        // and the loser re-reading that row converges — a convergent retry of the same change is an idempotent
        // no-op (already Refunded), so no second revocation is ever applied.
        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var settled = await verifyContext.PurchaseTransactions.SingleAsync(transaction => transaction.Id == purchaseId);
        Assert.Equal(PurchaseTransactionStatus.Refunded, settled.Status);
        Assert.False(settled.ChangeStatus(PurchaseTransactionStatus.Refunded, _at));
    }

    // --- money path over HTTP: two buyers race the same receipt -----------------

    [Fact]
    public async Task Two_concurrent_apple_verifications_of_one_receipt_never_double_grant()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _premiumProduct);

        using var clientA = factory.CreateClientFor("buyer-a", _issuer);
        using var clientB = factory.CreateClientFor("buyer-b", _issuer);

        // Two DIFFERENT buyers submit the SAME receipt at the same time. The fake verifier derives the provider
        // transaction id from the proof alone, so both verify to the SAME purchase — a true cross-subject race
        // through the endpoint's explicit record + link + grant transaction (the money path end to end).
        HttpStatusCode? statusA;
        HttpStatusCode? statusB;
        if (PostgresTestDatabase.IsConfigured)
        {
            var results = await Task.WhenAll(
                Task.Run(() => SubmitAppleAsync(clientA, "shared-receipt", _premiumProduct)),
                Task.Run(() => SubmitAppleAsync(clientB, "shared-receipt", _premiumProduct)));
            statusA = results[0];
            statusB = results[1];
        }
        else
        {
            // SQLite shares one connection, so two truly-concurrent HTTP requests cannot run; submit them
            // sequentially (the existing cross-subject path). The no-double-grant invariant is identical; the TRUE
            // race is exercised on the PostgreSQL CI leg.
            statusA = await SubmitAppleAsync(clientA, "shared-receipt", _premiumProduct);
            statusB = await SubmitAppleAsync(clientB, "shared-receipt", _premiumProduct);
        }

        // Exactly one buyer won the receipt and was granted (200 OK). The other was denied — a clean 409 (its
        // link resolved to a cross-subject conflict), or, when the duplicate insert aborted its in-flight
        // transaction on PostgreSQL, a 500 that succeeds on retry (surfaced here as 500 or, if the test host
        // rethrows the unhandled exception, a null status) — but NEVER a second grant.
        var statuses = new[] { statusA, statusB };
        Assert.Single(statuses, status => status == HttpStatusCode.OK);
        Assert.All(statuses, status => Assert.True(
            status is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.InternalServerError or null,
            $"Unexpected status {status?.ToString() ?? "rethrown"} from a concurrent receipt submission."));

        // The crown-jewel invariant: NO double-grant. Exactly one billing link for the receipt, and exactly one
        // buyer's worth of entitlements across BOTH buyers (the winner holds the two mapped grants; the loser
        // holds none — user B never obtained user A's entitlement, threat T5).
        Assert.Equal(1, await BillingLinkCountForReceiptAsync(factory, PurchaseProvider.Apple, "apple-txn-shared-receipt"));
        var grantsA = await BuyerEntitlementCountAsync(factory, "buyer-a");
        var grantsB = await BuyerEntitlementCountAsync(factory, "buyer-b");
        Assert.Equal(2, grantsA + grantsB);
        Assert.Contains(0, new[] { grantsA, grantsB });
    }

    // =====================================================================
    // Race helper.
    // =====================================================================

    /// <summary>
    /// Runs two colliding writers and returns both outcomes. On the PostgreSQL leg they run TRULY concurrently —
    /// each on its own Npgsql connection — so the loser genuinely races the winner's committed unique row and its
    /// <c>DbUpdateException -&gt; re-read -&gt; resolve</c> branch fires under real concurrency. On SQLite (one
    /// shared connection that cannot overlap) they run sequentially; the second insert still collides with the
    /// first's committed row, so the same branch is exercised without the true concurrency.
    /// </summary>
    private static async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        if (PostgresTestDatabase.IsConfigured)
        {
            var results = await Task.WhenAll(Task.Run(first), Task.Run(second)).ConfigureAwait(false);
            return (results[0], results[1]);
        }

        var firstResult = await first().ConfigureAwait(false);
        var secondResult = await second().ConfigureAwait(false);
        return (firstResult, secondResult);
    }

    /// <summary>
    /// Persists a purchase-status change through the real <see cref="PurchaseTransactionRepository.UpdateAsync"/>
    /// and reports whether it committed: <see langword="true"/> when the write won, <see langword="false"/> when
    /// the <c>xmin</c> token rejected it as a concurrent conflict (the loser).
    /// </summary>
    private static async Task<bool> AttemptStatusChangeAsync(LiveCoreDbContext context, PurchaseTransaction transaction)
    {
        try
        {
            await new PurchaseTransactionRepository(context).UpdateAsync(transaction, CancellationToken.None);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>
    /// Submits an Apple receipt over real HTTP and captures the resulting status. A thrown request (the test host
    /// rethrowing an unhandled in-transaction abort rather than mapping it to a 500) is captured as
    /// <see langword="null"/>, exactly as the transactional-atomicity tests do, so a concurrent loser never fails
    /// the test on the transport.
    /// </summary>
    private static async Task<HttpStatusCode?> SubmitAppleAsync(HttpClient client, string proof, string productReference)
    {
        try
        {
            using var response = await client.PostAsync(
                _appleRoute,
                JsonContent.Create(new { transaction = proof, productReference }, options: _json));
            return response.StatusCode;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    private static async Task<Guid> SeedPurchaseAsync(
        WorkspaceApiFactory factory,
        PurchaseProvider provider,
        string providerTransactionId,
        string productReference)
    {
        var purchaseId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var transaction = PurchaseTransaction.Record(
                VerifiedPurchase.Create(provider, providerTransactionId, productReference), TestData.SeedTime);
            context.PurchaseTransactions.Add(transaction);
            await context.SaveChangesAsync();
            purchaseId = transaction.Id;
        });
        return purchaseId;
    }

    private static async Task<EntitlementDefinition> SeedFlagDefinitionAsync(WorkspaceApiFactory factory, string key)
    {
        EntitlementDefinition definition = null!;
        await factory.SeedAsync(async context => definition = await context.AddFlagEntitlementDefinitionAsync(key));
        return definition;
    }

    // Seeds the plan a verified purchase of <paramref name="planKey"/> maps to: two entitlement definitions and a
    // plan (keyed by the product reference) granting the flag (true) and the quota (5), mirroring the grant-chain
    // endpoint tests. The vertical supplies this plan-definition seed data; Core only maps a product reference to
    // it by key (CORE-MON-003).
    private static async Task SeedPremiumPlanAsync(WorkspaceApiFactory factory, string planKey)
    {
        await factory.SeedAsync(async context =>
        {
            var adFree = await context.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            var workspaceLimit = await context.AddQuotaEntitlementDefinitionAsync(_workspaceLimitKey);

            var plan = PlanDefinition.Define(planKey, "Premium", null, TestData.SeedTime);
            plan.GrantFlag(adFree, true);
            plan.GrantQuota(workspaceLimit, 5);
            context.PlanDefinitions.Add(plan);
            await context.SaveChangesAsync();
        });
    }

    // =====================================================================
    // Persisted-state readers (each through a fresh scope, so it round-trips the database).
    // =====================================================================

    private static async Task<int> BillingLinkCountAsync(WorkspaceApiFactory factory, Guid purchaseTransactionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.BillingAccountLinks.AsNoTracking()
            .CountAsync(link => link.PurchaseTransactionId == purchaseTransactionId);
    }

    private static async Task<int> SubjectEntitlementRowCountAsync(
        WorkspaceApiFactory factory,
        Guid subjectId,
        Guid entitlementDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SubjectEntitlements.AsNoTracking()
            .CountAsync(assignment => assignment.SubjectType == EntitlementSubjectType.User
                && assignment.SubjectId == subjectId
                && assignment.EntitlementDefinitionId == entitlementDefinitionId);
    }

    private static async Task<int> PurchaseRowCountAsync(
        WorkspaceApiFactory factory,
        PurchaseProvider provider,
        string providerTransactionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking()
            .CountAsync(transaction => transaction.Provider == provider
                && transaction.ProviderTransactionId == providerTransactionId);
    }

    private static async Task<int> BillingLinkCountForReceiptAsync(
        WorkspaceApiFactory factory,
        PurchaseProvider provider,
        string providerTransactionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var purchase = await context.PurchaseTransactions.AsNoTracking()
            .SingleOrDefaultAsync(transaction => transaction.Provider == provider
                && transaction.ProviderTransactionId == providerTransactionId);
        if (purchase is null)
        {
            return 0;
        }

        return await context.BillingAccountLinks.AsNoTracking()
            .CountAsync(link => link.PurchaseTransactionId == purchase.Id);
    }

    // Counts the subject-entitlement rows for the buyer resolved from the OIDC identity pair. The buyer profile is
    // provisioned by the endpoint before the transaction, so even a denied buyer has a profile (with zero grants).
    private static async Task<int> BuyerEntitlementCountAsync(WorkspaceApiFactory factory, string subject)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var profile = await context.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Issuer == _issuer && p.SubjectId == subject);
        if (profile is null)
        {
            return 0;
        }

        return await context.SubjectEntitlements.AsNoTracking()
            .CountAsync(assignment => assignment.SubjectType == EntitlementSubjectType.User
                && assignment.SubjectId == profile.Id);
    }

    // =====================================================================
    // Fake verifier host (a conforming verifier; never a production verifier).
    // =====================================================================

    /// <summary>A <see cref="WorkspaceApiFactory"/> with a conforming fake Apple verifier substituted.</summary>
    private sealed class FakeAppleVerifierApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<IPurchaseVerificationProvider, FakeAppleVerifier>());
        }
    }

    /// <summary>
    /// A deterministic in-memory Apple verifier that "verifies" any proof and derives the provider transaction id
    /// from the proof ALONE — so the SAME proof from two different buyers verifies to the SAME purchase. It never
    /// contacts a real store API and is NOT a production verifier.
    /// </summary>
    private sealed class FakeAppleVerifier : IPurchaseVerificationProvider
    {
        public PurchaseProvider Provider => PurchaseProvider.Apple;

        public Task<PurchaseVerificationResult> VerifyAsync(
            PurchaseVerificationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var purchase = VerifiedPurchase.Create(
                PurchaseProvider.Apple, $"apple-txn-{request.Proof}", request.ProductReference ?? "product.unknown");
            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }
}
