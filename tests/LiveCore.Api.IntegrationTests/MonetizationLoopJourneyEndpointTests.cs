// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// THE MONETIZATION-LOOP COMPOSITION test (CORE-E2E-002, the "End-to-End Scenarios" epic). The phase-3 monetization
/// integration tests are strong slices, but each ARRANGES state by direct DB seeding and then exercises ONE link of
/// the money loop in isolation: <see cref="BillingAccountLinkEndpointTests"/> proves the buyer linkage,
/// <see cref="PurchaseEntitlementGrantEndpointTests"/> the product → plan → entitlement grant,
/// <see cref="MeEntitlementsEndpointTests"/> the effective-entitlements read, <see cref="AdEligibilityEndpointTests"/>
/// the entitlement-driven capability decision, <see cref="QuotaEnforcementEndpointTests"/> the quota gate, and
/// <see cref="StoreNotificationEndpointTests"/> the refund downgrade — but the WHOLE loop composing across the Store,
/// Entitlements and enforcement modules over the public API is never proven end-to-end. This test closes that gap: it
/// drives the full money loop through the WRITE/read endpoints over real HTTP (<see cref="WorkspaceApiFactory"/>: test
/// authentication scheme + EF Core SQLite, foreign keys ON), feeding the verified purchase's grant into the enforced
/// endpoints and the refund's revocation back out of them.
///
/// THE LOOP (the story's required tests): a verified, buyer-linked purchase
/// (<c>POST /api/v1/purchases/apple/transactions</c>, CORE-MON-002/003) grants the mapped entitlements →
/// <c>GET /api/v1/me/entitlements</c> reflects the grant (CORE-MON-009) → the gated capability AND the gated quota
/// action actually change behaviour on their real enforced endpoints (<c>GET /api/v1/me/ad-eligibility</c> turns
/// ad-free, CORE-ADS-001; <c>POST /api/v1/workspaces</c> is admitted by the granted <c>workspace.active.max</c>,
/// CORE-ENTL-004) → a refund store-notification (<c>POST /api/v1/store-notifications/apple</c>, CORE-MON-004) revokes
/// it → <c>GET /api/v1/me/entitlements</c> drops it → the capability locks again and the gated action is denied
/// fail-closed again. It runs after CORE-MON-011/012 so the corrected, revoked-stays-revoked semantics are exercised.
///
/// WHAT IS SUBSTITUTED (never weakening production). Receipt verification stays delegated to the deployment-supplied
/// <see cref="IPurchaseVerificationProvider"/> port (no real provider keys, threat T7): a conforming fake verifier
/// "verifies" the proof and derives the provider transaction id from it, exactly as the phase-3 tests do. The store
/// notification's signature/source validation stays delegated to the deployment-supplied
/// <see cref="IStoreNotificationParser"/> port: a conforming fake parser normalizes a simple
/// <c>notificationId|type|transactionId</c> payload. Every other production seam — the buyer linkage, the grant
/// chain, the monotonic status machine, the entitlement revocation, the ad-eligibility policy and the quota gate —
/// runs unchanged.
///
/// WHAT IS SEEDED DIRECTLY (the minimal bootstrap, and ONLY because the public API has no write route for it): the
/// generic plan-definition catalog a verified purchase of <c>product.premium</c> maps to (the vertical/deployment
/// supplies this seed data; Core only maps a product reference to it by key) — a flag entitlement
/// (<c>ads.disabled</c>), a quota entitlement (<c>workspace.active.max</c>), the quota DEFINITION that makes the
/// workspace-create command actually enforce that quota, and the plan granting both — plus, for the negative
/// authorization test, the in-tenant non-control member and the foreign-tenant owner (there is no add-org-member
/// route beyond the org-create founding-Owner grant). The buyer founds their OWN tenant through the API. All fixtures
/// are generic Store/Entitlements vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class MonetizationLoopJourneyEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlug = "northwind-labs";
    private const string _foreignOrgSlug = "acme-co";

    private const string _buyerSubject = "buyer-a";
    private const string _otherSubject = "buyer-b";

    private const string _appleVerifyRoute = "/api/v1/purchases/apple/transactions";
    private const string _appleNotificationRoute = "/api/v1/store-notifications/apple";
    private const string _entitlementsRoute = "/api/v1/me/entitlements";
    private const string _adEligibilityRoute = "/api/v1/me/ad-eligibility";
    private const string _workspacesRoute = "/api/v1/workspaces";

    // The verified purchase's product reference IS the plan key the deployment seeds (CORE-MON-003 maps product ->
    // plan by the plan's stable key); the plan grants the ad-free flag and the workspace quota.
    private const string _mappedProduct = "product.premium";
    private const string _adsDisabledKey = "ads.disabled";
    private const string _workspaceLimitKey = "workspace.active.max";
    private const long _premiumWorkspaceLimit = 5;

    // The proof the buyer submits. The fake verifier derives the provider transaction id from the proof ALONE, so the
    // refund notification can name the same purchase by its resulting transaction id.
    private const string _sharedReceipt = "shared-receipt";
    private const string _purchaseTransactionId = "apple-txn-shared-receipt";

    // A plausible, past event time for the refund notification: on or after StoreNotification.MinOccurredAt (year
    // 2000) and never beyond the endpoint's future-clock-skew tolerance, so it is accepted regardless of the host
    // clock. The loop asserts the revoke EFFECT, not any timestamp, so a fixed value keeps the test deterministic.
    private static readonly DateTimeOffset _refundOccurredAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // The full money loop composes through the API.
    // =====================================================================

    [Fact]
    public async Task The_full_monetization_loop_composes_through_the_api()
    {
        await using var factory = new MonetizationLoopApiFactory();
        await SeedPremiumPlanWithEnforcedQuotaAsync(factory);

        using var buyer = factory.CreateClientFor(_buyerSubject, _issuer, _orgSlug);
        using var store = factory.CreateAnonymousClient();

        // The buyer founds their OWN tenant through the API (becoming its Owner), so they are authorized to exercise
        // the quota-gated workspace-create command below; the quota itself still decides whether it is admitted.
        await CreateOrganizationAsync(buyer, _orgSlug, "Northwind Labs");

        // BASELINE (free tier, nothing purchased yet). The capability is locked and the gated quota action is
        // fail-closed denied: the workspace.active.max quota is DEFINED but the buyer holds no entitlement granting a
        // limit, so a free user cannot bypass it by simply never purchasing (CORE-ENTL-004).
        Assert.Empty(await GetEntitlementsAsync(buyer));
        Assert.True((await GetAdEligibilityAsync(buyer)).AdsRequired);
        await AssertWorkspaceCreateAsync(buyer, "ws-free", HttpStatusCode.Conflict);

        // PURCHASE: a verified, buyer-linked purchase grants the mapped entitlements (record + link + grant in one
        // transaction, CORE-MON-002/003).
        await VerifyPurchaseAsync(buyer);

        // /me/entitlements REFLECTS THE GRANT: exactly the mapped flag (true) and quota (the premium limit), ordered
        // by key (CORE-MON-009).
        var granted = await GetEntitlementsAsync(buyer);
        Assert.Equal(new[] { _adsDisabledKey, _workspaceLimitKey }, granted.Select(e => e.Key).ToArray());
        Assert.True(granted.Single(e => e.Key == _adsDisabledKey).FlagValue);
        Assert.Equal(_premiumWorkspaceLimit, granted.Single(e => e.Key == _workspaceLimitKey).QuotaLimit);

        // THE GATED CAPABILITY IS UNLOCKED on its real enforced endpoint: the ad-free grant turns ads off
        // server-side (CORE-ADS-001).
        var unlocked = await GetAdEligibilityAsync(buyer);
        Assert.False(unlocked.AdsRequired);
        Assert.Equal("AD_FREE_ENTITLEMENT", unlocked.Reason);

        // THE GATED QUOTA ACTION SUCCEEDS: the granted workspace.active.max now admits the create on the same
        // enforced endpoint that denied it at the free tier (CORE-ENTL-004).
        await AssertWorkspaceCreateAsync(buyer, "ws-premium", HttpStatusCode.Created);

        // REFUND: an authentic refund store-notification for the buyer's purchase revokes the granted entitlement
        // (CORE-MON-004), driving the purchase into the absorbing Refunded state.
        var refund = await PostRefundNotificationAsync(store);
        await EnsureStatusAsync(refund, HttpStatusCode.OK, "refund notification");
        Assert.Equal(nameof(StoreNotificationProcessingOutcome.Applied), await OutcomeAsync(refund));

        // /me/entitlements DROPS IT.
        Assert.Empty(await GetEntitlementsAsync(buyer));

        // THE CAPABILITY IS LOCKED AGAIN: ad-eligibility reverts to the fail-closed ads-required default.
        var relocked = await GetAdEligibilityAsync(buyer);
        Assert.True(relocked.AdsRequired);
        Assert.Equal("NO_AD_FREE_ENTITLEMENT", relocked.Reason);

        // THE GATED QUOTA ACTION IS DENIED AGAIN: with the entitlement revoked the workspace-create command is once
        // more fail-closed (no allowance), proving the loop locked the capability back, not just the ad flag.
        await AssertWorkspaceCreateAsync(buyer, "ws-after-refund", HttpStatusCode.Conflict);
    }

    // =====================================================================
    // NEGATIVE authorization: a purchase and its refund are strictly per-subject
    // (threat T5) — premium never leaks to another subject, and one subject's
    // refund never touches another.
    // =====================================================================

    [Fact]
    public async Task A_purchase_and_its_refund_affect_only_the_buyers_subject()
    {
        await using var factory = new MonetizationLoopApiFactory();
        await SeedPremiumPlanWithEnforcedQuotaAsync(factory);

        // Both are user concepts only: neither needs a tenant for the /me reads, so no organization claim.
        using var buyer = factory.CreateClientFor(_buyerSubject, _issuer);
        using var other = factory.CreateClientFor(_otherSubject, _issuer);
        using var store = factory.CreateAnonymousClient();

        // The buyer purchases premium; a second subject never does.
        await VerifyPurchaseAsync(buyer);

        // The buyer holds the grant and is ad-free; the OTHER subject holds nothing and stays fail-closed —
        // premium state is never returned through another subject's id (per-subject isolation, threat T5).
        Assert.NotEmpty(await GetEntitlementsAsync(buyer));
        Assert.False((await GetAdEligibilityAsync(buyer)).AdsRequired);
        Assert.Empty(await GetEntitlementsAsync(other));
        Assert.True((await GetAdEligibilityAsync(other)).AdsRequired);

        // The buyer's refund revokes the buyer's grant and never touches the other subject (who held nothing).
        var refund = await PostRefundNotificationAsync(store);
        await EnsureStatusAsync(refund, HttpStatusCode.OK, "refund notification");

        Assert.Empty(await GetEntitlementsAsync(buyer));
        Assert.True((await GetAdEligibilityAsync(buyer)).AdsRequired);
        Assert.Empty(await GetEntitlementsAsync(other));
        Assert.True((await GetAdEligibilityAsync(other)).AdsRequired);
    }

    // =====================================================================
    // NEGATIVE authorization: the gated quota action enforces tenant + role
    // authorization BEFORE the quota, fail-closed (threats T1/T5) — a buyer's
    // premium is never a backdoor around the authorization boundary.
    // =====================================================================

    [Fact]
    public async Task The_quota_gated_action_denies_foreign_tenant_and_unauthorized_role_fail_closed()
    {
        await using var factory = new MonetizationLoopApiFactory();
        await SeedPremiumPlanWithEnforcedQuotaAsync(factory);

        using var buyer = factory.CreateClientFor(_buyerSubject, _issuer, _orgSlug);
        await CreateOrganizationAsync(buyer, _orgSlug, "Northwind Labs");

        // The buyer is premium in their tenant (so the gated action would otherwise be admitted for them).
        await VerifyPurchaseAsync(buyer);

        // Seed an in-tenant non-control member (Host) and a foreign-tenant Owner. There is no add-org-member route
        // beyond the org-create founding-Owner grant, so these memberships are the minimal direct bootstrap.
        await factory.SeedAsync(async db =>
        {
            var organization = await db.Organizations.SingleAsync(o => o.Slug == _orgSlug);

            var hostUser = await db.AddUserAsync(_issuer, "host-user");
            await db.AddOrganizationMemberAsync(organization.Id, hostUser.Id, MembershipRole.Host);

            var foreignUser = await db.AddUserAsync(_issuer, "foreign-user");
            var foreignOrg = await db.AddOrganizationAsync(_foreignOrgSlug);
            await db.AddOrganizationMemberAsync(foreignOrg.Id, foreignUser.Id, MembershipRole.Owner);
        });

        // UNAUTHORIZED ROLE (threat T1): an in-tenant Host may not create a workspace (Owner/Admin only). The role
        // denial is 403 at the authorization boundary, BEFORE any quota is consulted — the buyer's premium does not
        // make the gated command reachable by a member who lacks the role.
        using var hostClient = factory.CreateClientFor("host-user", _issuer, _orgSlug);
        var roleDenied = await hostClient.PostAsJsonAsync(
            _workspacesRoute, new { organizationSlug = _orgSlug, slug = "host-ws", name = "Host WS" }, _json);
        Assert.Equal(HttpStatusCode.Forbidden, roleDenied.StatusCode);

        // FOREIGN TENANT (threat T5): a member of another tenant naming the buyer's tenant is denied 403 — premium in
        // the addressed tenant is never reachable by a non-member.
        using var foreignClient = factory.CreateClientFor("foreign-user", _issuer, _orgSlug, _foreignOrgSlug);
        var tenantDenied = await foreignClient.PostAsJsonAsync(
            _workspacesRoute, new { organizationSlug = _orgSlug, slug = "foreign-ws", name = "Foreign WS" }, _json);
        Assert.Equal(HttpStatusCode.Forbidden, tenantDenied.StatusCode);
    }

    // =====================================================================
    // Loop-step helpers (each asserts the real HTTP status where relevant).
    // =====================================================================

    private static async Task CreateOrganizationAsync(HttpClient client, string slug, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { slug, name }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create organization");
        var body = await response.Content.ReadFromJsonAsync<OrganizationDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
    }

    private static async Task VerifyPurchaseAsync(HttpClient client)
    {
        var response = await client.PostAsync(
            _appleVerifyRoute,
            JsonContent.Create(new { transaction = _sharedReceipt, productReference = _mappedProduct }, options: _json));
        await EnsureStatusAsync(response, HttpStatusCode.OK, "verify purchase");
    }

    private static async Task AssertWorkspaceCreateAsync(HttpClient client, string slug, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(
            _workspacesRoute, new { organizationSlug = _orgSlug, slug, name = slug }, _json);
        await EnsureStatusAsync(response, expected, $"create workspace '{slug}'");
    }

    private static async Task<HttpResponseMessage> PostRefundNotificationAsync(HttpClient store)
    {
        // The fake parser reads a "notificationId|type|transactionId" payload; the transaction id names the buyer's
        // purchase so the revocation resolves the buyer link and revokes the mapped entitlements.
        var payload = $"ntf-refund|{StoreNotificationType.Refunded}|{_purchaseTransactionId}";
        return await store.PostAsync(_appleNotificationRoute, new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private static async Task<IReadOnlyList<EntitlementItemDto>> GetEntitlementsAsync(HttpClient client)
    {
        var response = await client.GetAsync(_entitlementsRoute);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "read entitlements");
        var body = await response.Content.ReadFromJsonAsync<MeEntitlementsDto>(_json);
        Assert.NotNull(body);
        return body.Entitlements;
    }

    private static async Task<AdEligibilityDto> GetAdEligibilityAsync(HttpClient client)
    {
        var response = await client.GetAsync(_adEligibilityRoute);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "read ad eligibility");
        var body = await response.Content.ReadFromJsonAsync<AdEligibilityDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<string?> OutcomeAsync(HttpResponseMessage response)
    {
        var ack = await response.Content.ReadFromJsonAsync<AckDto>(_json);
        return ack?.Outcome;
    }

    private static async Task EnsureStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Loop step '{step}' expected {expected} but got {(int)response.StatusCode}. Body: {body}");
        }
    }

    /// <summary>
    /// Seeds the generic plan a verified purchase of <see cref="_mappedProduct"/> maps to: the ad-free flag and the
    /// workspace quota entitlement definitions, the quota DEFINITION that makes the workspace-create command actually
    /// enforce <see cref="_workspaceLimitKey"/> for the creating User subject (without it the command is ungoverned
    /// and always succeeds), and the plan granting the flag (true) and the quota (<see cref="_premiumWorkspaceLimit"/>).
    /// The vertical/deployment supplies this plan-definition seed data; Core only maps a product reference to it by
    /// key (CORE-MON-003).
    /// </summary>
    private static async Task SeedPremiumPlanWithEnforcedQuotaAsync(WorkspaceApiFactory factory)
    {
        await factory.SeedAsync(async db =>
        {
            var adFree = await db.AddFlagEntitlementDefinitionAsync(_adsDisabledKey);
            var workspaceLimit = await db.AddQuotaEntitlementDefinitionAsync(_workspaceLimitKey);

            // The quota definition the workspace-create command measures, keyed on the creating User subject — the
            // pair the grant later assigns a limit for, so enforcement engages on the same enforced endpoint.
            await db.AddQuotaDefinitionAsync(workspaceLimit, EntitlementSubjectType.User);

            var plan = PlanDefinition.Define(_mappedProduct, "Premium", null, TestData.SeedTime);
            plan.GrantFlag(adFree, true);
            plan.GrantQuota(workspaceLimit, _premiumWorkspaceLimit);
            db.PlanDefinitions.Add(plan);
            await db.SaveChangesAsync();
        });
    }

    // =====================================================================
    // Test host: both deployment-supplied ports substituted by conforming fakes
    // (no real provider keys; the fail-closed verify/validate adapters, threat T7).
    // =====================================================================

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> with BOTH the Apple receipt verifier and the Apple store-notification
    /// parser substituted by conforming fakes, so the whole loop — purchase verification AND the refund notification —
    /// runs against one app instance with the production grant/revoke/enforcement seams intact.
    /// </summary>
    private sealed class MonetizationLoopApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IPurchaseVerificationProvider, FakeAppleVerifier>();
                services.AddSingleton<IStoreNotificationParser>(new FakeAppleStoreNotificationParser());
            });
        }
    }

    /// <summary>
    /// A deterministic in-memory Apple verifier that "verifies" any proof and derives the provider transaction id from
    /// the proof ALONE, so the refund notification can name the resulting purchase by its transaction id. It never
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

    /// <summary>
    /// A deterministic in-memory <see cref="IStoreNotificationParser"/> standing in for a real, deployment-supplied
    /// validator. It "validates" any payload, reading a simple <c>notificationId|type|transactionId</c> payload into a
    /// normalized <see cref="StoreNotification"/>. It never contacts a real store and is NOT a production validator.
    /// </summary>
    private sealed class FakeAppleStoreNotificationParser : IStoreNotificationParser
    {
        public PurchaseProvider Provider => PurchaseProvider.Apple;

        public Task<StoreNotificationParseResult> ParseAsync(StoreNotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var parts = envelope.RawPayload.Trim().Split('|');
            var notification = StoreNotification.Create(
                Provider, parts[0], Enum.Parse<StoreNotificationType>(parts[1]), parts[2], _refundOccurredAt);
            return Task.FromResult(StoreNotificationParseResult.Parsed(notification));
        }
    }

    // =====================================================================
    // Response DTOs (only the fields the assertions read).
    // =====================================================================

    private sealed record OrganizationDto(Guid Id, string Slug, string Name);

    private sealed record MeEntitlementsDto(IReadOnlyList<EntitlementItemDto> Entitlements);

    private sealed record EntitlementItemDto(string Key, string ValueKind, bool? FlagValue, long? QuotaLimit);

    private sealed record AdEligibilityDto(
        bool AdsRequired,
        string Reason,
        DateTimeOffset? SessionAdFreeUntil,
        bool HostedSessionAdFree);

    private sealed record AckDto(string Outcome);
}
