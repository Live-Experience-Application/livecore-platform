using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the current-user effective-entitlements endpoint (CORE-API-007,
/// <c>GET /api/v1/me/entitlements</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys
/// ON), so the documented request flow (authentication -> endpoint -> authorization ->
/// server-side resolution) is exercised end-to-end.
///
/// Coverage, per the story's required integration + authorization-negative tests (threat T1
/// broken object-level authorization; threat T5 tenant/subject isolation;
/// docs/06_AUTHORIZATION_MATRIX.md):
/// <list type="bullet">
///   <item>POSITIVE: a user with no entitlements gets an empty set (the fail-closed default);
///   a user's granted flag and quota assignments are returned with the generic key + value,
///   ordered by key; a first-seen user is provisioned and gets the empty set.</item>
///   <item>SHAPE / no-leak: the item carries only {key, valueKind, flagValue, quotaLimit};
///   the body carries no subject id or authorization rationale (threat T7).</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a service account has no personal
///   premium state (403); one user's entitlement never leaks to another (per-subject
///   isolation, threat T5).</item>
/// </list>
/// All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class MeEntitlementsEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _route = "/api/v1/me/entitlements";
    private const string _adsDisabledKey = "ads.disabled";
    private const string _workspaceActiveMaxKey = "workspace.active.max";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_service_account_is_403()
    {
        // /me is a user concept: a service account (issuer-asserted client_id marker) has no
        // personal premium state.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_with_no_entitlements_gets_an_empty_set()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        await factory.SeedAsync(async db => await db.AddUserAsync(_issuer, subject));

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEntitlementsAsync(client);

        Assert.Empty(body.Entitlements);
    }

    [Fact]
    public async Task A_user_who_never_made_a_request_is_provisioned_and_gets_an_empty_set()
    {
        // No seeding at all: the endpoint provisions the current user's profile on first sight
        // (idempotent) and resolves the fail-closed default (nothing entitled).
        await using var factory = new WorkspaceApiFactory();

        using var client = factory.CreateClientFor("first-seen-user", _issuer);
        var body = await GetEntitlementsAsync(client);

        Assert.Empty(body.Entitlements);
    }

    [Fact]
    public async Task A_users_flag_and_quota_grants_are_returned_ordered_by_key()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "premium-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);

            var flagDefinition = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            await db.AddSubjectFlagEntitlementAsync(
                EntitlementSubjectType.User, user.Id, flagDefinition, value: true);

            var quotaDefinition = await db.AddQuotaEntitlementDefinitionAsync(_workspaceActiveMaxKey);
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.User, user.Id, quotaDefinition, limit: 5);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEntitlementsAsync(client);

        // Deterministically ordered by the generic key (ordinal): "ads.disabled" < "workspace.active.max".
        Assert.Equal(2, body.Entitlements.Count);
        Assert.Equal(new[] { _adsDisabledKey, _workspaceActiveMaxKey }, body.Entitlements.Select(e => e.Key).ToArray());

        var flag = body.Entitlements[0];
        Assert.Equal(nameof(EntitlementValueKind.Flag), flag.ValueKind);
        Assert.True(flag.FlagValue);
        Assert.Null(flag.QuotaLimit);

        var quota = body.Entitlements[1];
        Assert.Equal(nameof(EntitlementValueKind.Quota), quota.ValueKind);
        Assert.Null(quota.FlagValue);
        Assert.Equal(5, quota.QuotaLimit);
    }

    [Fact]
    public async Task An_unlimited_quota_grant_reports_a_null_limit()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "unlimited-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var quotaDefinition = await db.AddQuotaEntitlementDefinitionAsync(_workspaceActiveMaxKey);
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.User, user.Id, quotaDefinition, limit: null);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEntitlementsAsync(client);

        var quota = Assert.Single(body.Entitlements);
        Assert.Equal(_workspaceActiveMaxKey, quota.Key);
        Assert.Equal(nameof(EntitlementValueKind.Quota), quota.ValueKind);
        Assert.Null(quota.QuotaLimit);
        Assert.Null(quota.FlagValue);
    }

    [Fact]
    public async Task The_response_body_leaks_no_subject_id_or_rationale()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "premium-user";
        Guid userId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            userId = user.Id;
            var flagDefinition = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            await db.AddSubjectFlagEntitlementAsync(
                EntitlementSubjectType.User, user.Id, flagDefinition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var response = await client.GetAsync(_route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        // The subject id is never echoed (per-subject isolation; threat T7), nor any
        // authorization rationale or source-plan provenance.
        Assert.DoesNotContain(userId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan", raw, StringComparison.OrdinalIgnoreCase);

        // The exact item property set is {key, valueKind, flagValue, quotaLimit}.
        using var document = JsonDocument.Parse(raw);
        var item = document.RootElement.GetProperty("entitlements")[0];
        var properties = item.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { "key", "valueKind", "flagValue", "quotaLimit" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Another_users_entitlement_does_not_leak()
    {
        // Per-subject isolation (threat T5): the caller has no grant; a DIFFERENT user holds
        // ads.disabled. The caller's set must stay empty — premium state is never returned
        // through another's id.
        await using var factory = new WorkspaceApiFactory();
        const string caller = "caller-a";
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, caller);
            var other = await db.AddUserAsync(_issuer, "premium-other");
            var definition = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            await db.AddSubjectFlagEntitlementAsync(
                EntitlementSubjectType.User, other.Id, definition, value: true);
        });

        using var client = factory.CreateClientFor(caller, _issuer);
        var body = await GetEntitlementsAsync(client);

        Assert.Empty(body.Entitlements);
    }

    private static async Task<MeEntitlementsDto> GetEntitlementsAsync(HttpClient client)
    {
        var response = await client.GetAsync(_route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeEntitlementsDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private sealed record MeEntitlementsDto(IReadOnlyList<EntitlementItemDto> Entitlements);

    private sealed record EntitlementItemDto(
        string Key,
        string ValueKind,
        bool? FlagValue,
        long? QuotaLimit);
}
