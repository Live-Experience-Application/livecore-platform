// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the ad-eligibility endpoint (CORE-ADS-001, <c>GET /api/v1/me/ad-eligibility</c>). They
/// drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme +
/// EF Core SQLite, foreign keys ON), so the documented request flow (authentication → endpoint → authorization →
/// server-side decision) is exercised end-to-end.
///
/// Coverage, per the story's required "entitlement-driven ad eligibility tests" and the required NEGATIVE
/// authorization cases (threat T1 broken object-level authorization; threat T5 tenant/subject isolation;
/// docs/06_AUTHORIZATION_MATRIX.md):
/// <list type="bullet">
///   <item>POSITIVE: a user with no entitlements is fail-closed to ads required; a granted <c>ads.disabled</c> turns
///   ads off; a granted <c>ads.required</c> reports the explicit reason; the hosted-session capability is reported
///   independently.</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a service account has no personal premium state (403);
///   one user's ad-free grant never leaks to another user (per-subject isolation).</item>
/// </list>
/// All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class AdEligibilityEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _route = "/api/v1/me/ad-eligibility";

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
        // /me is a user concept: a service account (issuer-asserted client_id marker) has no personal premium state.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_user_with_no_entitlements_is_fail_closed_to_ads_required()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        await factory.SeedAsync(async db => await db.AddUserAsync(_issuer, subject));

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEligibilityAsync(client);

        Assert.True(body.AdsRequired);
        Assert.Equal("NO_AD_FREE_ENTITLEMENT", body.Reason);
        Assert.Null(body.SessionAdFreeUntil);
        Assert.False(body.HostedSessionAdFree);
    }

    [Fact]
    public async Task A_user_who_never_made_a_request_is_provisioned_and_fail_closed()
    {
        // No seeding at all: the endpoint provisions the current user's profile on first sight (idempotent) and the
        // decision is the fail-closed default.
        await using var factory = new WorkspaceApiFactory();

        using var client = factory.CreateClientFor("first-seen-user", _issuer);
        var body = await GetEligibilityAsync(client);

        Assert.True(body.AdsRequired);
        Assert.Equal("NO_AD_FREE_ENTITLEMENT", body.Reason);
    }

    [Fact]
    public async Task A_granted_ads_disabled_turns_ads_off()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "premium-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var definition = await db.AddFlagEntitlementDefinitionAsync(AdEntitlementKeys.AdsDisabled);
            await db.AddSubjectFlagEntitlementAsync(EntitlementSubjectType.User, user.Id, definition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEligibilityAsync(client);

        Assert.False(body.AdsRequired);
        Assert.Equal("AD_FREE_ENTITLEMENT", body.Reason);
        Assert.False(body.HostedSessionAdFree);
    }

    [Fact]
    public async Task A_granted_ads_required_reports_the_explicit_reason()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "free-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var definition = await db.AddFlagEntitlementDefinitionAsync(AdEntitlementKeys.AdsRequired);
            await db.AddSubjectFlagEntitlementAsync(EntitlementSubjectType.User, user.Id, definition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEligibilityAsync(client);

        Assert.True(body.AdsRequired);
        Assert.Equal("ADS_REQUIRED_ENTITLEMENT", body.Reason);
    }

    [Fact]
    public async Task The_hosted_session_capability_is_reported_independently()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "table-pass-host";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var definition = await db.AddFlagEntitlementDefinitionAsync(AdEntitlementKeys.HostedSessionsAdsDisabled);
            await db.AddSubjectFlagEntitlementAsync(EntitlementSubjectType.User, user.Id, definition, value: true);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var body = await GetEligibilityAsync(client);

        // The host capability does not turn the host's OWN ads off.
        Assert.True(body.AdsRequired);
        Assert.Equal("NO_AD_FREE_ENTITLEMENT", body.Reason);
        Assert.True(body.HostedSessionAdFree);
    }

    [Fact]
    public async Task Another_users_ad_free_grant_does_not_leak()
    {
        // Per-subject isolation (threat T5): the caller has no grant; a DIFFERENT user holds ads.disabled. The
        // caller must stay fail-closed to ads required — premium state is never returned through another's id.
        await using var factory = new WorkspaceApiFactory();
        const string caller = "caller-a";
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, caller);
            var other = await db.AddUserAsync(_issuer, "premium-other");
            var definition = await db.AddFlagEntitlementDefinitionAsync(AdEntitlementKeys.AdsDisabled);
            await db.AddSubjectFlagEntitlementAsync(EntitlementSubjectType.User, other.Id, definition, value: true);
        });

        using var client = factory.CreateClientFor(caller, _issuer);
        var body = await GetEligibilityAsync(client);

        Assert.True(body.AdsRequired);
        Assert.Equal("NO_AD_FREE_ENTITLEMENT", body.Reason);
    }

    private static async Task<AdEligibilityDto> GetEligibilityAsync(HttpClient client)
    {
        var response = await client.GetAsync(_route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdEligibilityDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private sealed record AdEligibilityDto(
        bool AdsRequired,
        string Reason,
        DateTimeOffset? SessionAdFreeUntil,
        bool HostedSessionAdFree);
}
