// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveCore.Api.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="VapidWebPushSender"/> (CORE-PUSH-002) — the default OUTBOUND Web Push sender. Driven
/// through a stub <see cref="HttpMessageHandler"/> so the VAPID signing and the response mapping are exercised
/// without a real push service. They pin: the request is a PAYLOAD-LESS POST to the subscription endpoint with a
/// valid, verifiable VAPID (RFC 8292) Authorization JWT and a TTL header; a 2xx maps to Delivered; a 404/410 maps to
/// Gone (the stale-subscription cleanup signal); any other status or a transport failure maps to Failed (best-effort).
/// The VAPID JWT signature is VERIFIED end-to-end against the public key, so the BCL ES256 crypto is proven correct.
/// Generic transport vocabulary only (AGENTS.md).
/// </summary>
public sealed class VapidWebPushSenderTests
{
    private const string _endpoint = "https://push.example.test/sub/abc123";

    private static readonly WebPushSignal _signal = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task SendAsync_posts_a_payloadless_request_with_a_verifiable_vapid_jwt_and_ttl()
    {
        var (publicKey, privateKey, verifier) = GenerateVapidKeyPair();
        using var disposeVerifier = verifier;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var sender = CreateSender(handler, publicKey, privateKey);

        var result = await sender.SendAsync(
            new WebPushDispatch(_endpoint, "p256dh", "auth", _signal), CancellationToken.None);

        Assert.Equal(WebPushSendOutcome.Delivered, result.Outcome);

        // It is a POST to the exact endpoint, with no payload (a content-free signal).
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(_endpoint, handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(0, handler.LastRequestContentLength);

        // RFC 8030 requires a TTL.
        Assert.True(handler.LastRequest.Headers.TryGetValues("TTL", out _));

        // The VAPID Authorization header carries a verifiable ES256 JWT (RFC 8292): "vapid t=<jwt>, k=<publicKey>".
        var authorization = handler.LastRequest.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("vapid t=", authorization, StringComparison.Ordinal);
        Assert.Contains(", k=" + publicKey, authorization, StringComparison.Ordinal);

        var token = authorization["vapid t=".Length..authorization.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        // The signature verifies against the public key — proof the BCL ES256 signing is correct.
        var signingInput = Encoding.ASCII.GetBytes(string.Concat(parts[0], ".", parts[1]));
        var signature = WebPushVapid.Base64UrlDecode(parts[2]);
        Assert.True(verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256));

        // The claims name the push-service origin (aud), the configured subject (sub) and a future expiry (exp).
        using var payload = JsonDocument.Parse(WebPushVapid.Base64UrlDecode(parts[1]));
        Assert.Equal("https://push.example.test", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@example.test", payload.RootElement.GetProperty("sub").GetString());
        Assert.True(payload.RootElement.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task SendAsync_maps_a_success_status_to_delivered(HttpStatusCode statusCode)
    {
        var result = await SendWithStatusAsync(statusCode);
        Assert.Equal(WebPushSendOutcome.Delivered, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task SendAsync_maps_404_and_410_to_gone(HttpStatusCode statusCode)
    {
        // A gone subscription: the dispatch deletes it (the stale-subscription cleanup criterion).
        var result = await SendWithStatusAsync(statusCode);
        Assert.Equal(WebPushSendOutcome.Gone, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SendAsync_maps_any_other_status_to_failed(HttpStatusCode statusCode)
    {
        // Best-effort: a non-2xx, non-gone status is a dropped send (the client recovers on replay).
        var result = await SendWithStatusAsync(statusCode);
        Assert.Equal(WebPushSendOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_maps_a_transport_failure_to_failed_without_throwing()
    {
        var (publicKey, privateKey, verifier) = GenerateVapidKeyPair();
        using var disposeVerifier = verifier;
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var sender = CreateSender(handler, publicKey, privateKey);

        var result = await sender.SendAsync(
            new WebPushDispatch(_endpoint, "p256dh", "auth", _signal), CancellationToken.None);

        Assert.Equal(WebPushSendOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_propagates_a_genuine_cancellation()
    {
        var (publicKey, privateKey, verifier) = GenerateVapidKeyPair();
        using var disposeVerifier = verifier;
        var handler = new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var sender = CreateSender(handler, publicKey, privateKey);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(new WebPushDispatch(_endpoint, "p256dh", "auth", _signal), cancellation.Token));
    }

    private static async Task<WebPushSendResult> SendWithStatusAsync(HttpStatusCode statusCode)
    {
        var (publicKey, privateKey, verifier) = GenerateVapidKeyPair();
        using var disposeVerifier = verifier;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        var sender = CreateSender(handler, publicKey, privateKey);
        return await sender.SendAsync(new WebPushDispatch(_endpoint, "p256dh", "auth", _signal), CancellationToken.None);
    }

    private static VapidWebPushSender CreateSender(HttpMessageHandler handler, string publicKey, string privateKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebPush:Delivery:Enabled"] = "true",
            ["WebPush:Vapid:PublicKey"] = publicKey,
            ["WebPush:Vapid:PrivateKey"] = privateKey,
            ["WebPush:Vapid:Subject"] = "mailto:ops@example.test",
        }).Build();
        var options = WebPushDeliveryOptions.FromConfiguration(configuration);

        return new VapidWebPushSender(
            new HttpClient(handler),
            options,
            TimeProvider.System,
            NullLogger<VapidWebPushSender>.Instance);
    }

    /// <summary>
    /// Generates a fresh P-256 VAPID key pair, returning the base64url public point (0x04||X||Y) and private scalar
    /// the sender is configured with, plus an <see cref="ECDsa"/> that can VERIFY the signatures it produces.
    /// </summary>
    private static (string PublicKey, string PrivateKey, ECDsa Verifier) GenerateVapidKeyPair()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        var point = new byte[65];
        point[0] = 0x04;
        LeftPad(parameters.Q.X!, 32).CopyTo(point, 1);
        LeftPad(parameters.Q.Y!, 32).CopyTo(point, 33);

        return (
            WebPushVapid.Base64UrlEncode(point),
            WebPushVapid.Base64UrlEncode(LeftPad(parameters.D!, 32)),
            ecdsa);
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public long? LastRequestContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestContentLength = request.Content is null
                ? null
                : (await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).LongLength;
            return await _responder(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
