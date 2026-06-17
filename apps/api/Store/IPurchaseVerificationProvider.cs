// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// The purchase verification adapter PORT (CORE-STORE-001) — the single seam between the Core platform and a
/// store's own server APIs that verify a purchase proof. One implementation handles exactly ONE
/// <see cref="PurchaseProvider"/> (its <see cref="Provider"/>), encapsulating that store's verification
/// protocol so the provider-specific logic lives entirely behind this interface. This is the abstraction the
/// epic acceptance criterion calls for: "Apple/Google provider logic is isolated from Core domain logic."
///
/// WHY A PORT (and not concrete provider logic in Core): verifying a proof requires the deployment's store
/// credentials and the provider's SDK / server endpoint (an App Store key, a Google service account), which
/// must never live in this repository (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). So the concrete,
/// provider-specific adapter is supplied by the deployment, exactly as the S3-compatible object-storage
/// adapter (<c>IAssetStorage</c>, CORE-AST-002) and the realtime scale-out backplane
/// (<c>IRealtimeBackplane</c>, CORE-RT-006) are deployment-supplied. Core carries no native store SDK
/// dependency and no store credentials in source (docs/13_SELF_HOSTING_REQUIREMENTS.md). Until an adapter is
/// wired for a provider, that provider has none registered and verification FAILS CLOSED through the
/// <see cref="PurchaseVerificationProviderResolver"/> — assets-storage's <c>UnconfiguredAssetStorage</c>
/// analogue — so Core never grants premium state without a real verification behind it ("Never unlock limits
/// before server verification succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// THE CONTRACT — every implementation MUST honor it:
/// <list type="bullet">
///   <item>
///   It serves exactly the provider named by <see cref="Provider"/>; a request for any other provider is the
///   resolver's concern, not this adapter's.
///   </item>
///   <item>
///   It treats <see cref="PurchaseVerificationRequest.Proof"/> as OPAQUE input to be verified against the
///   provider's server APIs — never as a trusted assertion. A client-supplied proof is untrusted until the
///   provider confirms it ("Never trust client-side premium flags", docs/21).
///   </item>
///   <item>
///   It reduces the provider's raw response to a provider-neutral <see cref="PurchaseVerificationResult"/>: a
///   normalized <see cref="VerifiedPurchase"/> on success, or a generic, log-safe rejection otherwise. The
///   raw provider payload never escapes the adapter.
///   </item>
///   <item>
///   A verdict of "not a genuine purchase" is a <see cref="PurchaseVerificationStatus.Rejected"/> RESULT; a
///   provider being unreachable/misconfigured is an EXCEPTION (fail-closed), so a transient outage is never
///   mistaken for a definitive rejection.
///   </item>
///   <item>
///   SANDBOX vs PRODUCTION (CORE-MON-008). It reports the <see cref="VerifiedPurchase.Environment"/> the proof
///   was verified in (the provider's sandbox/test environment or its live production environment — Apple's signed
///   transaction carries this; Google distinguishes test from live purchases). Core keeps the two apart through
///   the fail-closed <see cref="PurchaseEnvironmentPolicy"/> — a production deployment honors only a
///   <see cref="PurchaseEnvironment.Production"/> purchase — so "a sandbox receipt is not honored in production".
///   When the adapter genuinely cannot determine the environment it reports the fail-closed default
///   (<see cref="PurchaseEnvironment.Sandbox"/>), never production.
///   </item>
///   <item>
///   REPLAY PROTECTION (CORE-MON-008). A receipt whose proof has already been consumed/redeemed at the provider
///   is NOT a fresh grantable purchase: the adapter MUST surface that as a <see cref="PurchaseVerificationStatus.Rejected"/>
///   RESULT (so a replayed proof grants nothing). Core adds a second, provider-independent layer of replay
///   defence: recording is idempotent on the (<see cref="PurchaseProvider"/>,
///   <see cref="VerifiedPurchase.ProviderTransactionId"/>) pair (CORE-STORE-002), so even a replayed-but-genuine
///   proof that re-verifies to the SAME purchase records no second row and grants nothing twice.
///   </item>
/// </list>
///
/// AUTHORIZATION IS UPSTREAM, NOT HERE. This port does not decide WHO may submit a proof or WHETHER they are
/// authorized — that is the verification endpoint's server-side responsibility (the later CORE-STORE-003 Apple
/// and CORE-STORE-004 Google routes authorize the caller and only THEN ask the resolved adapter to verify).
/// The adapter is a focused, secure verifier invoked after the permission check has passed.
/// </summary>
public interface IPurchaseVerificationProvider
{
    /// <summary>The single store this adapter verifies proofs for.</summary>
    PurchaseProvider Provider { get; }

    /// <summary>
    /// Verifies the request's opaque proof against this provider's server APIs and returns a provider-neutral
    /// outcome — a <see cref="PurchaseVerificationStatus.Verified"/> result carrying the normalized
    /// <see cref="VerifiedPurchase"/>, or a <see cref="PurchaseVerificationStatus.Rejected"/> result carrying a
    /// generic reason. The request's <see cref="PurchaseVerificationRequest.Provider"/> is expected to match
    /// <see cref="Provider"/> (the resolver guarantees this before calling).
    /// </summary>
    /// <param name="request">The provider-neutral verification request carrying the opaque proof.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider-neutral verification result.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    Task<PurchaseVerificationResult> VerifyAsync(PurchaseVerificationRequest request, CancellationToken cancellationToken);
}
