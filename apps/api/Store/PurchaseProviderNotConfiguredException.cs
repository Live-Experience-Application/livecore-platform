// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Thrown by the <see cref="PurchaseVerificationProviderResolver"/> when verification is attempted for a
/// <see cref="PurchaseProvider"/> that has no concrete adapter configured (CORE-STORE-001).
///
/// The concrete, provider-specific verification adapter (its SDK and store credentials) is supplied by the
/// deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7 in docs/07_SECURITY_THREAT_MODEL.md). When none
/// is wired for a provider, Core must NOT fall back to trusting the client's unverified proof: it FAILS CLOSED,
/// so no entitlement is ever granted without a real server-side verification behind it ("Never unlock limits
/// before server verification succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). This mirrors how
/// the fail-closed <c>UnconfiguredAssetStorage</c> (CORE-AST-002) denies asset access when no storage adapter
/// is configured, and how the host denies cleanly without a database connection string or an OIDC authority.
/// The later verification endpoints (CORE-STORE-003/004) surface this as an unavailable-feature outcome, never
/// as an unverified grant.
///
/// The message carries only the provider NAME — never the submitted proof or any credential — so it is safe for
/// structured logs (threat T7).
/// </summary>
public sealed class PurchaseProviderNotConfiguredException : InvalidOperationException
{
    /// <summary>
    /// Creates a fail-closed error describing the provider whose verification could not be performed because no
    /// adapter is configured for it.
    /// </summary>
    public PurchaseProviderNotConfiguredException(PurchaseProvider provider)
        : base($"No purchase verification provider is configured for the {provider} store.")
    {
        Provider = provider;
    }

    /// <summary>The provider that had no configured verification adapter.</summary>
    public PurchaseProvider Provider { get; }
}
