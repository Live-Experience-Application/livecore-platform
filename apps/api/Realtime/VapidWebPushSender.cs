// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The default OUTBOUND Web Push sender (CORE-PUSH-002, <see cref="IWebPushSender"/>): it POSTs a VAPID-signed
/// (RFC 8292), PAYLOAD-LESS push to the subscription's push-service endpoint (RFC 8030). It is the concrete
/// realization of "reuse the worker for the outbound send", living behind the <see cref="IWebPushSender"/> seam so
/// the dispatch logic is transport-agnostic.
///
/// <para>
/// CONTENT-FREE / PAYLOAD-LESS (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). The push carries NO body at all
/// — only the VAPID authentication header and a TTL. The recipient's service worker treats it as a signal to open
/// the app and fetch the event through the authorized, visibility-gated channel (CORE-RT-005). Sending no payload is
/// the strongest possible lock-screen guarantee: there is nothing — not even an encrypted blob — for anything to
/// surface, and it needs no message-encryption crypto (only the VAPID JWT, <see cref="WebPushVapid"/>).
/// </para>
///
/// <para>
/// IT NEVER THROWS for an expected transport outcome. A push service that reports the subscription GONE (HTTP
/// 404/410) yields <see cref="WebPushSendOutcome.Gone"/> so the dispatch deletes the stale subscription; any other
/// non-2xx, or a transport/configuration error, yields <see cref="WebPushSendOutcome.Failed"/> (best-effort — a
/// missed signal is recovered when the client next opens the app). Only a genuine cancellation propagates. Failures
/// are logged by status/identifier only — never the endpoint, the keys or any content (threat T7).
/// </para>
/// </summary>
internal sealed class VapidWebPushSender : IWebPushSender
{
    // The VAPID JWT lifetime. RFC 8292 caps `exp` at 24h in the future; 12h is a safe, generous default that keeps
    // a signed header reusable within a sweep window without re-signing per request being a concern.
    private static readonly TimeSpan _tokenLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly WebPushDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VapidWebPushSender> _logger;

    public VapidWebPushSender(
        HttpClient httpClient,
        WebPushDeliveryOptions options,
        TimeProvider timeProvider,
        ILogger<VapidWebPushSender> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebPushSendResult> SendAsync(WebPushDispatch dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        try
        {
            using var request = BuildRequest(dispatch);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // A gone subscription: the push service reports the endpoint no longer exists. The dispatch deletes it.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new WebPushSendResult(WebPushSendOutcome.Gone);
            }

            if (response.IsSuccessStatusCode)
            {
                return new WebPushSendResult(WebPushSendOutcome.Delivered);
            }

            // Any other non-2xx is a best-effort failure: log the status code only (never the endpoint or keys).
            _logger.LogWarning(
                "A closed-app push was rejected by the push service with status {StatusCode}; it is dropped (best-effort).",
                (int)response.StatusCode);
            return new WebPushSendResult(WebPushSendOutcome.Failed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown / genuine cancellation: not a transport failure, let it propagate.
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or InvalidOperationException
                or FormatException
                or ArgumentException
                or CryptographicException)
        {
            // A transport error or a malformed VAPID configuration. Best-effort: log without the endpoint/keys/any
            // content (threat T7) and report Failed so the dispatch drops the row rather than blocking.
            _logger.LogWarning(exception, "A closed-app push could not be sent (transport or configuration error); it is dropped (best-effort).");
            return new WebPushSendResult(WebPushSendOutcome.Failed);
        }
    }

    private HttpRequestMessage BuildRequest(WebPushDispatch dispatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, dispatch.Endpoint)
        {
            // PAYLOAD-LESS: an empty body and no Content-Encoding, so there is nothing to encrypt and nothing to
            // leak (RFC 8030 allows a push with no payload). The signal is the push itself.
            Content = new ByteArrayContent([]),
        };

        using var signingKey = WebPushVapid.CreateSigningKey(_options.VapidPublicKey!, _options.VapidPrivateKey!);
        var authorization = WebPushVapid.CreateAuthorizationHeader(
            signingKey,
            _options.VapidPublicKey!,
            dispatch.Endpoint,
            _options.VapidSubject!,
            _timeProvider.GetUtcNow().Add(_tokenLifetime));

        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        // RFC 8030 requires a TTL the push service should hold an undelivered push for.
        request.Headers.TryAddWithoutValidation(
            "TTL",
            ((long)_options.TimeToLive.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        return request;
    }
}
