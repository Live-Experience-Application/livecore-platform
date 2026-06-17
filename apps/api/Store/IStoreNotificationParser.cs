// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// The store notification parser/validator PORT (CORE-STORE-005) — the single seam between the Core platform and
/// a store's server-to-server notification format. One implementation handles exactly ONE
/// <see cref="PurchaseProvider"/> (its <see cref="Provider"/>), encapsulating that store's notification protocol
/// (its signature scheme, its envelope and its raw notification types) so the provider-specific logic lives
/// entirely behind this interface — the notification analogue of <see cref="IPurchaseVerificationProvider"/>
/// (CORE-STORE-001).
///
/// WHY A PORT (and not concrete provider logic in Core): validating a notification's authenticity requires the
/// provider's signing keys / source verification (Apple's App Store Server Notifications V2 JWS chain, Google's
/// Pub/Sub push identity) and SDK, which must never live in this repository (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). So the concrete adapter is supplied by the deployment, exactly as the
/// purchase verification adapter, the S3-compatible <c>IAssetStorage</c> and the realtime <c>IRealtimeBackplane</c>
/// are (docs/13_SELF_HOSTING_REQUIREMENTS.md). Until an adapter is wired for a provider, that provider has none
/// registered and notification handling FAILS CLOSED through the <see cref="StoreNotificationParserResolver"/> —
/// so an unauthenticated notification endpoint can never change a purchase without a real validator behind it.
///
/// THE CONTRACT — every implementation MUST honor it:
/// <list type="bullet">
///   <item>It serves exactly the provider named by <see cref="Provider"/>; routing is the resolver's concern.</item>
///   <item>It treats <see cref="StoreNotificationEnvelope.RawPayload"/> as UNTRUSTED input to be VALIDATED — a
///   notification endpoint is unauthenticated, so the payload could be forged or replayed. It validates the
///   signature/source BEFORE reading any field ("Must validate signature/idempotency" / "Must validate
///   source/idempotency", csv/mobile_store_api_routes.csv).</item>
///   <item>It reduces the result to a provider-neutral <see cref="StoreNotificationParseResult"/>: a normalized
///   <see cref="StoreNotification"/> for an authentic, ACTIONABLE notification; an <c>Ignored</c> result for an
///   authentic but non-actionable one; a <c>Rejected</c> result (generic, log-safe reason) for an
///   unauthentic/unparseable one. The raw provider payload never escapes the adapter.</item>
///   <item>A verdict of "not authentic / not parseable" is a <see cref="StoreNotificationParseStatus.Rejected"/>
///   RESULT; the provider's infrastructure being unreachable/misconfigured is an EXCEPTION (fail-closed), so a
///   transient outage is never mistaken for a definitive rejection.</item>
/// </list>
///
/// The port does NOT deduplicate, persist or apply the notification — that is the
/// <see cref="StoreNotificationService"/>'s job, invoked only after the adapter has produced a trusted normalized
/// notification. The adapter is a focused, secure validator/normalizer.
/// </summary>
public interface IStoreNotificationParser
{
    /// <summary>The single store this adapter validates and parses notifications for.</summary>
    PurchaseProvider Provider { get; }

    /// <summary>
    /// Validates the envelope's signature/source and parses its opaque payload into a provider-neutral outcome —
    /// a <see cref="StoreNotificationParseStatus.Parsed"/> result carrying the normalized
    /// <see cref="StoreNotification"/>, an <see cref="StoreNotificationParseStatus.Ignored"/> result, or a
    /// <see cref="StoreNotificationParseStatus.Rejected"/> result carrying a generic reason. The envelope's
    /// <see cref="StoreNotificationEnvelope.Provider"/> is expected to match <see cref="Provider"/> (the resolver
    /// guarantees this before calling).
    /// </summary>
    /// <param name="envelope">The provider-neutral envelope carrying the opaque raw payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider-neutral parse result.</returns>
    /// <exception cref="ArgumentNullException">The envelope is null.</exception>
    Task<StoreNotificationParseResult> ParseAsync(StoreNotificationEnvelope envelope, CancellationToken cancellationToken);
}
