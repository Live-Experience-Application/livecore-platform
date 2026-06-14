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
/// HTTP integration tests for the purchase-to-entitlement grant chain CORE-MON-003 wires into the purchase
/// verification endpoints (<c>POST /api/v1/purchases/apple/transactions</c> and <c>.../google/tokens</c>). They
/// drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme
/// + EF Core SQLite, foreign keys ON) with a conforming fake <see cref="IPurchaseVerificationProvider"/>, then
/// assert the grant through the SAME read the product uses — <c>GET /api/v1/me/entitlements</c> — so the full
/// product -> plan -> entitlement chain is exercised end to end. The fake derives the provider transaction id
/// from the proof alone, so two DIFFERENT users submitting the SAME proof verify to the SAME purchase (the "same
/// external receipt" the cross-subject denial is about).
///
/// Coverage, per the story's required tests ("a verified purchase grants exactly the mapped entitlement to the
/// buyer; a duplicate webhook/retry does not double-grant; an unverified/failed purchase grants nothing
/// (fail-closed)") plus the threat model's fail-closed isolation (threats T1/T5):
/// <list type="bullet">
///   <item>GRANTS THE MAPPED ENTITLEMENT (happy path): a verified purchase of a product mapped to a plan grants
///   the buyer exactly that plan's entitlements, visible in /me/entitlements.</item>
///   <item>IDEMPOTENT: a duplicate submission (retry) grants once, not twice.</item>
///   <item>FAIL-CLOSED — REJECTED: an unverified/rejected purchase grants nothing and records nothing.</item>
///   <item>FAIL-CLOSED — UNMAPPED PRODUCT: a verified purchase of a product mapped to no plan records + links but
///   grants nothing (no premium for an ungoverned product).</item>
///   <item>CROSS-SUBJECT DENIAL (negative authorization, threat T5): once user A's receipt is linked + granted,
///   user B submitting the SAME receipt is 409 and gets NOTHING — B can never obtain A's entitlement.</item>
///   <item>GOOGLE SYMMETRY: the Google endpoint grants the mapped entitlement identically.</item>
/// </list>
/// All fixtures are generic Store/Entitlements vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class PurchaseEntitlementGrantEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _appleRoute = "/api/v1/purchases/apple/transactions";
    private const string _googleRoute = "/api/v1/purchases/google/tokens";
    private const string _entitlementsRoute = "/api/v1/me/entitlements";

    // The verified purchase's product reference IS the plan key the deployment/vertical seeds (CORE-MON-003 maps
    // product -> plan by the plan's stable key). "product.premium" is a valid generic plan-key shape.
    private const string _mappedProduct = "product.premium";
    private const string _adsDisabledKey = "ads.disabled";
    private const string _workspaceLimitKey = "workspace.active.max";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // --- Apple: grants exactly the mapped entitlement --------------------------

    [Fact]
    public async Task Apple_verified_purchase_grants_exactly_the_mapped_entitlements_to_the_buyer()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAppleAsync(client, "shared-receipt", _mappedProduct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The grant shows up in the effective-entitlements read — exactly the two mapped entitlements.
        var entitlements = await GetEntitlementsAsync(client);
        Assert.Equal(
            new[] { _adsDisabledKey, _workspaceLimitKey },
            entitlements.Select(e => e.Key).ToArray());
        Assert.True(entitlements.Single(e => e.Key == _adsDisabledKey).FlagValue);
        Assert.Equal(5, entitlements.Single(e => e.Key == _workspaceLimitKey).QuotaLimit);

        // Exactly two subject-entitlement rows for the buyer.
        Assert.Equal(2, await SubjectEntitlementCountAsync(factory, "buyer-a", _issuer));
    }

    [Fact]
    public async Task Apple_duplicate_submission_grants_once_not_twice()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var first = await PostAppleAsync(client, "shared-receipt", _mappedProduct);
        var second = await PostAppleAsync(client, "shared-receipt", _mappedProduct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Idempotent on (purchase, entitlement): still exactly two rows, not four.
        Assert.Equal(2, await SubjectEntitlementCountAsync(factory, "buyer-a", _issuer));
        Assert.Equal(2, (await GetEntitlementsAsync(client)).Count);
    }

    // --- Apple: fail-closed — rejected / unmapped ------------------------------

    [Fact]
    public async Task Apple_rejected_purchase_grants_nothing()
    {
        await using var factory = new RejectingAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAppleAsync(client, "forged-receipt", _mappedProduct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // Nothing recorded and nothing granted — fail-closed.
        Assert.Equal(0, await TransactionCountAsync(factory));
        Assert.Empty(await GetEntitlementsAsync(client));
    }

    [Fact]
    public async Task Apple_verified_purchase_of_an_unmapped_product_grants_nothing()
    {
        // The product is verified and linked, but maps to NO plan, so it grants nothing (no premium for an
        // ungoverned product). The purchase is still recorded as the auditable source of truth.
        await using var factory = new FakeAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAppleAsync(client, "shared-receipt", "product.unmapped");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await TransactionCountAsync(factory));
        Assert.Empty(await GetEntitlementsAsync(client));
    }

    // --- Apple: cross-subject denial (negative authorization, threat T5) -------

    [Fact]
    public async Task Apple_a_different_subject_claiming_an_already_granted_receipt_gets_nothing()
    {
        await using var factory = new FakeAppleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var clientA = factory.CreateClientFor("buyer-a", _issuer);
        using var clientB = factory.CreateClientFor("buyer-b", _issuer);

        var aResponse = await PostAppleAsync(clientA, "shared-receipt", _mappedProduct);
        var bResponse = await PostAppleAsync(clientB, "shared-receipt", _mappedProduct);

        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        // Fail-closed: the receipt is already A's, so B is denied 409 and granted nothing.
        Assert.Equal(HttpStatusCode.Conflict, bResponse.StatusCode);

        // A holds the mapped entitlements; B holds nothing — B never obtained A's entitlement.
        Assert.Equal(2, (await GetEntitlementsAsync(clientA)).Count);
        Assert.Empty(await GetEntitlementsAsync(clientB));
        Assert.Equal(0, await SubjectEntitlementCountAsync(factory, "buyer-b", _issuer));
    }

    // --- Google: symmetry ------------------------------------------------------

    [Fact]
    public async Task Google_verified_purchase_grants_exactly_the_mapped_entitlements_to_the_buyer()
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        await SeedPremiumPlanAsync(factory, _mappedProduct);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostGoogleAsync(client, "shared-token", _mappedProduct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entitlements = await GetEntitlementsAsync(client);
        Assert.Equal(
            new[] { _adsDisabledKey, _workspaceLimitKey },
            entitlements.Select(e => e.Key).ToArray());
        Assert.True(entitlements.Single(e => e.Key == _adsDisabledKey).FlagValue);
        Assert.Equal(5, entitlements.Single(e => e.Key == _workspaceLimitKey).QuotaLimit);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostAppleAsync(HttpClient client, string transaction, string productReference)
        => await client.PostAsync(_appleRoute, JsonContent.Create(new { transaction, productReference }, options: _json));

    private static async Task<HttpResponseMessage> PostGoogleAsync(HttpClient client, string purchaseToken, string productReference)
        => await client.PostAsync(_googleRoute, JsonContent.Create(new { purchaseToken, productReference }, options: _json));

    private static async Task<IReadOnlyList<EntitlementItemDto>> GetEntitlementsAsync(HttpClient client)
    {
        var response = await client.GetAsync(_entitlementsRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeEntitlementsDto>(_json);
        Assert.NotNull(body);
        return body.Entitlements;
    }

    // Seeds the plan a verified purchase of <paramref name="planKey"/> maps to: two entitlement definitions and a
    // plan (keyed by the product reference) granting the flag (true) and the quota (5). The vertical supplies this
    // plan-definition seed data; Core only maps a product reference to it by key (CORE-MON-003).
    private static async Task SeedPremiumPlanAsync(WorkspaceApiFactory factory, string planKey)
    {
        await factory.SeedAsync(async db =>
        {
            var adFree = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            var workspaceLimit = await db.AddQuotaEntitlementDefinitionAsync(_workspaceLimitKey);

            var plan = PlanDefinition.Define(planKey, "Premium", null, TestData.SeedTime);
            plan.GrantFlag(adFree, true);
            plan.GrantQuota(workspaceLimit, 5);
            db.PlanDefinitions.Add(plan);
            await db.SaveChangesAsync();
        });
    }

    private static async Task<int> TransactionCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    // Counts the subject-entitlement rows for the user resolved from the OIDC identity pair (the buyer subject).
    private static async Task<int> SubjectEntitlementCountAsync(WorkspaceApiFactory factory, string subject, string issuer)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var profile = await context.UserProfiles.AsNoTracking()
            .SingleAsync(p => p.Issuer == issuer && p.SubjectId == subject);
        return await context.SubjectEntitlements.AsNoTracking()
            .CountAsync(e => e.SubjectType == EntitlementSubjectType.User && e.SubjectId == profile.Id);
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

    /// <summary>A <see cref="WorkspaceApiFactory"/> whose Apple verifier REJECTS every proof (the failed-purchase path).</summary>
    private sealed class RejectingAppleVerifierApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<IPurchaseVerificationProvider, RejectingAppleVerifier>());
        }
    }

    /// <summary>
    /// A deterministic in-memory Apple verifier that "verifies" any proof and derives the provider transaction id
    /// from the proof ALONE — so the SAME proof from two different users verifies to the SAME purchase. It never
    /// contacts a real store API and is NOT a production verifier.
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

    /// <summary>An Apple verifier that rejects every proof (forged / replayed / unverifiable), granting nothing.</summary>
    private sealed class RejectingAppleVerifier : IPurchaseVerificationProvider
    {
        public PurchaseProvider Provider => PurchaseProvider.Apple;

        public Task<PurchaseVerificationResult> VerifyAsync(PurchaseVerificationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(PurchaseVerificationResult.Rejected("invalid or unverifiable proof"));
        }
    }

    private sealed record MeEntitlementsDto(IReadOnlyList<EntitlementItemDto> Entitlements);

    private sealed record EntitlementItemDto(
        string Key,
        string ValueKind,
        bool? FlagValue,
        long? QuotaLimit);
}
