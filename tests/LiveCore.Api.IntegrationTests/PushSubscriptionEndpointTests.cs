// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the closed-app Web Push subscription surface (CORE-PUSH-001, the "Closed-App Push
/// Notifications" epic): <c>GET /api/v1/push/vapid-public-key</c>, <c>POST /api/v1/me/push-subscriptions</c> and
/// <c>DELETE /api/v1/me/push-subscriptions/{subscriptionId}</c>. They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (SQLite with foreign keys enforced), so the documented request flow
/// (authentication -> principal mapping -> endpoint -> inline authorization -> per-principal store) AND the
/// users(id) ON DELETE CASCADE are exercised end-to-end.
///
/// The heart of the story, plus the threat model (T1/T5/T7):
/// <list type="bullet">
///   <item>a principal registers and deletes its OWN subscription (and a re-register refreshes the keys in
///   place);</item>
///   <item>a caller can NEVER delete another principal's subscription (hidden 404), and registration requires
///   authentication (401) and a user principal (service account 403);</item>
///   <item>the VAPID public-key route returns the configured key, and the surface is INERT when unconfigured
///   (null key; registration refused 503);</item>
///   <item>the subscription is erased on user erasure (the users CASCADE) and appears in the user-data
///   export.</item>
/// </list>
/// </summary>
public sealed class PushSubscriptionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _endpoint = "https://push.example.test/sub/abc-123";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static string SubscriptionRoute(Guid subscriptionId) => $"/api/v1/me/push-subscriptions/{subscriptionId}";

    private static object RegisterBody(string endpoint = _endpoint, string p256dh = "p256dh-key", string auth = "auth-secret")
        => new { endpoint, p256dh, auth };

    // ---- VAPID public key route: configured vs inert -------------------------

    [Fact]
    public async Task Vapid_public_key_route_returns_the_configured_key()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("caller", _issuer);

        var response = await client.GetAsync("/api/v1/push/vapid-public-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VapidDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(ConfiguredWebPushApiFactory.VapidPublicKey, body!.PublicKey);
    }

    [Fact]
    public async Task Vapid_public_key_route_is_inert_with_no_key_configured()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("caller", _issuer);

        var response = await client.GetAsync("/api/v1/push/vapid-public-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VapidDto>(_json);
        Assert.NotNull(body);
        Assert.Null(body!.PublicKey);
    }

    // ---- 401: missing auth on every route ------------------------------------

    [Fact]
    public async Task Vapid_public_key_without_a_token_is_401()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/push/vapid-public-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_without_a_token_is_401()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/v1/me/push-subscriptions", RegisterBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_without_a_token_is_401()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(SubscriptionRoute(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 201/204: a principal registers and deletes its OWN subscription -----

    [Fact]
    public async Task Principal_registers_and_then_deletes_its_own_subscription()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("caller-a", _issuer);

        var registerResponse = await client.PostAsJsonAsync("/api/v1/me/push-subscriptions", RegisterBody());
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        var registered = await registerResponse.Content.ReadFromJsonAsync<PushSubscriptionDto>(_json);
        Assert.NotNull(registered);
        Assert.NotEqual(Guid.Empty, registered!.Id);
        Assert.Equal(_endpoint, registered.Endpoint);

        // The subscription exists in the store, scoped to the caller's resolved profile.
        await AssertSubscriptionCountAsync(factory, 1);

        var deleteResponse = await client.DeleteAsync(SubscriptionRoute(registered.Id));
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        await AssertSubscriptionCountAsync(factory, 0);
    }

    [Fact]
    public async Task Re_registering_the_same_endpoint_refreshes_the_keys_in_place()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("caller-a", _issuer);

        var first = await client.PostAsJsonAsync(
            "/api/v1/me/push-subscriptions", RegisterBody(p256dh: "p256dh-v1", auth: "auth-v1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<PushSubscriptionDto>(_json);

        var second = await client.PostAsJsonAsync(
            "/api/v1/me/push-subscriptions", RegisterBody(p256dh: "p256dh-v2", auth: "auth-v2"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondDto = await second.Content.ReadFromJsonAsync<PushSubscriptionDto>(_json);

        // Same browser endpoint -> same row (no duplicate), with the rotated keys persisted.
        Assert.Equal(firstDto!.Id, secondDto!.Id);
        await AssertSubscriptionCountAsync(factory, 1);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var stored = await context.PushSubscriptions.AsNoTracking().SingleAsync();
        Assert.Equal("p256dh-v2", stored.P256dh);
        Assert.Equal("auth-v2", stored.Auth);
    }

    // ---- 400: malformed registration body ------------------------------------

    [Fact]
    public async Task Register_with_a_non_absolute_endpoint_is_400()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("caller-a", _issuer);

        var response = await client.PostAsJsonAsync(
            "/api/v1/me/push-subscriptions", RegisterBody(endpoint: "not-a-url"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSubscriptionCountAsync(factory, 0);
    }

    // ---- 503: inert when no VAPID key is configured --------------------------

    [Fact]
    public async Task Register_is_503_when_the_surface_is_inert()
    {
        // The default factory configures no VAPID key, so the surface is inert: no subscription is registrable.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("caller-a", _issuer);

        var response = await client.PostAsJsonAsync("/api/v1/me/push-subscriptions", RegisterBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertSubscriptionCountAsync(factory, 0);
    }

    // ---- 403: a service account is not a user --------------------------------

    [Fact]
    public async Task Register_by_a_service_account_is_403()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("svc", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "machine-client");

        var response = await client.PostAsJsonAsync("/api/v1/me/push-subscriptions", RegisterBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertSubscriptionCountAsync(factory, 0);
    }

    [Fact]
    public async Task Delete_by_a_service_account_is_403()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("svc", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "machine-client");

        var response = await client.DeleteAsync(SubscriptionRoute(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 404 hidden: a caller cannot delete another principal's subscription -

    [Fact]
    public async Task Delete_of_another_principals_subscription_is_a_hidden_404_and_leaves_it_intact()
    {
        await using var factory = new ConfiguredWebPushApiFactory();

        Guid foreignSubscriptionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // Another principal (user B) owns a subscription.
            var victim = await db.AddUserAsync(_issuer, "victim-b");
            var subscription = await db.AddPushSubscriptionAsync(victim.Id, "https://push.example.test/sub/victim");
            foreignSubscriptionId = subscription.Id;
        });

        // The attacker (user A, a different principal) tries to delete user B's subscription by id.
        using var attacker = factory.CreateClientFor("attacker-a", _issuer);
        var response = await attacker.DeleteAsync(SubscriptionRoute(foreignSubscriptionId));

        // It is an indistinguishable hidden 404 (never 403), and B's subscription is left intact (threats T1/T5).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.True(await context.PushSubscriptions.AsNoTracking().AnyAsync(s => s.Id == foreignSubscriptionId));
    }

    [Fact]
    public async Task Delete_of_an_unknown_subscription_is_a_hidden_404()
    {
        await using var factory = new ConfiguredWebPushApiFactory();
        using var client = factory.CreateClientFor("caller-a", _issuer);

        var response = await client.DeleteAsync(SubscriptionRoute(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Erasure: the subscription is removed on user erasure ----------------

    [Fact]
    public async Task Subscription_is_erased_on_user_erasure()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectSubscriptionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync("northwind-labs");
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);

            var subject = await db.AddUserAsync(_issuer, "data-subject-a", "Subject", "subject@example.test");
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
            var subscription = await db.AddPushSubscriptionAsync(subject.Id);
            subjectSubscriptionId = subscription.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, "northwind-labs");
        var response = await admin.DeleteAsync(
            $"/api/v1/organizations/northwind-labs/members/{subjectOrgMembershipId}/personal-data");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The users(id) ON DELETE CASCADE removed the subject's subscription with their profile.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.PushSubscriptions.AsNoTracking().AnyAsync(s => s.Id == subjectSubscriptionId));
    }

    // ---- Export: the subscription appears in the user-data export ------------

    [Fact]
    public async Task Subscription_appears_in_the_user_data_export()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subjectSubject = "subject-self";
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectSubscriptionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync("northwind-labs");
            var subject = await db.AddUserAsync(_issuer, subjectSubject, "Subject", "subject@example.test");
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
            var subscription = await db.AddPushSubscriptionAsync(
                subject.Id, "https://push.example.test/sub/exported");
            subjectSubscriptionId = subscription.Id;
        });

        // The subject themselves obtains their own export (self-service).
        using var client = factory.CreateClientFor(subjectSubject, _issuer, "northwind-labs");
        var response = await client.GetAsync(
            $"/api/v1/organizations/northwind-labs/members/{subjectOrgMembershipId}/personal-data-export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await response.Content.ReadFromJsonAsync<ExportDto>(_json);
        Assert.NotNull(export);
        var subscription = Assert.Single(export!.PushSubscriptions);
        Assert.Equal(subjectSubscriptionId, subscription.Id);
        Assert.Equal("https://push.example.test/sub/exported", subscription.Endpoint);

        // The auth encryption secret and the p256dh key are never projected into the export (threat T7).
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var raw = document.RootElement.GetRawText();
        Assert.DoesNotContain("auth", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("p256dh", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertSubscriptionCountAsync(WorkspaceApiFactory factory, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Equal(expected, await context.PushSubscriptions.AsNoTracking().CountAsync());
    }

    private sealed record VapidDto(string? PublicKey);

    private sealed record PushSubscriptionDto(Guid Id, string Endpoint, DateTimeOffset CreatedAt);

    private sealed record ExportDto(PushSubscriptionDto[] PushSubscriptions);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that additionally configures a deployment VAPID public key, so the push
    /// surface is CONFIGURED (not inert): the public-key route returns the key and registration is allowed. The
    /// default factory leaves the key unset, which exercises the inert path.
    /// </summary>
    private sealed class ConfiguredWebPushApiFactory : WorkspaceApiFactory
    {
        public const string VapidPublicKey = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            base.ConfigureWebHost(builder);
            builder.UseSetting("WebPush:Vapid:PublicKey", VapidPublicKey);
        }
    }
}
