// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// User profile reference of the IdentityAccess module (CORE-ID-002).
///
/// A <see cref="UserProfile"/> is the Core-owned, persisted reference to a
/// known human user (docs/05_MODULE_CONTRACTS.md: "user profile reference";
/// csv/database_tables.csv: table <c>users</c>, "OIDC-linked user
/// reference"). It stores no credentials of any kind: authentication is
/// owned by the external OIDC provider (docs/adr/0005), and this type only
/// records which identities the platform has seen plus optional display
/// metadata mirrored from the provider's token claims.
///
/// Identity invariant: a profile is keyed by the OIDC identity pair
/// (<see cref="Issuer"/>, <see cref="SubjectId"/>), exactly as asserted by
/// the provider. Subjects are unique only within one issuer, so the same
/// subject value under a different issuer is a different user and must map
/// to a different profile. All identity comparisons are exact (ordinal and
/// case-sensitive); a near-match must never address a foreign profile
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The identity pair is
/// immutable for the lifetime of the profile.
///
/// Metadata invariant: <see cref="DisplayName"/> and <see cref="Email"/> are
/// a cache of provider-asserted claims, never an identity and never an
/// authorization input. Refreshing mirrors the token exactly, including
/// clearing values the provider no longer asserts, so stale personal data is
/// not retained.
/// </summary>
public sealed class UserProfile
{
    private UserProfile(
        Guid id,
        string issuer,
        string subjectId,
        string? displayName,
        string? email,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Profile id must not be empty.", nameof(id));
        }

        if (!OidcPrincipal.IsValidIssuer(issuer))
        {
            throw new ArgumentException("Issuer violates the issuer invariants.", nameof(issuer));
        }

        if (!OidcPrincipal.IsValidSubjectId(subjectId))
        {
            throw new ArgumentException("Subject violates the subject invariants.", nameof(subjectId));
        }

        if (displayName is not null && !OidcPrincipal.IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name violates the display name invariants.", nameof(displayName));
        }

        if (email is not null && !OidcPrincipal.IsValidEmail(email))
        {
            throw new ArgumentException("Email violates the email invariants.", nameof(email));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException("A profile cannot be updated before it was created.", nameof(updatedAt));
        }

        Id = id;
        Issuer = issuer;
        SubjectId = subjectId;
        DisplayName = displayName;
        Email = email;
        // Timestamps are normalized to UTC so the persisted timestamptz
        // values are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private UserProfile()
    {
        Issuer = null!;
        SubjectId = null!;
    }

    /// <summary>
    /// Surrogate key of the profile row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). Later stories reference profiles through
    /// this key; the OIDC identity pair stays the natural lookup key.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Identity of the OIDC provider that asserted this user, exactly as
    /// issued. Part of the immutable identity pair.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Issuer-local stable identifier of the user, exactly as issued. Part
    /// of the immutable identity pair.
    /// </summary>
    public string SubjectId { get; }

    /// <summary>
    /// Optional human-readable name mirrored from the provider.
    /// Informational metadata only: never an identity, never an
    /// authorization input, never written to logs (threat T7).
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// Optional email address mirrored from the provider. Informational
    /// metadata only: never an identity, never an authorization input,
    /// never written to logs (threat T7).
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>When this profile reference was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this profile reference was last refreshed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates the profile reference for a user principal. Only principals
    /// of type <see cref="PrincipalType.User"/> have a user profile; service
    /// accounts are machine clients, not users, and are rejected.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The principal is not a user principal.
    /// </exception>
    public static UserProfile CreateFromPrincipal(OidcPrincipal principal, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Type != PrincipalType.User)
        {
            throw new ArgumentException(
                "Only user principals have a user profile reference; service accounts are machine clients.",
                nameof(principal));
        }

        return new UserProfile(
            Guid.CreateVersion7(),
            principal.Issuer,
            principal.SubjectId,
            principal.DisplayName,
            principal.Email,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this profile belongs to the given OIDC identity. Both values
    /// must match exactly (ordinal and case-sensitive): subjects are unique
    /// only per issuer, so the same subject under a different issuer is a
    /// different user (threat T5). Blank or null values match nothing.
    /// </summary>
    public bool BelongsToOidcIdentity(string? issuer, string? subjectId)
        => !string.IsNullOrWhiteSpace(issuer)
            && !string.IsNullOrWhiteSpace(subjectId)
            && string.Equals(Issuer, issuer, StringComparison.Ordinal)
            && string.Equals(SubjectId, subjectId, StringComparison.Ordinal);

    /// <summary>
    /// Whether this profile belongs to the identity asserted by the given
    /// principal. Exact comparison, see
    /// <see cref="BelongsToOidcIdentity(string?, string?)"/>.
    /// </summary>
    public bool BelongsToPrincipal(OidcPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return BelongsToOidcIdentity(principal.Issuer, principal.SubjectId);
    }

    /// <summary>
    /// Whether the cached display metadata already mirrors the metadata
    /// asserted by the given principal, so callers can skip needless writes.
    /// </summary>
    public bool MetadataMatchesPrincipal(OidcPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return string.Equals(DisplayName, principal.DisplayName, StringComparison.Ordinal)
            && string.Equals(Email, principal.Email, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mirrors the display metadata of the given principal into this
    /// profile, including clearing values the provider no longer asserts.
    /// The principal must carry exactly this profile's identity pair: a
    /// profile must never be overwritten with claims of a foreign identity,
    /// no matter how similar (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The principal is not a user principal or belongs to a different
    /// OIDC identity.
    /// </exception>
    public void RefreshMetadataFromPrincipal(OidcPrincipal principal, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Type != PrincipalType.User)
        {
            throw new ArgumentException(
                "Only user principals have a user profile reference; service accounts are machine clients.",
                nameof(principal));
        }

        if (!BelongsToPrincipal(principal))
        {
            throw new ArgumentException(
                "The principal identity does not match this profile; a profile is never updated across identities.",
                nameof(principal));
        }

        DisplayName = principal.DisplayName;
        Email = principal.Email;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// profile id, subject and issuer. Display name and email are
    /// deliberately excluded (threat T7: logs carry identifiers and
    /// metadata, never personal or sensitive content).
    /// </summary>
    public override string ToString() => $"UserProfile {Id} sub={SubjectId} iss={Issuer}";
}
