// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the realtime session hub's AUTHENTICATION (CORE-RT-001,
/// <c>/hubs/session</c>). SignalR exposes a <c>POST {hub}/negotiate</c> endpoint to establish a
/// connection; because the hub is <c>[Authorize]</c> and mapped with <c>RequireAuthorization()</c>, that
/// endpoint must challenge an unauthenticated client with 401 and admit an authenticated one — exactly
/// the gate the REST endpoints apply (docs/02_ARCHITECTURE.md request flow; docs/05_MODULE_CONTRACTS.md:
/// the Realtime module may not send unfiltered events, so the connection itself must be authenticated
/// first). These drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme), so the negotiate gate is exercised end-to-end without a real identity
/// provider.
///
/// The negotiate endpoint enforces only authentication; it does NOT run the hub's
/// <c>OnConnectedAsync</c> (that runs when the transport connection is established). The connect-time
/// authorization — resolving the caller's session context and joining the SERVER-MANAGED groups, or
/// aborting fail-closed (CORE-RT-002) — is covered at the unit level by RealtimeConnectionResolverTests;
/// its end-to-end, over-the-wire coverage arrives with event delivery (CORE-RT-003), where group
/// membership becomes observable through a received event.
/// </summary>
public sealed class RealtimeHubAuthenticationTests
{
    private const string _issuer = "https://issuer.test";
    private const string _negotiatePath = "/hubs/session/negotiate?negotiateVersion=1";

    [Fact]
    public async Task Hub_negotiate_is_401_without_authentication()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(_negotiatePath, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Hub_negotiate_succeeds_for_an_authenticated_caller()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("host-a", _issuer);

        var response = await client.PostAsync(_negotiatePath, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A real SignalR negotiate response carries a connection id, confirming the hub accepted the
        // authenticated caller (not just any 200).
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_negotiate_with_an_unknown_method_is_not_anonymously_reachable()
    {
        // A GET to the hub path (no SignalR handshake) must still not be served anonymously — the
        // endpoint requires authorization, so an anonymous request is challenged (401), never 200.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/hubs/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
