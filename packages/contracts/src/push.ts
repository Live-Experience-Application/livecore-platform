// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Closed-app Web Push contracts (CORE-PUSH-001): the per-principal push subscription
 * registration surface and the deployment VAPID public key. A browser client reads
 * the VAPID public key (`GET /api/v1/push/vapid-public-key`), creates a Web Push
 * subscription against it, then registers that subscription with Core
 * (`POST /api/v1/me/push-subscriptions`) and can remove it
 * (`DELETE /api/v1/me/push-subscriptions/{subscriptionId}`). The subscription is
 * scoped server-side to the authenticated caller, so a caller can only ever register
 * or delete its OWN subscription (threats T1/T5). The `auth` encryption secret is
 * write-only — it is never echoed back to a client (threat T7). Closed-app DELIVERY
 * is a later story (CORE-PUSH-002); this is the registration enabler.
 */

/**
 * Request body of `POST /api/v1/me/push-subscriptions`: the W3C Push API subscription
 * the browser produced — the push service `endpoint` URL plus the client's `p256dh`
 * public key and `auth` secret. The subscription is registered scoped to the caller
 * (resolved server-side from the principal); the body never names a target principal.
 */
export interface RegisterPushSubscriptionRequest {
  /** The push service endpoint URL the browser registered. */
  endpoint: string;
  /** The client's P-256 ECDH public key (base64url). */
  p256dh: string;
  /** The client's auth secret (base64url), used to encrypt a future push payload. */
  auth: string;
}

/**
 * Response of a registered push subscription. It carries the subscription's surrogate
 * id (the handle the DELETE route addresses), the endpoint and the creation time —
 * never the `auth` encryption secret, which is write-only (threat T7).
 */
export interface PushSubscriptionResponse {
  /** Surrogate id of the subscription (the handle `DELETE /api/v1/me/push-subscriptions/{id}` uses). */
  id: Uuid;
  /** The push service endpoint URL the subscription is registered against. */
  endpoint: string;
  /** When the subscription was first registered (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * Response of `GET /api/v1/push/vapid-public-key`: the deployment VAPID public key a
 * client needs to create a push subscription, or `null` when no key is configured —
 * in which case the push surface is inert (no subscription is registrable). The VAPID
 * PRIVATE key is never exposed (threat T7).
 */
export interface PushVapidPublicKeyResponse {
  /** The configured VAPID public key, or `null` when the surface is inert. */
  publicKey: string | null;
}
