// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Typed reason why token claims could not be mapped to an
/// <see cref="OidcPrincipal"/> (CORE-ID-001).
///
/// Values are deliberately identifier-only: they can be logged and translated
/// into a 401 response without ever echoing claim contents (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md, "never leak sensitive content in
/// errors" in docs/02_ARCHITECTURE.md).
/// </summary>
public enum OidcPrincipalMappingError
{
    /// <summary>Mapping succeeded; no error.</summary>
    None = 0,

    /// <summary>The caller carries no authenticated identity.</summary>
    NotAuthenticated = 1,

    /// <summary>The token carries no issuer claim.</summary>
    MissingIssuer = 2,

    /// <summary>The token carries conflicting issuer claim values.</summary>
    AmbiguousIssuer = 3,

    /// <summary>The issuer claim value violates the issuer invariants.</summary>
    InvalidIssuer = 4,

    /// <summary>The token carries no subject claim.</summary>
    MissingSubject = 5,

    /// <summary>The token carries conflicting subject claim values.</summary>
    AmbiguousSubject = 6,

    /// <summary>The subject claim value violates the subject invariants.</summary>
    InvalidSubject = 7,

    /// <summary>
    /// An organization claim value violates the organization claim
    /// invariants. Organization claims feed the tenant boundary, so a
    /// malformed value fails the whole mapping instead of being dropped.
    /// </summary>
    InvalidOrganizationClaim = 8,

    /// <summary>
    /// The caller carries more than one authenticated identity. A principal
    /// maps from exactly one identity; merging claims across identities could
    /// attribute one identity's organization claims to another's subject.
    /// </summary>
    AmbiguousIdentity = 9,
}
