namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Persistence contract for the user profile reference (CORE-ID-002). The
/// IdentityAccess module owns the <c>users</c> table; other modules access
/// profiles only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations).
///
/// Lookups address a profile only by its full OIDC identity pair
/// (issuer, subject). There is deliberately no lookup by subject alone:
/// subjects are unique only per issuer, so such a lookup could return a
/// foreign user's profile (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public interface IUserProfileRepository
{
    /// <summary>
    /// Finds the profile with exactly the given OIDC identity pair, or
    /// <see langword="null"/> when no such profile exists. Matching is
    /// exact (ordinal and case-sensitive) on both values.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The issuer or subject violates the corresponding identity invariants.
    /// Invalid values can never address a stored profile, so the lookup is
    /// rejected instead of silently returning nothing.
    /// </exception>
    Task<UserProfile?> FindByOidcIdentityAsync(string issuer, string subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new profile. Returns
    /// <see cref="UserProfileAddResult.DuplicateIdentity"/> when a profile
    /// with the same OIDC identity pair already exists (enforced by the
    /// unique database index, so concurrent first-time callers can never
    /// create two profiles for one identity).
    /// </summary>
    Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a profile previously loaded through this
    /// repository. The identity pair of a profile is immutable; only
    /// display metadata and the update timestamp change.
    /// </summary>
    Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken);
}
