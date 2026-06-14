using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the Google purchase token verification endpoint (CORE-STORE-004,
/// <c>POST /api/v1/purchases/google/tokens</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented receipt-verification flow (authentication → authorization → provider verification → persistence)
/// is exercised end-to-end. The happy-path/rejection tests substitute a conforming fake
/// <see cref="IPurchaseVerificationProvider"/> for Google (the deployment supplies the real, credential-bearing
/// verifier — docs/13_SELF_HOSTING_REQUIREMENTS.md); one test runs against the unmodified app to prove the
/// verifier-unconfigured fail-closed path. This is the Google mirror of
/// <see cref="ApplePurchaseVerificationEndpointTests"/>.
///
/// Coverage, per the story's required tests ("Contract and negative verification tests") and the threat model's
/// fail-closed requirements (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md; threats T1/T7):
/// <list type="bullet">
///   <item>CONTRACT (happy path): an authenticated user submits a genuine token → 200 with the normalized,
///   provider-neutral purchase identity (provider/transaction id/product) at status <c>Active</c>, and EXACTLY
///   one <c>purchase_transactions</c> row plus one initial <c>purchase_events</c> audit entry are persisted.</item>
///   <item>IDEMPOTENCY (replay): re-submitting the same genuine token → 200 again, with still exactly one
///   transaction and one audit event (a replayed-but-genuine token grants nothing twice; CORE-MON-008).</item>
///   <item>NEGATIVE VERIFICATION (the crown jewel): a forged/unverifiable token → 422 and NOTHING is recorded —
///   no entitlement can be granted off an unverified token ("Never trust client-side premium flags").</item>
///   <item>REPLAY REJECTION (CORE-MON-008): a token the store reports as already consumed/redeemed → 422 and
///   NOTHING is recorded (the adapter's provider-side replay defence).</item>
///   <item>SANDBOX/PRODUCTION SEPARATION (CORE-MON-008): a sandbox receipt on a PRODUCTION deployment → 422 and
///   NOTHING is recorded ("a sandbox receipt is not honored in production"); a sandbox receipt on a non-production
///   deployment IS honored, and a production receipt on a production deployment IS honored (the gate neither
///   over-rejects nor leaks).</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a service account has no personal purchase (403),
///   recording nothing. There is no tenant on this route — a purchase is named globally (CORE-STORE-002), so
///   there is no foreign-tenant case to test here.</item>
///   <item>VALIDATION: a null body and a missing/blank purchase token are 400, recording nothing.</item>
///   <item>FAIL-CLOSED VERIFIER: with no Google adapter configured (the production default), an authorized request
///   is 503 and NOTHING is recorded — Core never grants premium state without a real verification behind it.</item>
/// </list>
/// Every denial/validation case asserts NO transaction was recorded, so a wrong-status pass that also mutated
/// state is caught. All fixtures are generic Store vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class GooglePurchaseVerificationEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _route = "/api/v1/purchases/google/tokens";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Google_verification_unauthenticated_is_401()
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_for_a_service_account_is_403_and_records_nothing()
    {
        // Submitting a purchase is a per-user action: a service account (issuer-asserted client_id marker) has no
        // personal purchase, so it is denied fail-closed before any verification or persistence.
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_is_400_for_a_null_body()
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await client.PostAsync(_route, JsonContent.Create<object?>(null, options: _json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Google_verification_is_400_for_a_missing_or_blank_token(string? purchaseToken)
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body(purchaseToken, "product.premium"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_records_a_verified_purchase_and_an_initial_audit_event()
    {
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VerificationDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(nameof(PurchaseProvider.Google), body.Provider);
        Assert.False(string.IsNullOrWhiteSpace(body.ProviderTransactionId));
        Assert.Equal("product.premium", body.ProductReference);
        Assert.Equal(nameof(PurchaseTransactionStatus.Active), body.Status);

        // Exactly one verified purchase persisted (Active, Google), with the verified identifiers — never the token.
        var transaction = Assert.Single(await TransactionsAsync(factory));
        Assert.Equal(PurchaseProvider.Google, transaction.Provider);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
        Assert.Equal(body.ProviderTransactionId, transaction.ProviderTransactionId);
        Assert.Equal("product.premium", transaction.ProductReference);

        // Exactly one initial audit event (no previous status -> Active), so the purchase lifecycle is auditable.
        var auditEvent = Assert.Single(await EventsAsync(factory));
        Assert.Equal(transaction.Id, auditEvent.PurchaseTransactionId);
        Assert.Null(auditEvent.PreviousStatus);
        Assert.Equal(PurchaseTransactionStatus.Active, auditEvent.NewStatus);
    }

    [Fact]
    public async Task Google_verification_is_idempotent_for_a_replayed_token()
    {
        // A genuine token submitted twice (a client retry / replay) records exactly one transaction and one audit
        // event: the unique (provider, provider_transaction_id) index makes recording idempotent.
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var first = await PostAsync(client, Body("genuine-token", "product.premium"));
        var second = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await TransactionCountAsync(factory));
        Assert.Equal(1, await EventCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_is_422_for_a_rejected_token_and_records_nothing()
    {
        // The crown-jewel negative: a forged/unverifiable token is rejected by the provider, so the request is 422
        // and NOTHING is recorded — no entitlement can ever be granted off an unverified token.
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body(FakeGoogleVerifier.ForgedToken, "product.premium"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
        Assert.Equal(0, await EventCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_is_503_when_no_verifier_is_configured_and_records_nothing()
    {
        // No fake adapter: the unmodified app keeps the production fail-closed resolver (no Google adapter), so
        // verification cannot run and the request is 503 — Core grants nothing without a real verifier.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_is_422_for_a_replayed_token_the_store_already_consumed_and_records_nothing()
    {
        // CORE-MON-008 replay rejection: a token the store reports as already consumed/redeemed is rejected by the
        // adapter, so the request is 422 and NOTHING is recorded — a replayed receipt grants nothing.
        await using var factory = new FakeGoogleVerifierApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body(FakeGoogleVerifier.ReplayedToken, "product.premium"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
        Assert.Equal(0, await EventCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_rejects_a_sandbox_receipt_on_a_production_deployment_and_records_nothing()
    {
        // CORE-MON-008 headline: "a sandbox receipt is not honored in production." The adapter verifies a genuine
        // token but reports the SANDBOX environment; on a production deployment the fail-closed
        // PurchaseEnvironmentPolicy rejects it (422) and NOTHING is recorded or granted — a free test-harness
        // receipt can never unlock premium in production.
        await using var factory = new FakeGoogleVerifierApiFactory(
            environment: PurchaseEnvironment.Sandbox,
            productionDeployment: true);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
        Assert.Equal(0, await EventCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_honors_a_sandbox_receipt_on_a_non_production_deployment()
    {
        // The gate does not over-reject: a non-production deployment (the default test host) honors a sandbox
        // receipt, so a developer can verify sandbox purchases end-to-end.
        await using var factory = new FakeGoogleVerifierApiFactory(
            environment: PurchaseEnvironment.Sandbox,
            productionDeployment: false);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Google_verification_honors_a_production_receipt_on_a_production_deployment()
    {
        // The gate does not leak: a production receipt on a production deployment is honored and recorded.
        await using var factory = new FakeGoogleVerifierApiFactory(
            environment: PurchaseEnvironment.Production,
            productionDeployment: true);
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await PostAsync(client, Body("genuine-token", "product.premium"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TransactionCountAsync(factory));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? purchaseToken, string? productReference)
        => new { purchaseToken, productReference };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
        => await client.PostAsync(_route, JsonContent.Create(body, options: _json));

    private static async Task<int> TransactionCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private static async Task<int> EventCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseEvents.AsNoTracking().CountAsync();
    }

    private static async Task<IReadOnlyList<PurchaseTransaction>> TransactionsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().OrderBy(transaction => transaction.Id).ToListAsync();
    }

    private static async Task<IReadOnlyList<PurchaseEvent>> EventsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseEvents.AsNoTracking().OrderBy(purchaseEvent => purchaseEvent.Id).ToListAsync();
    }

    private sealed record VerificationDto(
        string Provider,
        string ProviderTransactionId,
        string ProductReference,
        string Status);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that registers a conforming fake Google
    /// <see cref="IPurchaseVerificationProvider"/>, so the verify-then-record happy path can run without a real,
    /// deployment-supplied verifier. The production fail-closed resolver picks the fake up because it resolves
    /// adapters from the registered <see cref="IPurchaseVerificationProvider"/> set; every other production
    /// behavior (auth, the recording service, persistence) runs unchanged.
    ///
    /// The fake reports the configured <paramref name="environment"/> (default <see cref="PurchaseEnvironment.Production"/>,
    /// so the happy-path/idempotency/forged tests are honored on any deployment). When
    /// <paramref name="productionDeployment"/> is set it also overrides the registered
    /// <see cref="PurchaseEnvironmentPolicy"/> with the production posture, so the sandbox/production gate
    /// (CORE-MON-008) can be exercised in the endpoint without flipping the host environment.
    /// </summary>
    private sealed class FakeGoogleVerifierApiFactory(
        PurchaseEnvironment environment = PurchaseEnvironment.Production,
        bool productionDeployment = false) : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IPurchaseVerificationProvider>(new FakeGoogleVerifier(environment));

                if (productionDeployment)
                {
                    // Last registration wins for GetService<T>, so this overrides Program.cs's host-derived policy.
                    services.AddSingleton(PurchaseEnvironmentPolicy.ForProductionDeployment());
                }
            });
        }
    }

    /// <summary>
    /// A deterministic in-memory Google <see cref="IPurchaseVerificationProvider"/> standing in for a real,
    /// deployment-supplied verifier. It "verifies" any token except the sentinels <see cref="ForgedToken"/>
    /// (forged/unverifiable) and <see cref="ReplayedToken"/> (already consumed at the store), which it rejects, and
    /// derives a stable provider transaction id from the token so a replayed-but-genuine token verifies to the
    /// same purchase (exercising idempotency). It reports the environment it was constructed with (CORE-MON-008).
    /// It never contacts a real store API and is NOT a production verifier.
    /// </summary>
    private sealed class FakeGoogleVerifier(PurchaseEnvironment environment) : IPurchaseVerificationProvider
    {
        public const string ForgedToken = "forged-token";
        public const string ReplayedToken = "replayed-token";

        public PurchaseProvider Provider => PurchaseProvider.Google;

        public Task<PurchaseVerificationResult> VerifyAsync(
            PurchaseVerificationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.Equals(request.Proof, ForgedToken, StringComparison.Ordinal))
            {
                return Task.FromResult(PurchaseVerificationResult.Rejected("The purchase token could not be verified."));
            }

            if (string.Equals(request.Proof, ReplayedToken, StringComparison.Ordinal))
            {
                return Task.FromResult(PurchaseVerificationResult.Rejected("The purchase token was already redeemed."));
            }

            var purchase = VerifiedPurchase.Create(
                PurchaseProvider.Google,
                $"google-txn-{request.Proof}",
                request.ProductReference ?? "product.unknown",
                environment);

            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }
}
