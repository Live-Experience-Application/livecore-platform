// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the baseline HTTP security response headers (CORE-SEC-009). They drive the
/// REAL application over real HTTP (<see cref="WorkspaceApiFactory"/>) and assert the acceptance criteria:
/// <list type="bullet">
///   <item>an authenticated JSON response AND a Problem Details error response both carry
///   <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c> and the deny-all CSP;</item>
///   <item>the headers ride on the error paths too — an unmatched-route <c>404</c>, an induced <c>500</c>
///   (whose Problem Details body is written after the pipeline <c>Response.Clear()</c>s), and — through the
///   real <see cref="SecurityHeadersMiddleware"/> over a minimal pipeline — a <c>404</c>/<c>406</c>/<c>500</c>;</item>
///   <item>toggling one header off via configuration removes EXACTLY that header and leaves the others, and
///   disabling the feature removes all three;</item>
///   <item>no PII in any header: every value is the exact static directive, never the request's tenant slug
///   or subject (threat T7).</item>
/// </list>
///
/// The headers are a coarse browser-facing defense layered on the OIDC/tenant authorization every endpoint
/// already enforces; they never widen it. All fixtures are generic Core vocabulary (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class SecurityHeadersEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    // ---- authenticated JSON response carries the baseline -------------------

    [Fact]
    public async Task An_authenticated_json_response_carries_nosniff_referrer_policy_and_the_csp()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    // ---- Problem Details error response carries the baseline ----------------

    [Fact]
    public async Task A_problem_details_error_response_carries_nosniff_referrer_policy_and_the_csp()
    {
        // An owner requesting an unknown workspace id is hidden as a 404 application/problem+json — a genuine
        // Problem Details error path. It must still carry the baseline headers.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertBaselineHeaders(response);
    }

    // ---- SignalR-negotiate response carries the baseline --------------------

    [Fact]
    public async Task The_signalr_negotiate_response_carries_the_baseline()
    {
        // The hub requires authorization, so an anonymous negotiate is a 401 — but the security-headers
        // middleware runs at the top of the pipeline (before authentication), so the baseline rides on the
        // SignalR-negotiate response too, exactly as the acceptance criterion requires.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        using var response = await client.PostAsync("/hubs/session/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    // ---- error status codes still carry the baseline ------------------------

    [Fact]
    public async Task An_unmatched_route_404_still_carries_the_baseline()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("owner-a", _issuer, _orgA);

        using var response = await client.GetAsync("/api/v1/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task An_induced_500_still_carries_the_baseline()
    {
        // A faulty dependency makes a normal authenticated request reach an unhandled exception, so the global
        // exception handler writes its Problem Details 500 AFTER Response.Clear(). Because the headers are applied
        // from an OnStarting callback (not inline), they survive the clear and ride on the final 500 — the key
        // correctness property of the middleware.
        await using var factory = new FaultyDependencyApiFactory();
        using var client = factory.CreateClientFor("owner-a", _issuer, _orgA);

        using var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status406NotAcceptable)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task The_real_middleware_applies_the_baseline_on_every_status_code(int statusCode)
    {
        // The API renders no HTML and does no content negotiation, so a genuine 406 is not producible from a real
        // endpoint. This exercises the REAL SecurityHeadersMiddleware (with the production default settings) over
        // a minimal TestServer pipeline whose terminal endpoint returns the requested status, proving the
        // OnStarting baseline rides on 404/406/500 alike regardless of how the status was produced.
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder().Build());

        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services => services.AddSingleton(settings))
                .Configure(app =>
                {
                    app.UseMiddleware<SecurityHeadersMiddleware>();
                    app.Run(context =>
                    {
                        context.Response.StatusCode = statusCode;
                        return Task.CompletedTask;
                    });
                }))
            .StartAsync();

        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(statusCode, (int)response.StatusCode);
        AssertBaselineHeaders(response);
    }

    // ---- individually configurable: toggling a header off -------------------

    [Fact]
    public async Task Toggling_the_csp_off_via_config_removes_exactly_that_header()
    {
        await using var factory = new ConfiguredSecurityHeadersApiFactory(new Dictionary<string, string?>
        {
            ["SecurityHeaders:ContentSecurityPolicy:Enabled"] = "false",
        });
        await SeedOwnerAsync(factory, "owner-a");

        using var client = factory.CreateClientFor("owner-a", _issuer, _orgA);
        using var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly the CSP is gone; the other two are untouched.
        Assert.False(
            response.Headers.Contains(SecurityHeadersConfiguration.ContentSecurityPolicyHeaderName),
            "Disabling the CSP must remove exactly that header.");
        Assert.Equal(
            SecurityHeadersConfiguration.DefaultContentTypeOptions,
            HeaderValue(response, SecurityHeadersConfiguration.ContentTypeOptionsHeaderName));
        Assert.Equal(
            SecurityHeadersConfiguration.DefaultReferrerPolicy,
            HeaderValue(response, SecurityHeadersConfiguration.ReferrerPolicyHeaderName));
    }

    [Fact]
    public async Task Disabling_the_feature_removes_all_three_headers()
    {
        await using var factory = new ConfiguredSecurityHeadersApiFactory(new Dictionary<string, string?>
        {
            ["SecurityHeaders:Enabled"] = "false",
        });
        await SeedOwnerAsync(factory, "owner-a");

        using var client = factory.CreateClientFor("owner-a", _issuer, _orgA);
        using var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(SecurityHeadersConfiguration.ContentTypeOptionsHeaderName));
        Assert.False(response.Headers.Contains(SecurityHeadersConfiguration.ReferrerPolicyHeaderName));
        Assert.False(response.Headers.Contains(SecurityHeadersConfiguration.ContentSecurityPolicyHeaderName));
    }

    // ---- T7: no PII in any header -------------------------------------------

    [Fact]
    public async Task No_header_value_carries_any_tenant_or_principal_detail()
    {
        // Drive an authenticated request that carries a distinctive tenant slug and subject, then assert each
        // header value is EXACTLY the static directive — so nothing request-derived (the slug, the subject, the
        // issuer) ever leaks into a header (threat T7).
        const string subject = "principal-subject-marker";
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exact static directives — no request value spliced in.
        AssertBaselineHeaders(response);

        foreach (var name in new[]
                 {
                     SecurityHeadersConfiguration.ContentTypeOptionsHeaderName,
                     SecurityHeadersConfiguration.ReferrerPolicyHeaderName,
                     SecurityHeadersConfiguration.ContentSecurityPolicyHeaderName,
                 })
        {
            var value = HeaderValue(response, name);
            Assert.DoesNotContain(_orgA, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(subject, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_issuer, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static void AssertBaselineHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            SecurityHeadersConfiguration.DefaultContentTypeOptions,
            HeaderValue(response, SecurityHeadersConfiguration.ContentTypeOptionsHeaderName));
        Assert.Equal(
            SecurityHeadersConfiguration.DefaultReferrerPolicy,
            HeaderValue(response, SecurityHeadersConfiguration.ReferrerPolicyHeaderName));
        Assert.Equal(
            SecurityHeadersConfiguration.DefaultContentSecurityPolicy,
            HeaderValue(response, SecurityHeadersConfiguration.ContentSecurityPolicyHeaderName));
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static Task SeedOwnerAsync(WorkspaceApiFactory factory, string subject)
        => factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that layers in-memory configuration overrides on top of the production
    /// configuration, so a test can flip a <c>SecurityHeaders:*</c> toggle and observe the resulting headers over
    /// real HTTP. Added AFTER the base wiring so the overrides win.
    /// </summary>
    private sealed class ConfiguredSecurityHeadersApiFactory : WorkspaceApiFactory
    {
        private readonly Dictionary<string, string?> _overrides;

        public ConfiguredSecurityHeadersApiFactory(Dictionary<string, string?> overrides) => _overrides = overrides;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_overrides));
        }
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a faulty <see cref="IOrganizationRepository"/> for the
    /// production one, so a normal authenticated <c>GET /api/v1/me</c> reaches an unhandled exception and the
    /// global handler writes a Problem Details 500 after clearing the response — exercising that the OnStarting
    /// baseline survives the clear.
    /// </summary>
    private sealed class FaultyDependencyApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrganizationRepository>();
                services.AddScoped<IOrganizationRepository>(_ => new FaultyOrganizationRepository());
            });
        }
    }

    private sealed class FaultyOrganizationRepository : IOrganizationRepository
    {
        private static InvalidOperationException Fault() => new("Induced fault for the security-headers 500 test.");

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => throw Fault();

        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) => throw Fault();

        public Task<IReadOnlyList<Organization>> ListByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw Fault();

        public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw Fault();

        public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
            => throw Fault();

        public Task<OrganizationAddResult> AddWithOwnerAsync(
            Organization organization,
            OrganizationMember owner,
            CancellationToken cancellationToken)
            => throw Fault();

        public Task DeleteAsync(Organization organization, CancellationToken cancellationToken) => throw Fault();
    }
}
