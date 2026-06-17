// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Thrown by the <see cref="StoreNotificationParserResolver"/> when a store notification arrives for a
/// <see cref="PurchaseProvider"/> that has no concrete parser adapter configured (CORE-STORE-005).
///
/// The concrete, provider-specific notification validator (its signing keys and source verification) is supplied
/// by the deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in docs/07_SECURITY_THREAT_MODEL.md). A
/// store notification endpoint is unauthenticated at the HTTP layer (csv/mobile_store_api_routes.csv: the two
/// store-notification routes are <c>auth_required=false</c>), so the ONLY thing that makes an inbound payload
/// trustworthy is the adapter's signature/source validation. When none is wired for a provider, Core must NOT
/// fall back to trusting the unauthenticated payload: it FAILS CLOSED, so no purchase is ever changed without a
/// real validator behind it. This mirrors the fail-closed <see cref="PurchaseProviderNotConfiguredException"/>
/// (CORE-STORE-001) and the unconfigured asset storage (CORE-AST-002). The notification endpoints surface this as
/// an unavailable-feature outcome (503), never as an applied change.
///
/// The message carries only the provider NAME — never the payload or any credential — so it is safe for
/// structured logs (threat T7).
/// </summary>
public sealed class StoreNotificationParserNotConfiguredException : InvalidOperationException
{
    /// <summary>
    /// Creates a fail-closed error describing the provider whose notification could not be validated because no
    /// parser adapter is configured for it.
    /// </summary>
    public StoreNotificationParserNotConfiguredException(PurchaseProvider provider)
        : base($"No store notification parser is configured for the {provider} store.")
    {
        Provider = provider;
    }

    /// <summary>The provider that had no configured notification parser.</summary>
    public PurchaseProvider Provider { get; }
}
