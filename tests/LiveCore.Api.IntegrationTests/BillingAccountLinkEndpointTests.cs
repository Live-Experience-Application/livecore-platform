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
/// HTTP integration tests for the buyer linkage CORE-MON-002 adds to the purchase verification endpoints
/// (<c>POST /api/v1/purchases/apple/transactions</c> and <c>.../google/tokens</c>). They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core
/// SQLite, foreign keys ON) with a conforming fake <see cref="IPurchaseVerificationProvider"/> substituted for the
/// store, so the documented record-then-link flow is exercised end-to-end. The fake derives the provider
/// transaction id from the proof alone, so two DIFFERENT users submitting the SAME proof verify to the SAME
/// purchase (the "same external receipt" the acceptance criterion is about).
///
/// Coverage, per the story's required tests ("a verified purchase records the buyer; user B cannot bind user A's
/// transaction/receipt; foreign-tenant denied") and the threat model's fail-closed requirements (threats T5/T7):
/// <list type="bullet">
///   <item>RECORDS THE BUYER (happy path): an authenticated user's verified purchase persists exactly one
///   <c>billing_account_links</c> row binding the purchase to that user's <c>users(id)</c> subject.</item>
///   <item>CROSS-SUBJECT DENIAL (the crown jewel): once user A's receipt is linked to A, user B submitting the
///   SAME receipt is 409 and the link STILL belongs to A — user B can never bind user A's receipt.</item>
///   <item>IDEMPOTENCY: the same buyer re-submitting their own receipt is 200 with still exactly one link.</item>
///   <item>FOREIGN-IDENTITY DENIAL: the route is global and carries no tenant, so the relevant isolation is
///   per-SUBJECT — the same subject id under a DIFFERENT OIDC issuer is a different user (threat T5) and is denied
///   the same way a foreign tenant would be, claiming nothing.</item>
///   <item>GOOGLE SYMMETRY: the Google endpoint links the buyer and denies a cross-subject claim identically.</item>
/// </list>
/// All fixtures are generic Store/Entitlements vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class BillingAccountLinkEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _appleRoute = "/api/v1/purchases/apple/transactions";
    private const string _googleRoute = "/api/v1/purchases/google/tokens";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // --- Apple: records the buyer ----------------------------------------------

    [Fact]
    public async Task Apple_verification_links_the_verified_purchase_to_the_buyer()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAppleAsync(client, "shared-receipt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one purchase, and exactly one buyer link binding it to buyer A's resolved user subject.
        var transaction = Assert.Single(await TransactionsAsync(factory));
        var link = Assert.Single(await LinksAsync(factory));
        Assert.Equal(transaction.Id, link.PurchaseTransactionId);
        Assert.Equal(EntitlementSubjectType.User, link.SubjectType);
        Assert.Equal(await ProfileIdAsync(factory, "buyer-a", _issuer), link.SubjectId);
    }

    [Fact]
    public async Task Apple_verification_links_the_buyer_idempotently_on_a_replay()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var first = await PostAppleAsync(client, "shared-receipt");
        var second = await PostAppleAsync(client, "shared-receipt");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // The same buyer re-submitting their own receipt links once, not twice.
        Assert.Single(await LinksAsync(factory));
        Assert.Equal(1, await TransactionCountAsync(factory));
    }

    // --- Apple: user B cannot bind user A's receipt ----------------------------

    [Fact]
    public async Task Apple_verification_denies_a_different_subject_claiming_an_already_linked_receipt()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        using var clientA = factory.CreateClientFor("buyer-a", _issuer);
        using var clientB = factory.CreateClientFor("buyer-b", _issuer);

        var aResponse = await PostAppleAsync(clientA, "shared-receipt");
        var bResponse = await PostAppleAsync(clientB, "shared-receipt");

        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        // Fail-closed: the receipt is already A's, so B's claim is denied 409 and nothing is granted to B.
        Assert.Equal(HttpStatusCode.Conflict, bResponse.StatusCode);

        // The one purchase and its one link still belong to A — user B never bound A's receipt.
        Assert.Equal(1, await TransactionCountAsync(factory));
        var link = Assert.Single(await LinksAsync(factory));
        Assert.Equal(await ProfileIdAsync(factory, "buyer-a", _issuer), link.SubjectId);
    }

    // --- Apple: foreign identity (the same subject under a different issuer) ----

    [Fact]
    public async Task Apple_verification_denies_the_same_subject_under_a_different_issuer()
    {
        // The route is global and has no tenant, so the relevant isolation is per-SUBJECT: a subject value is
        // unique only within its issuer, so "buyer-a" under issuer A and "buyer-a" under issuer B are DIFFERENT
        // users (threat T5). The second is denied exactly as a foreign tenant would be.
        await using var factory = new FakeAppleVerifierApiFactory();
        using var realmA = factory.CreateClientFor("buyer-a", "https://issuer-a.test");
        using var realmB = factory.CreateClientFor("buyer-a", "https://issuer-b.test");

        var aResponse = await PostAppleAsync(realmA, "shared-receipt");
        var bResponse = await PostAppleAsync(realmB, "shared-receipt");

        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, bResponse.StatusCode);

        // The link belongs to the issuer-A user; the issuer-B near-identity never claimed it.
        var link = Assert.Single(await LinksAsync(factory));
        Assert.Equal(await ProfileIdAsync(factory, "buyer-a", "https://issuer-a.test"), link.SubjectId);
    }

    // --- Google: symmetry ------------------------------------------------------

    [Fact]
    public async Task Google_verification_links_the_buyer_and_denies_a_cross_subject_claim()
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var clientA = factory.CreateClientFor("buyer-a", _issuer);
        using var clientB = factory.CreateClientFor("buyer-b", _issuer);

        var aResponse = await PostGoogleAsync(clientA, "shared-token");
        var bResponse = await PostGoogleAsync(clientB, "shared-token");

        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, bResponse.StatusCode);

        Assert.Equal(1, await TransactionCountAsync(factory));
        var link = Assert.Single(await LinksAsync(factory));
        Assert.Equal(await ProfileIdAsync(factory, "buyer-a", _issuer), link.SubjectId);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostAppleAsync(HttpClient client, string transaction)
        => await client.PostAsync(_appleRoute, JsonContent.Create(new { transaction, productReference = "product.premium" }, options: _json));

    private static async Task<HttpResponseMessage> PostGoogleAsync(HttpClient client, string purchaseToken)
        => await client.PostAsync(_googleRoute, JsonContent.Create(new { purchaseToken, productReference = "product.premium" }, options: _json));

    private static async Task<int> TransactionCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private static async Task<IReadOnlyList<PurchaseTransaction>> TransactionsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().OrderBy(transaction => transaction.Id).ToListAsync();
    }

    private static async Task<IReadOnlyList<BillingAccountLink>> LinksAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.BillingAccountLinks.AsNoTracking().OrderBy(link => link.Id).ToListAsync();
    }

    // Resolves the persisted user profile id for an OIDC identity pair (the buyer subject id the endpoint links).
    private static async Task<Guid> ProfileIdAsync(WorkspaceApiFactory factory, string subject, string issuer)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var profile = await context.UserProfiles.AsNoTracking()
            .SingleAsync(p => p.Issuer == issuer && p.SubjectId == subject);
        return profile.Id;
    }

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

    /// <summary>A <see cref="WorkspaceApiFactory"/> with a conforming fake Google verifier substituted.</summary>
    private sealed class FakeGoogleVerifierApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<IPurchaseVerificationProvider, FakeGoogleVerifier>());
        }
    }

    /// <summary>
    /// A deterministic in-memory Apple verifier that "verifies" any proof and derives the provider transaction id
    /// from the proof ALONE — so the SAME proof from two different users verifies to the SAME purchase (the "same
    /// external receipt"). It never contacts a real store API and is NOT a production verifier.
    /// </summary>
    private sealed class FakeAppleVerifier : IPurchaseVerificationProvider
    {
        public PurchaseProvider Provider => PurchaseProvider.Apple;

        public Task<PurchaseVerificationResult> VerifyAsync(PurchaseVerificationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var purchase = VerifiedPurchase.Create(
                PurchaseProvider.Apple, $"apple-txn-{request.Proof}", request.ProductReference ?? "product.unknown");
            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }

    /// <summary>The Google analogue of <see cref="FakeAppleVerifier"/>.</summary>
    private sealed class FakeGoogleVerifier : IPurchaseVerificationProvider
    {
        public PurchaseProvider Provider => PurchaseProvider.Google;

        public Task<PurchaseVerificationResult> VerifyAsync(PurchaseVerificationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var purchase = VerifiedPurchase.Create(
                PurchaseProvider.Google, $"google-txn-{request.Proof}", request.ProductReference ?? "product.unknown");
            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }
}
