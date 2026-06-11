using System.Security.Claims;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Maps the claims of a validated OIDC token to an <see cref="OidcPrincipal"/>
/// (CORE-ID-001). This is the OIDC claims normalization owned by the
/// IdentityAccess module (docs/05_MODULE_CONTRACTS.md).
///
/// The mapper authenticates nothing itself: the input must be the
/// <see cref="ClaimsPrincipal"/> produced by token signature and lifetime
/// validation (authentication middleware wiring is a follow-up story in the
/// Identity and Tenant Boundaries epic). Claims are read from exactly one
/// authenticated identity: unauthenticated identities are ignored entirely,
/// and more than one authenticated identity fails the mapping, so no second
/// identity can ever smuggle a foreign identity or a foreign organization
/// claim into the principal (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Mapping is fail-closed: a required or security-relevant claim that is
/// missing, conflicting or malformed produces a typed
/// <see cref="OidcPrincipalMappingError"/>, never a partially trusted
/// principal. Optional display metadata is dropped when malformed or
/// conflicting instead of failing the mapping.
/// </summary>
public static class OidcPrincipalMapper
{
    /// <summary>
    /// Normalizes the claims of <paramref name="claimsPrincipal"/> into an
    /// <see cref="OidcPrincipal"/> or a typed mapping error.
    /// </summary>
    public static OidcPrincipalMappingResult Map(ClaimsPrincipal claimsPrincipal)
    {
        ArgumentNullException.ThrowIfNull(claimsPrincipal);

        var authenticatedIdentities = claimsPrincipal.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();

        if (authenticatedIdentities.Length == 0)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.NotAuthenticated);
        }

        // A second authenticated identity without its own iss/sub claims
        // would otherwise have its organization claims attributed to the
        // first identity's (iss, sub) — a tenant-boundary widening (T5).
        // One principal maps from exactly one identity, never a merge.
        if (authenticatedIdentities.Length > 1)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.AmbiguousIdentity);
        }

        var claims = authenticatedIdentities[0].Claims.ToArray();

        var issuerOutcome = FindSingleValue(claims, OidcClaimTypes.Issuer, out var issuer);
        if (issuerOutcome == ClaimLookupOutcome.Missing)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.MissingIssuer);
        }

        if (issuerOutcome == ClaimLookupOutcome.Conflicting)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.AmbiguousIssuer);
        }

        if (!OidcPrincipal.IsValidIssuer(issuer))
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.InvalidIssuer);
        }

        var subjectOutcome = FindSingleValue(claims, OidcClaimTypes.Subject, out var subjectId);
        if (subjectOutcome == ClaimLookupOutcome.Missing)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.MissingSubject);
        }

        if (subjectOutcome == ClaimLookupOutcome.Conflicting)
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.AmbiguousSubject);
        }

        if (!OidcPrincipal.IsValidSubjectId(subjectId))
        {
            return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.InvalidSubject);
        }

        // Organization claims feed the tenant boundary, so any malformed
        // value fails the whole mapping (fail-closed) instead of being
        // silently dropped like display metadata.
        var organizationClaims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, OidcClaimTypes.Organization, StringComparison.Ordinal))
            {
                continue;
            }

            if (!OidcPrincipal.IsValidOrganizationClaim(claim.Value))
            {
                return OidcPrincipalMappingResult.Failure(OidcPrincipalMappingError.InvalidOrganizationClaim);
            }

            organizationClaims.Add(claim.Value);
        }

        var displayName = FindOptionalValue(claims, OidcClaimTypes.Name, OidcPrincipal.IsValidDisplayName)
            ?? FindOptionalValue(claims, OidcClaimTypes.PreferredUsername, OidcPrincipal.IsValidDisplayName);
        var email = FindOptionalValue(claims, OidcClaimTypes.Email, OidcPrincipal.IsValidEmail);

        var type = HasServiceAccountMarker(claims) ? PrincipalType.ServiceAccount : PrincipalType.User;

        var principal = new OidcPrincipal(type, issuer, subjectId, displayName, email, organizationClaims);
        return OidcPrincipalMappingResult.Success(principal);
    }

    // Keycloak, the default self-hosted provider (docs/adr/0005), stamps
    // client-credentials tokens with a client_id claim through the protocol
    // mappers it creates when service accounts are enabled; end-user tokens
    // carry no such claim. The marker is issuer-asserted, so a human user can
    // never pose as a service account by choosing a particular username.
    private static bool HasServiceAccountMarker(Claim[] claims)
        => claims.Any(claim =>
            string.Equals(claim.Type, OidcClaimTypes.ClientId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(claim.Value));

    /// <summary>
    /// Looks up a required claim. Repeated identical values collapse to one;
    /// distinct values make the claim conflicting, because an ambiguous
    /// identity claim must never be resolved by picking one value.
    /// </summary>
    private static ClaimLookupOutcome FindSingleValue(Claim[] claims, string claimType, out string value)
    {
        value = string.Empty;
        string? single = null;

        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            {
                continue;
            }

            if (single is null)
            {
                single = claim.Value;
            }
            else if (!string.Equals(single, claim.Value, StringComparison.Ordinal))
            {
                return ClaimLookupOutcome.Conflicting;
            }
        }

        if (single is null)
        {
            return ClaimLookupOutcome.Missing;
        }

        value = single;
        return ClaimLookupOutcome.Found;
    }

    /// <summary>
    /// Looks up optional display metadata. Values are trimmed; malformed
    /// values are dropped and conflicting values make the claim absent
    /// (fail-safe: questionable metadata is discarded, never guessed).
    /// </summary>
    private static string? FindOptionalValue(Claim[] claims, string claimType, Func<string, bool> isValid)
    {
        string? single = null;

        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = claim.Value.Trim();
            if (!isValid(candidate))
            {
                continue;
            }

            if (single is null)
            {
                single = candidate;
            }
            else if (!string.Equals(single, candidate, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return single;
    }

    private enum ClaimLookupOutcome
    {
        Found,
        Missing,
        Conflicting,
    }
}
