// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Request body of <c>POST /api/v1/me/push-subscriptions</c> (CORE-PUSH-001): the W3C Push API subscription a
/// browser produced for the authenticated caller — the push service <see cref="Endpoint"/> URL plus the
/// client's <see cref="P256dh"/> public key and <see cref="Auth"/> secret. The subscription is registered
/// scoped to the CALLER (resolved server-side from the principal); the body never names a target principal, so
/// a caller can only ever register a subscription for itself (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Endpoint">The push service endpoint URL the browser registered.</param>
/// <param name="P256dh">The client's P-256 ECDH public key (base64url).</param>
/// <param name="Auth">The client's auth secret (base64url), used to encrypt a future push payload.</param>
public sealed record RegisterPushSubscriptionRequest(string? Endpoint, string? P256dh, string? Auth);

/// <summary>
/// Response projection of a registered push subscription (CORE-PUSH-001). It carries the subscription's
/// surrogate id (the handle the DELETE route addresses), the endpoint and the creation time — never the
/// <c>auth</c> encryption secret, which is write-only and never echoed back to a client (threat T7).
/// </summary>
/// <param name="Id">Surrogate id of the subscription (the handle <c>DELETE /api/v1/me/push-subscriptions/{id}</c> uses).</param>
/// <param name="Endpoint">The push service endpoint URL the subscription is registered against.</param>
/// <param name="CreatedAt">When the subscription was first registered (UTC).</param>
public sealed record PushSubscriptionResponse(Guid Id, string Endpoint, DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="PushSubscription"/> into its response DTO, never emitting the auth secret.</summary>
    public static PushSubscriptionResponse From(PushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new PushSubscriptionResponse(subscription.Id, subscription.Endpoint, subscription.CreatedAt);
    }
}

/// <summary>
/// Response projection of the deployment's VAPID public key (CORE-PUSH-001,
/// <c>GET /api/v1/push/vapid-public-key</c>). The <see cref="PublicKey"/> is the value a browser client needs
/// to create a push subscription; it is <see langword="null"/> when no key is configured, in which case the
/// push surface is inert (no subscription is registrable). The VAPID PRIVATE key is never exposed (threat T7).
/// </summary>
/// <param name="PublicKey">The configured VAPID public key, or <see langword="null"/> when the surface is inert.</param>
public sealed record PushVapidPublicKeyResponse(string? PublicKey);
