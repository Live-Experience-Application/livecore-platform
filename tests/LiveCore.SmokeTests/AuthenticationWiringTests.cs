// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// Authentication-wiring tests (CORE-WS-003) over the real production
/// <c>Program</c> with no test seams: no OIDC Authority and no database
/// connection string are configured, exactly the default smoke-test host.
///
/// They pin the fail-closed default scheme: when no identity provider is
/// configured a protected endpoint must be challenged with 401, never granted
/// anonymously and never crash with a 500 from a missing default challenge
/// scheme (threats T1/T7 in docs/07_SECURITY_THREAT_MODEL.md). This guards the
/// realistic misconfiguration where the host is deployed before its
/// <c>Authentication:Oidc:*</c> settings are in place.
/// </summary>
public class AuthenticationWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthenticationWiringTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/v1/workspaces?organizationSlug=northwind-labs")]
    [InlineData("/api/v1/workspaces/00000000-0000-0000-0000-000000000000?organizationSlug=northwind-labs")]
    public async Task Protected_route_without_authentication_is_401_when_no_identity_provider_is_configured(
        string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        // 401 (fail-closed challenge), not 500 (missing default scheme) and not
        // 200 (anonymous access).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_health_endpoint_stays_reachable_without_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        // The fail-closed default scheme must not bleed onto the anonymous health
        // endpoints, which are mapped outside the authenticated route group.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
