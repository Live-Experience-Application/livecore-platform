using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the in-process mobile API gateway (CORE-MON-009). The store/entitlement routes
/// are documented in their mobile-facing shape under a bare <c>/v1</c> prefix
/// (<c>csv/mobile_store_api_routes.csv</c>; docs/21; docs/22) but mounted under <c>/api/v1</c>; the gateway
/// rewrites a documented mobile <c>/v1</c> path to its <c>/api/v1</c> endpoint BEFORE routing, so a mobile
/// client following the documented path reaches the implemented endpoint with no 404. These tests drive the
/// real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF
/// Core SQLite), so the rewrite is exercised end-to-end together with the unchanged authentication, the
/// fail-closed authorization and the server-side <see cref="SubjectEntitlementResolver"/> resolution.
///
/// Coverage, per the story's required tests (authenticated read returns the caller's effective entitlements;
/// anonymous 401; the documented mobile path reaches the endpoint with no 404) plus the negative-authorization
/// and gateway-scope (no over-exposure) properties (threats T1/T5):
/// <list type="bullet">
///   <item>ENTITLEMENTS (the headline mobile read): the documented <c>GET /v1/me/entitlements</c> reaches the
///   endpoint — an authenticated caller gets 200 with their effective entitlements (byte-for-byte the same
///   body the <c>/api/v1</c> path returns), an anonymous caller is 401, and a service account is 403.</item>
///   <item>PURCHASE: the documented <c>POST /v1/purchases/apple/transactions</c> reaches the endpoint — an
///   anonymous caller is 401 (the route matched; not a 404) and an authorized caller with no verifier
///   configured is 503 (it reached the handler), recording nothing.</item>
///   <item>SCOPED GATEWAY (no over-exposure): a non-documented <c>/v1</c> path (e.g. <c>/v1/workspaces</c>,
///   <c>/v1/organizations</c>) is NOT aliased and still 404s, so the gateway never exposes the rest of the
///   Core API under a second prefix.</item>
/// </list>
/// All fixtures are generic Core/Store vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class MobileApiGatewayEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _mobileEntitlementsRoute = "/v1/me/entitlements";
    private const string _coreEntitlementsRoute = "/api/v1/me/entitlements";
    private const string _mobileAppleTransactionsRoute = "/v1/purchases/apple/transactions";
    private const string _adsDisabledKey = "ads.disabled";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Mobile_entitlements_path_for_an_anonymous_caller_is_401_not_404()
    {
        // The documented mobile path reaches the endpoint: an anonymous caller is challenged with 401 (the route
        // matched and RequireAuthorization ran). An unmounted path would instead fall through to 404.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(_mobileEntitlementsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mobile_entitlements_path_for_a_service_account_is_403()
    {
        // /me is a user concept (the same fail-closed rule the /api/v1 endpoint applies); the rewrite does not
        // widen authorization.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync(_mobileEntitlementsRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mobile_entitlements_path_returns_the_callers_effective_entitlements()
    {
        // Authenticated read returns the caller's effective entitlements over the documented mobile path,
        // produced entirely by the server-side SubjectEntitlementResolver (no client input).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "premium-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var flagDefinition = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            await db.AddSubjectFlagEntitlementAsync(
                EntitlementSubjectType.User, user.Id, flagDefinition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var response = await client.GetAsync(_mobileEntitlementsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeEntitlementsDto>(_json);
        Assert.NotNull(body);
        var item = Assert.Single(body.Entitlements);
        Assert.Equal(_adsDisabledKey, item.Key);
        Assert.Equal(nameof(EntitlementValueKind.Flag), item.ValueKind);
        Assert.True(item.FlagValue);
    }

    [Fact]
    public async Task Mobile_entitlements_path_resolves_to_the_same_endpoint_as_the_core_path()
    {
        // The mobile /v1 path and the Core /api/v1 path are the SAME implemented endpoint: the same caller gets
        // an identical body from both. A first-seen caller is provisioned and resolves the fail-closed default.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "parity-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var flagDefinition = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            await db.AddSubjectFlagEntitlementAsync(
                EntitlementSubjectType.User, user.Id, flagDefinition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var mobile = await client.GetAsync(_mobileEntitlementsRoute);
        var core = await client.GetAsync(_coreEntitlementsRoute);

        Assert.Equal(HttpStatusCode.OK, mobile.StatusCode);
        Assert.Equal(HttpStatusCode.OK, core.StatusCode);
        Assert.Equal(
            await core.Content.ReadAsStringAsync(),
            await mobile.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mobile_apple_transactions_path_for_an_anonymous_caller_is_401_not_404()
    {
        // The documented mobile purchase path reaches the endpoint: anonymous is 401 (route matched), not 404.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            _mobileAppleTransactionsRoute,
            JsonContent.Create(new { transaction = "genuine-proof", productReference = "product.premium" }, options: _json));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task Mobile_apple_transactions_path_reaches_the_handler_and_fails_closed_without_a_verifier()
    {
        // An authorized caller reaches the implemented handler (not a 404): with no Apple verifier configured
        // (the production default) the verify gate fails closed with 503 and records nothing — proving the
        // documented mobile path resolved to the real endpoint, which then applied its own fail-closed contract.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);

        var response = await client.PostAsync(
            _mobileAppleTransactionsRoute,
            JsonContent.Create(new { transaction = "genuine-proof", productReference = "product.premium" }, options: _json));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await TransactionCountAsync(factory));
    }

    [Theory]
    [InlineData("/v1/workspaces")]
    [InlineData("/v1/organizations")]
    public async Task A_non_documented_mobile_path_is_not_aliased_and_is_404(string path)
    {
        // The gateway is scoped to the documented mobile routes only: an authenticated caller's request to a
        // non-documented /v1 path is left untouched and 404s, so the rest of the API is never exposed under /v1.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("user-a", _issuer);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<int> TransactionCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private sealed record MeEntitlementsDto(IReadOnlyList<EntitlementItemDto> Entitlements);

    private sealed record EntitlementItemDto(
        string Key,
        string ValueKind,
        bool? FlagValue,
        long? QuotaLimit);
}
