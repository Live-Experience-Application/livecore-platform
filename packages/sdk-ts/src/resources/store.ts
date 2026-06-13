/**
 * Store resource group (CORE-SDK-002): submitting a purchase proof for
 * server-side verification before any entitlement is granted. The submitted
 * proof is opaque and untrusted; the backend verifies it with the provider's
 * server APIs (docs/21). The proof is a secret — send it over HTTPS and never
 * log it (threat T7).
 */
import type {
  AppleTransactionVerificationRequest,
  GoogleTokenVerificationRequest,
  PurchaseVerificationResponse,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class StoreClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `POST /api/v1/purchases/apple/transactions` — verify an Apple App Store
   * signed transaction server-side.
   */
  verifyAppleTransaction(
    request: AppleTransactionVerificationRequest,
  ): Promise<PurchaseVerificationResponse> {
    return this.http.send<PurchaseVerificationResponse>({
      method: "POST",
      path: "/purchases/apple/transactions",
      body: request,
    });
  }

  /**
   * `POST /api/v1/purchases/google/tokens` — verify a Google Play purchase token
   * server-side.
   */
  verifyGoogleToken(
    request: GoogleTokenVerificationRequest,
  ): Promise<PurchaseVerificationResponse> {
    return this.http.send<PurchaseVerificationResponse>({
      method: "POST",
      path: "/purchases/google/tokens",
      body: request,
    });
  }
}
