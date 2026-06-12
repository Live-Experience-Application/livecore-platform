using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the store notification endpoints (CORE-STORE-005,
/// <c>POST /api/v1/store-notifications/apple</c> and <c>POST /api/v1/store-notifications/google/rtdn</c>). They
/// drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (EF Core SQLite, foreign
/// keys ON), so the documented flow (unauthenticated callback → signature/source validation by the
/// deployment-supplied adapter → idempotent dedup → purchase lifecycle change) is exercised end-to-end. The
/// happy-path/rejection tests substitute a conforming fake <see cref="IStoreNotificationParser"/> for both
/// providers (the deployment supplies the real, credential-bearing validator); one test runs against the
/// unmodified app to prove the parser-unconfigured fail-closed path.
///
/// Coverage, per the story's required tests ("Duplicate notification and downgrade tests") and the fail-closed
/// security requirements (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md; threats T1/T7):
/// <list type="bullet">
///   <item>UNAUTHENTICATED BY DESIGN: an anonymous caller (no token) is NOT challenged 401 — a store callback
///   carries no OIDC token (csv/mobile_store_api_routes.csv: <c>auth_required=false</c>).</item>
///   <item>FAIL-CLOSED VALIDATOR (the security negative): with no parser configured (the production default) an
///   inbound notification is 503 and NOTHING is recorded — an unvalidated payload never changes a purchase.</item>
///   <item>FORGED/UNPARSEABLE (the security negative): a payload the adapter rejects is 400 and records nothing,
///   changing no purchase.</item>
///   <item>DOWNGRADE: an authentic refund notification downgrades a seeded Active purchase to Refunded, returns
///   200, and records exactly one ledger row.</item>
///   <item>DUPLICATE (idempotency): re-delivering the same notification returns 200 again with still one ledger
///   row and no second downgrade — "Store notifications must be idempotent".</item>
///   <item>IGNORED: an authentic but non-actionable notification is acknowledged 200 and records nothing.</item>
///   <item>UNKNOWN PURCHASE: a notification for a purchase Core never recorded is acknowledged 200 (not found),
///   fabricates no purchase, yet is recorded so it is auditable.</item>
///   <item>GOOGLE ROUTE: the Google RTDN route is wired and provider-routed.</item>
/// </list>
/// All fixtures are generic Store vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class StoreNotificationEndpointTests
{
    private const string _appleRoute = "/api/v1/store-notifications/apple";
    private const string _googleRoute = "/api/v1/store-notifications/google/rtdn";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Notification_endpoint_is_unauthenticated_and_does_not_challenge_an_anonymous_caller()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.Payload("ntf-1", StoreNotificationType.Renewed, "txn-1"));

        // A store callback carries no token; the route must not 401 an anonymous caller (it is AllowAnonymous).
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Apple_notification_with_no_parser_configured_is_503_and_records_nothing()
    {
        // No fake adapter: the unmodified app keeps the production fail-closed resolver, so an unauthenticated
        // payload cannot be validated and the request is 503 — nothing changes a purchase without a real validator.
        await using var factory = new WorkspaceApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.Payload("ntf-1", StoreNotificationType.Refunded, "txn-1"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await NotificationCountAsync(factory));
        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync(factory, PurchaseProvider.Apple, "txn-1"));
    }

    [Fact]
    public async Task Apple_notification_with_a_forged_payload_is_400_and_records_nothing()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.ForgedPayload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await NotificationCountAsync(factory));
        // The purchase is untouched by a payload that could not be validated.
        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync(factory, PurchaseProvider.Apple, "txn-1"));
    }

    [Fact]
    public async Task Apple_refund_notification_downgrades_the_purchase_and_is_acknowledged()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.Payload("ntf-1", StoreNotificationType.Refunded, "txn-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.Applied), await OutcomeAsync(response));
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(factory, PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(1, await NotificationCountAsync(factory));
    }

    [Fact]
    public async Task A_duplicate_notification_is_acknowledged_without_double_applying()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();
        var payload = FakeStoreNotificationParser.Payload("ntf-dup", StoreNotificationType.Refunded, "txn-1");

        var first = await PostAsync(client, _appleRoute, payload);
        var second = await PostAsync(client, _appleRoute, payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.Applied), await OutcomeAsync(first));
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.AlreadyProcessed), await OutcomeAsync(second));
        // Still exactly one ledger row, and the purchase is downgraded exactly once.
        Assert.Equal(1, await NotificationCountAsync(factory));
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(factory, PurchaseProvider.Apple, "txn-1"));
    }

    [Fact]
    public async Task An_ignored_notification_is_acknowledged_and_records_nothing()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Apple, "txn-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.IgnorePayload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(StoreNotificationParseStatus.Ignored), await OutcomeAsync(response));
        Assert.Equal(0, await NotificationCountAsync(factory));
        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync(factory, PurchaseProvider.Apple, "txn-1"));
    }

    [Fact]
    public async Task A_notification_for_an_unknown_purchase_is_acknowledged_as_not_found_and_recorded()
    {
        await using var factory = new FakeParserApiFactory();
        // No purchase seeded for txn-missing.
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _appleRoute, FakeStoreNotificationParser.Payload("ntf-missing", StoreNotificationType.Refunded, "txn-missing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.TransactionNotFound), await OutcomeAsync(response));
        // Nothing fabricated, but the notification's arrival is recorded so it is auditable and not reprocessed.
        Assert.Equal(0, await TransactionCountAsync(factory));
        Assert.Equal(1, await NotificationCountAsync(factory));
    }

    [Fact]
    public async Task Google_rtdn_notification_downgrades_the_purchase()
    {
        await using var factory = new FakeParserApiFactory();
        await SeedActivePurchaseAsync(factory, PurchaseProvider.Google, "order-1");
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, _googleRoute, FakeStoreNotificationParser.Payload("ntf-g1", StoreNotificationType.Cancelled, "order-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.Applied), await OutcomeAsync(response));
        Assert.Equal(PurchaseTransactionStatus.Cancelled, await StatusAsync(factory, PurchaseProvider.Google, "order-1"));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string payload)
        => await client.PostAsync(route, new StringContent(payload, Encoding.UTF8, "application/json"));

    private static async Task<string?> OutcomeAsync(HttpResponseMessage response)
    {
        var ack = await response.Content.ReadFromJsonAsync<AckDto>(_json);
        return ack?.Outcome;
    }

    private static async Task SeedActivePurchaseAsync(WorkspaceApiFactory factory, PurchaseProvider provider, string transactionId)
        => await factory.SeedAsync(async context =>
        {
            context.PurchaseTransactions.Add(PurchaseTransaction.Record(
                VerifiedPurchase.Create(provider, transactionId, "product.premium"),
                new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero)));
            await context.SaveChangesAsync();
        });

    private static async Task<int> NotificationCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.StoreNotificationEvents.AsNoTracking().CountAsync();
    }

    private static async Task<int> TransactionCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private static async Task<PurchaseTransactionStatus> StatusAsync(
        WorkspaceApiFactory factory, PurchaseProvider provider, string transactionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var transaction = await context.PurchaseTransactions.AsNoTracking()
            .SingleAsync(t => t.Provider == provider && t.ProviderTransactionId == transactionId);
        return transaction.Status;
    }

    private sealed record AckDto(string Outcome);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that registers a conforming fake <see cref="IStoreNotificationParser"/>
    /// for both providers, so the validate-then-handle flow can run without a real, deployment-supplied validator.
    /// The production fail-closed resolver picks the fakes up because it resolves adapters from the registered
    /// parser set; every other production behavior (the handler, persistence) runs unchanged.
    /// </summary>
    private sealed class FakeParserApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStoreNotificationParser>(new FakeStoreNotificationParser(PurchaseProvider.Apple));
                services.AddSingleton<IStoreNotificationParser>(new FakeStoreNotificationParser(PurchaseProvider.Google));
            });
        }
    }

    /// <summary>
    /// A deterministic in-memory <see cref="IStoreNotificationParser"/> standing in for a real, deployment-supplied
    /// validator. It "validates" any payload except the sentinel <see cref="ForgedPayload"/> (which it rejects),
    /// treats the sentinel <see cref="IgnorePayload"/> as an authentic-but-non-actionable notification, and
    /// otherwise reads a simple <c>notificationId|type|transactionId</c> payload into a normalized
    /// <see cref="StoreNotification"/>. It never contacts a real store and is NOT a production validator.
    /// </summary>
    private sealed class FakeStoreNotificationParser : IStoreNotificationParser
    {
        public const string ForgedPayload = "forged";
        public const string IgnorePayload = "ignore";

        private static readonly DateTimeOffset _occurredAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        public FakeStoreNotificationParser(PurchaseProvider provider) => Provider = provider;

        public PurchaseProvider Provider { get; }

        public static string Payload(string notificationId, StoreNotificationType type, string transactionId)
            => $"{notificationId}|{type}|{transactionId}";

        public Task<StoreNotificationParseResult> ParseAsync(StoreNotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            var payload = envelope.RawPayload.Trim();
            if (string.Equals(payload, ForgedPayload, StringComparison.Ordinal))
            {
                return Task.FromResult(StoreNotificationParseResult.Rejected("The store notification could not be validated."));
            }

            if (string.Equals(payload, IgnorePayload, StringComparison.Ordinal))
            {
                return Task.FromResult(StoreNotificationParseResult.Ignored());
            }

            var parts = payload.Split('|');
            var notification = StoreNotification.Create(
                Provider,
                parts[0],
                Enum.Parse<StoreNotificationType>(parts[1]),
                parts[2],
                _occurredAt);
            return Task.FromResult(StoreNotificationParseResult.Parsed(notification));
        }
    }
}
