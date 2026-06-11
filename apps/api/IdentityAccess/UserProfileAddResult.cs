namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Outcome of persisting a new user profile reference (CORE-ID-002).
/// </summary>
public enum UserProfileAddResult
{
    /// <summary>The profile was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A profile with the same OIDC identity pair (issuer, subject) already
    /// exists. The existing profile was not modified; the caller decides
    /// whether to re-read it (for example after losing a create race).
    /// </summary>
    DuplicateIdentity = 2,
}
