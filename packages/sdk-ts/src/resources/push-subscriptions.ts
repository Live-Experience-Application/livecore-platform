// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Closed-app Web Push resource group (CORE-PUSH-001): the per-principal push
 * subscription registration surface (csv/api_routes.csv `GET /api/v1/push/vapid-public-key`,
 * `POST /api/v1/me/push-subscriptions`, `DELETE /api/v1/me/push-subscriptions/{subscriptionId}`).
 * A browser client reads the deployment VAPID public key, creates a Web Push
 * subscription against it, then registers that subscription with Core and can later
 * remove it.
 *
 * The server scopes every call to the authenticated caller, so a caller can only ever
 * register or delete its OWN subscription (threats T1/T5). With no VAPID key configured
 * the surface is inert: {@link getVapidPublicKey} returns a `null` key and
 * {@link register} is refused server-side. Closed-app DELIVERY is a later story
 * (CORE-PUSH-002).
 */
import type {
  PushSubscriptionResponse,
  PushVapidPublicKeyResponse,
  RegisterPushSubscriptionRequest,
} from "@livecore/contracts";
import type { Uuid } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class PushSubscriptionsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/push/vapid-public-key` — the deployment VAPID public key a client
   * subscribes against, or `null` when the push surface is inert (no key configured).
   */
  getVapidPublicKey(): Promise<PushVapidPublicKeyResponse> {
    return this.http.send<PushVapidPublicKeyResponse>({
      method: "GET",
      path: "/push/vapid-public-key",
    });
  }

  /**
   * `POST /api/v1/me/push-subscriptions` — register (idempotently) the caller's own
   * Web Push subscription. Re-registering the same endpoint refreshes its keys. Refused
   * server-side (`503`) when no VAPID key is configured.
   */
  register(
    request: RegisterPushSubscriptionRequest,
  ): Promise<PushSubscriptionResponse> {
    return this.http.send<PushSubscriptionResponse>({
      method: "POST",
      path: "/me/push-subscriptions",
      body: request,
    });
  }

  /**
   * `DELETE /api/v1/me/push-subscriptions/{subscriptionId}` — remove one of the
   * caller's own subscriptions. Responds `204 No Content`; a subscription that does not
   * exist or belongs to another principal is hidden as `404`.
   */
  delete(subscriptionId: Uuid): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/me/push-subscriptions/${encodeURIComponent(subscriptionId)}`,
    });
  }
}
