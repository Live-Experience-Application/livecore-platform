// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The single OUTBOUND seam for sending one closed-app Web Push (CORE-PUSH-002). The worker's dispatch service
/// drains the outbox and hands each (subscription, signal) pair to this sender; the concrete VAPID-signed HTTP
/// implementation (<see cref="VapidWebPushSender"/>) lives behind it, so the dispatch logic stays transport-agnostic
/// and fully testable (a test substitutes a recording sender, exactly as the realtime tests substitute the
/// backplane).
///
/// CONTENT-FREE BY CONTRACT. The sender is only ever given a <see cref="WebPushSignal"/> of IDENTIFIERS — never a
/// projected title, body or content — so nothing sensitive can be put on the wire or surface on a lock screen
/// (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public interface IWebPushSender
{
    /// <summary>
    /// Sends one content-free push to the given subscription. Never throws for an expected transport outcome: a
    /// transport/HTTP failure is returned as <see cref="WebPushSendOutcome.Failed"/> and a gone subscription
    /// (HTTP 404/410) as <see cref="WebPushSendOutcome.Gone"/>, so the caller can clean up a stale subscription
    /// without exception handling. Only a genuine cancellation propagates.
    /// </summary>
    /// <exception cref="ArgumentNullException">The dispatch is null.</exception>
    Task<WebPushSendResult> SendAsync(WebPushDispatch dispatch, CancellationToken cancellationToken);
}

/// <summary>The CONTENT-FREE signal a closed-app push carries: identifiers only, never projected content (threats T2/T7).</summary>
/// <param name="SessionEventId">The source session event the recipient is signalled about (an identifier).</param>
/// <param name="SessionId">The session the event belongs to (an identifier).</param>
public sealed record WebPushSignal(Guid SessionEventId, Guid SessionId);

/// <summary>
/// One outbound push to send: the target subscription's push-service endpoint and its client encryption keys
/// (carried for the W3C Push API; the default sender delivers a payload-less signal and does not need them, but the
/// seam carries them so an encrypted-payload sender could be swapped in without a contract change) plus the
/// content-free <see cref="Signal"/>.
/// </summary>
public sealed record WebPushDispatch(string Endpoint, string P256dh, string Auth, WebPushSignal Signal);

/// <summary>The outcome of one push send.</summary>
public enum WebPushSendOutcome
{
    /// <summary>The push service accepted the push (a 2xx response).</summary>
    Delivered,

    /// <summary>The subscription is gone (HTTP 404/410) and must be deleted as stale.</summary>
    Gone,

    /// <summary>The send failed (a transport error or a non-2xx, non-gone response); best-effort, drop it.</summary>
    Failed,
}

/// <summary>The result of one push send.</summary>
/// <param name="Outcome">Whether the push was delivered, the subscription is gone, or the send failed.</param>
public readonly record struct WebPushSendResult(WebPushSendOutcome Outcome);
