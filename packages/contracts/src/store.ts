// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
  PurchaseProvider,
  PurchaseTransactionStatus,
  StoreNotificationOutcome,
} from "./enums.js";

/**
 * Store module contracts (CORE-SDK-001): the purchase-verification requests a
 * mobile client submits and the responses the backend returns after verifying a
 * purchase with the provider's server APIs. The submitted proof is opaque and
 * untrusted — the backend verifies it before granting any entitlement, and the
 * proof is a secret that is never logged (docs/21; threat T7).
 */

/** Request body for `POST /api/v1/purchases/apple/transactions`. */
export interface AppleTransactionVerificationRequest {
  /** The opaque Apple App Store signed transaction (JWS) / transaction proof. */
  transaction: string;
  /** An optional opaque store product/subscription identifier. */
  productReference?: string;
}

/** Request body for `POST /api/v1/purchases/google/tokens`. */
export interface GoogleTokenVerificationRequest {
  /** The opaque Google Play purchase token to verify. */
  purchaseToken: string;
  /** An optional opaque store product/subscription identifier. */
  productReference?: string;
}

/** Response body of a successful purchase verification. */
export interface PurchaseVerificationResponse {
  /** The store that verified the purchase. */
  provider: PurchaseProvider;
  /** The provider-assigned transaction identifier of the verified purchase. */
  providerTransactionId: string;
  /** The opaque store product/subscription identifier the purchase is for. */
  productReference: string;
  /** The current lifecycle status of the recorded purchase. */
  status: PurchaseTransactionStatus;
}

/**
 * Acknowledgement body returned to a store for a received notification
 * (`POST /api/v1/store-notifications/apple` and `.../google/rtdn`). It carries
 * only the generic processing outcome name so the store stops re-delivering it.
 */
export interface StoreNotificationAck {
  /** What the endpoint did with the notification. */
  outcome: StoreNotificationOutcome;
}
