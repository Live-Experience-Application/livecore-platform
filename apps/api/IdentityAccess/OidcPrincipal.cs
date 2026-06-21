// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections.Frozen;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Authenticated principal of the IdentityAccess module (CORE-ID-001).
///
/// An <see cref="OidcPrincipal"/> is the normalized, validated representation
/// of a caller derived from the claims of a validated OIDC token
/// (docs/05_MODULE_CONTRACTS.md: authenticated principal mapping and OIDC
/// claims normalization; docs/adr/0005: OIDC-first, no custom password
/// authentication). Instances are immutable and only exist in a valid state:
/// the constructor rejects every argument that violates an invariant.
///
/// Identity invariant: a principal is identified by the pair
/// (<see cref="Issuer"/>, <see cref="SubjectId"/>). Subjects are unique only
/// within one issuer, so neither value alone identifies a caller. Both values
/// are preserved exactly as asserted (no trimming, no case folding), because
/// any normalization could merge two distinct identities.
///
/// Tenant boundary: <see cref="OrganizationClaims"/> carries only the
/// organization claim values asserted by the identity provider. They are raw
/// token data, not membership decisions; resolving them against organizations
/// and memberships is owned by later stories (CORE-ID-003..005). Matching is
/// exact and case-sensitive so claim-value variance can never widen the
/// tenant boundary (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class OidcPrincipal
{
    /// <summary>
    /// Maximum accepted issuer length. Issuers are http(s) URIs; anything
    /// beyond this length is rejected as hostile or broken input.
    /// </summary>
    public const int MaxIssuerLength = 2048;

    /// <summary>
    /// Maximum accepted subject length, per the OIDC Core requirement that
    /// the <c>sub</c> value must not exceed 255 characters.
    /// </summary>
    public const int MaxSubjectIdLength = 255;

    /// <summary>
    /// Maximum accepted length of a single organization claim value.
    /// </summary>
    public const int MaxOrganizationClaimLength = 255;

    /// <summary>
    /// Maximum accepted display name length. Display names are informational
    /// metadata; longer values are dropped during mapping.
    /// </summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>
    /// Maximum accepted email length (the maximum length of a valid address).
    /// </summary>
    public const int MaxEmailLength = 320;

    private readonly FrozenSet<string> _organizationClaims;

    /// <summary>
    /// Creates a principal and enforces all invariants. Throws when an
    /// argument violates them; use <see cref="OidcPrincipalMapper"/> to turn
    /// untrusted token claims into a principal without exceptions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The principal type is not a defined <see cref="PrincipalType"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The issuer, subject, organization claims or optional metadata violate
    /// an invariant.
    /// </exception>
    public OidcPrincipal(
        PrincipalType type,
        string issuer,
        string subjectId,
        string? displayName = null,
        string? email = null,
        bool emailVerified = false,
        IEnumerable<string>? organizationClaims = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown principal type.");
        }

        if (!IsValidIssuer(issuer))
        {
            throw new ArgumentException(
                $"Issuer must be an absolute http(s) URI without query or fragment, at most {MaxIssuerLength} characters long.",
                nameof(issuer));
        }

        if (!IsValidSubjectId(subjectId))
        {
            throw new ArgumentException(
                $"Subject must be a non-blank value of at most {MaxSubjectIdLength} characters without control characters.",
                nameof(subjectId));
        }

        if (displayName is not null && !IsValidDisplayName(displayName))
        {
            throw new ArgumentException(
                $"Display name must be non-blank, at most {MaxDisplayNameLength} characters long and free of control characters.",
                nameof(displayName));
        }

        if (email is not null && !IsValidEmail(email))
        {
            throw new ArgumentException(
                "Email must be a plausibly shaped address without control characters.",
                nameof(email));
        }

        // Fail-closed (CORE-INV-001): a "verified email" is only meaningful
        // when an email is actually present. The model can never hold the
        // verified fact without an email, so a feature keying on the verified
        // email (CORE-INV-002) can trust the pair and an absent email always
        // reads as unverified (an enumeration/invitation-hijack guard; threats
        // T5/T6 in docs/07_SECURITY_THREAT_MODEL.md).
        if (emailVerified && email is null)
        {
            throw new ArgumentException(
                "Email cannot be marked verified when no email address is present.",
                nameof(emailVerified));
        }

        var normalizedOrganizationClaims = new HashSet<string>(StringComparer.Ordinal);
        if (organizationClaims is not null)
        {
            foreach (var organizationClaim in organizationClaims)
            {
                if (!IsValidOrganizationClaim(organizationClaim))
                {
                    throw new ArgumentException(
                        $"Organization claim values must be non-blank, at most {MaxOrganizationClaimLength} characters long and free of control characters.",
                        nameof(organizationClaims));
                }

                normalizedOrganizationClaims.Add(organizationClaim);
            }
        }

        Type = type;
        Issuer = issuer;
        SubjectId = subjectId;
        DisplayName = displayName;
        Email = email;
        EmailVerified = emailVerified;
        // Frozen so the advertised immutability cannot be defeated by casting
        // the exposed set back to a mutable implementation.
        _organizationClaims = normalizedOrganizationClaims.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Whether the caller is a human user or a machine client.</summary>
    public PrincipalType Type { get; }

    /// <summary>
    /// Identity of the OIDC provider that asserted this principal, exactly as
    /// issued. Issuer comparison is exact per the OIDC specification.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Issuer-local stable identifier of the caller, exactly as issued.
    /// </summary>
    public string SubjectId { get; }

    /// <summary>
    /// Optional human-readable name. Informational metadata only: never an
    /// identity, never an authorization input, never written to logs.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Optional email address. Informational metadata only: never an
    /// identity, never an authorization input, never written to logs. On its
    /// own this value is unverified and spoofable; a feature may only key on
    /// the email once <see cref="EmailVerified"/> is true.
    /// </summary>
    public string? Email { get; }

    /// <summary>
    /// Whether the identity provider asserted that it verified the end user
    /// controls <see cref="Email"/> (the OIDC <c>email_verified</c> claim,
    /// CORE-INV-001). True only when the provider asserted a boolean
    /// <c>true</c> for a present, valid email; a missing, <c>false</c>,
    /// non-boolean or conflicting claim — and an absent email — leave this
    /// false (fail-closed). This is the only trustworthy email fact a feature
    /// may safely key on (the invitation self-discovery in CORE-INV-002),
    /// because the bare <see cref="Email"/> is spoofable; keying on it would be
    /// an email-enumeration / invitation-hijack vector (threats T5/T6). It is
    /// NEVER an authorization input on any existing route.
    /// </summary>
    public bool EmailVerified { get; }

    /// <summary>
    /// Organization claim values the identity provider asserted for this
    /// principal. Deduplicated, validated, immutable raw token data for the
    /// tenant boundary; not a membership decision.
    /// </summary>
    public IReadOnlySet<string> OrganizationClaims => _organizationClaims;

    /// <summary>
    /// Whether the identity provider asserted the given organization claim
    /// value for this principal. The comparison is exact (ordinal and
    /// case-sensitive): a near-match must never cross the tenant boundary
    /// (threat T5). Blank or null values match nothing. This checks the token
    /// assertion only; organization membership decisions are owned by later
    /// stories.
    /// </summary>
    public bool HasOrganizationClaim(string? organizationClaimValue)
        => !string.IsNullOrWhiteSpace(organizationClaimValue)
            && _organizationClaims.Contains(organizationClaimValue);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// principal type, subject and issuer. Display name and email are
    /// deliberately excluded (threat T7: logs carry identifiers and metadata,
    /// never personal or sensitive content).
    /// </summary>
    public override string ToString() => $"{Type} sub={SubjectId} iss={Issuer}";

    internal static bool IsValidIssuer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxIssuerLength
            || ContainsControlCharacter(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var issuerUri))
        {
            return false;
        }

        // OIDC Core requires an https issuer with no query or fragment.
        // Plain http stays accepted at the model level so self-hosted
        // development providers work; transport requirements for production
        // are enforced where tokens are validated (follow-up story).
        var hasHttpScheme = issuerUri.Scheme == Uri.UriSchemeHttps || issuerUri.Scheme == Uri.UriSchemeHttp;
        return hasHttpScheme
            && string.IsNullOrEmpty(issuerUri.Query)
            && string.IsNullOrEmpty(issuerUri.Fragment);
    }

    internal static bool IsValidSubjectId(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxSubjectIdLength
            && !ContainsControlCharacter(value);

    internal static bool IsValidOrganizationClaim(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxOrganizationClaimLength
            && !ContainsControlCharacter(value);

    internal static bool IsValidDisplayName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxDisplayNameLength
            && !ContainsControlCharacter(value);

    internal static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxEmailLength
            || ContainsControlCharacter(value))
        {
            return false;
        }

        // Shape check only (local part, separator, domain part). The value is
        // informational metadata; full address validation belongs to the
        // identity provider.
        var separatorIndex = value.IndexOf('@');
        return separatorIndex > 0 && separatorIndex < value.Length - 1;
    }

    // Control characters in identity values enable log forging and header
    // injection downstream; they never appear in legitimate token claims.
    private static bool ContainsControlCharacter(string value)
        => value.Any(char.IsControl);
}
