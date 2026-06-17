// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// The external store INFRASTRUCTURE provider whose server APIs verify a purchase proof (CORE-STORE-001,
/// the first story of the "Store Purchase Verification" epic). A mobile app completes a purchase against
/// one of these stores and sends its proof (a transaction token / JWS / purchase token) to the backend,
/// which verifies it with the provider's own server APIs before granting any entitlement
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification"). This enum names WHICH
/// provider a <see cref="PurchaseVerificationRequest"/> targets, so Core can select the right verifier
/// (<see cref="IPurchaseVerificationProvider"/>) without itself knowing any provider's protocol.
///
/// PROVIDER NAMES ARE ALLOWED HERE. <c>Apple</c> and <c>Google</c> are infrastructure provider names, not
/// vertical product vocabulary: docs/21 states "Apple/Google names are allowed here as infrastructure
/// provider names, not product vertical names", and docs/22 allows provider names "in ... provider
/// infrastructure modules". They are not in csv/forbidden_core_terms.csv. Core still contains NO native
/// store SDK and NO store credentials — the provider-specific verification logic lives in a
/// deployment-supplied adapter behind the <see cref="IPurchaseVerificationProvider"/> port (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// The provider is persisted by its stable NAME (the <c>purchase_providers</c> / <c>purchase_transactions</c>
/// tables are a later story, CORE-STORE-002), so the integers below are only in-memory discriminators with
/// no ordering meaning, exactly like every other enum in the model (e.g. <c>EntitlementSubjectType</c>);
/// they must not be compared with &gt;/&lt;.
/// </summary>
public enum PurchaseProvider
{
    /// <summary>
    /// The Apple App Store. Its proof is an App Store signed transaction (JWS) submitted to
    /// <c>POST /v1/purchases/apple/transactions</c> (csv/mobile_store_api_routes.csv); the deployment-supplied
    /// adapter verifies it with Apple's server APIs (the verification endpoint is the later CORE-STORE-003).
    /// </summary>
    Apple = 1,

    /// <summary>
    /// The Google Play store. Its proof is a Google Play purchase token submitted to
    /// <c>POST /v1/purchases/google/tokens</c> (csv/mobile_store_api_routes.csv); the deployment-supplied
    /// adapter verifies it with Google's server APIs (the verification endpoint is the later CORE-STORE-004).
    /// </summary>
    Google = 2,
}
