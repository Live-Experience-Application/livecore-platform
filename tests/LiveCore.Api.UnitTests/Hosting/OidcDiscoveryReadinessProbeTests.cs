// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="OidcDiscoveryReadinessProbe"/> (CORE-OBS-009) — the live reachability probe for the
/// OIDC provider's discovery endpoint. Driven through a stub <see cref="HttpMessageHandler"/> so the success,
/// non-success and transport-failure paths are exercised without a real provider. They pin: a reachable provider
/// (a success status at the well-known discovery URL) completes the probe; a non-success status or a transport
/// failure THROWS (so the health check fails closed); and the probe honors cancellation (the readiness timeout).
/// Generic, product-neutral vocabulary only (AGENTS.md).
/// </summary>
public class OidcDiscoveryReadinessProbeTests
{
    private const string _authority = "https://issuer.example.test";

    [Fact]
    public void DependencyName_is_generic_and_carries_no_secret()
        => Assert.Equal("oidc-discovery", new OidcDiscoveryReadinessProbe(_authority, new HttpClient()).DependencyName);

    [Theory]
    [InlineData("https://issuer.example.test")]
    [InlineData("https://issuer.example.test/")]
    public async Task ProbeAsync_requests_the_well_known_discovery_document(string authority)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK)));
        using var probe = new OidcDiscoveryReadinessProbe(authority, new HttpClient(handler));

        await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(
            "https://issuer.example.test/.well-known/openid-configuration",
            handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ProbeAsync_completes_when_the_provider_answers_with_success()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(Respond(HttpStatusCode.OK)));
        using var probe = new OidcDiscoveryReadinessProbe(_authority, new HttpClient(handler));

        // Reachable: a success status means the provider answered. No exception => the health check stays Ready.
        await probe.ProbeAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProbeAsync_throws_on_a_non_success_status(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(Respond(statusCode)));
        using var probe = new OidcDiscoveryReadinessProbe(_authority, new HttpClient(handler));

        // The provider is up but the discovery URL is wrong/broken: that is still not-ready (fail-closed).
        await Assert.ThrowsAsync<HttpRequestException>(() => probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_throws_on_a_transport_failure()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var probe = new OidcDiscoveryReadinessProbe(_authority, new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_honors_cancellation()
    {
        var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Respond(HttpStatusCode.OK);
        });
        using var probe = new OidcDiscoveryReadinessProbe(_authority, new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(cancellation.Token));
    }

    private static HttpResponseMessage Respond(HttpStatusCode statusCode) => new(statusCode);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return _responder(request, cancellationToken);
        }
    }
}
