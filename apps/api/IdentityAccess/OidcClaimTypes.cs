// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Raw OIDC/JWT claim names consumed by the IdentityAccess module
/// (CORE-ID-001).
///
/// The module normalizes the original token claim names on purpose, instead
/// of the legacy SOAP-era claim URIs that the .NET JWT handler produces when
/// its inbound claim type mapping is enabled. The authentication wiring
/// (follow-up story) must therefore disable inbound claim type mapping so
/// tokens reach <see cref="OidcPrincipalMapper"/> unmodified. Until then this
/// behavior is fail-closed: a renamed subject claim maps to a typed failure,
/// never to a different identity.
/// </summary>
public static class OidcClaimTypes
{
    /// <summary>
    /// Token issuer (<c>iss</c>): the identity of the OIDC provider that
    /// asserted all other claims. Subjects are unique only per issuer.
    /// </summary>
    public const string Issuer = "iss";

    /// <summary>
    /// Subject (<c>sub</c>): the issuer-local, stable, unique identifier of
    /// the authenticated caller.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// Email address (<c>email</c>, OIDC standard claim). Informational
    /// metadata only; never used for identity or authorization decisions. On
    /// its own the value is unverified and spoofable; a feature may only key on
    /// it once <see cref="EmailVerified"/> proves the provider verified it.
    /// </summary>
    public const string Email = "email";

    /// <summary>
    /// Email-verification flag (<c>email_verified</c>, OIDC standard claim):
    /// the provider's assertion that it verified the end user controls the
    /// <see cref="Email"/> address (CORE-INV-001). Consumed fail-closed — the
    /// email is treated as a trustworthy verified fact only when the provider
    /// asserts a boolean <c>true</c>; a missing, <c>false</c>, non-boolean or
    /// conflicting value (and an absent email) leaves the email unverified. It
    /// is never an authorization input on any existing route; it only enables
    /// features that must safely key on the email (the invitation
    /// self-discovery in CORE-INV-002).
    /// </summary>
    public const string EmailVerified = "email_verified";

    /// <summary>
    /// Full display name (<c>name</c>, OIDC standard claim). Informational
    /// metadata only.
    /// </summary>
    public const string Name = "name";

    /// <summary>
    /// Username preferred by the end user (<c>preferred_username</c>, OIDC
    /// standard claim). Fallback display name; informational metadata only.
    /// </summary>
    public const string PreferredUsername = "preferred_username";

    /// <summary>
    /// Organization claim (<c>organization</c>): the multivalued claim that
    /// Keycloak-style providers use to assert which organizations the caller
    /// authenticated against. Raw token data for the tenant boundary; the
    /// mapping to actual organizations and memberships is owned by later
    /// stories (CORE-ID-003..005).
    /// </summary>
    public const string Organization = "organization";

    /// <summary>
    /// OAuth client identifier (<c>client_id</c>). Keycloak, the default
    /// self-hosted provider (docs/adr/0005), stamps client-credentials tokens
    /// with this claim; end-user tokens do not carry it. Its presence marks a
    /// <see cref="PrincipalType.ServiceAccount"/> principal.
    /// </summary>
    public const string ClientId = "client_id";
}
