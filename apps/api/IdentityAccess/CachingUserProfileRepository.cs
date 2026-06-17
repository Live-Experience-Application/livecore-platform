// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Caching decorator over <see cref="IUserProfileRepository"/> (CORE-PERF-003). The tenant context resolver runs
/// the user-profile-by-OIDC-identity lookup on EVERY authenticated request before the endpoint (to map the bearer
/// token's identity to the platform subject id). This decorator serves <see cref="FindByOidcIdentityAsync"/> from
/// the shared <see cref="AuthorizationLookupCache"/> so a high request rate by the same principal does not re-issue
/// the identical profile SELECT each time.
///
/// <para>
/// IMPORTANT — READ PATH ONLY. This decorator is registered as <see cref="IUserProfileRepository"/> for the
/// READ-ONLY consumer (the resolver, which uses only the profile's stable surrogate id). The
/// <see cref="UserProfileReferenceService"/> — the only writer, which loads-then-refreshes a profile's display
/// metadata in place — is wired to the CONCRETE <see cref="UserProfileRepository"/> instead, so it never
/// reads-modify-writes a shared cached instance. Only a positive lookup is cached (an unknown subject is never
/// cached, so first-login provisioning is observed immediately); the cached profile is treated as read-only and the
/// resolver reads only its immutable id, so a later display-metadata refresh through the concrete repository never
/// affects an authorization decision. ERASING a profile (the data-subject erasure command, CORE-PRIV-001)
/// invalidates the subject's cache group — evicting the profile AND the memberships the database cascade revoked —
/// so the erased subject cannot resolve a tenant from a stale cache. All other operations delegate to the inner
/// repository.
/// </para>
/// </summary>
internal sealed class CachingUserProfileRepository : IUserProfileRepository
{
    private const string _oidcKeyPrefix = "auth:user:oidc:";

    private readonly IUserProfileRepository _inner;
    private readonly AuthorizationLookupCache _cache;

    public CachingUserProfileRepository(IUserProfileRepository inner, AuthorizationLookupCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<UserProfile?> FindByOidcIdentityAsync(
        string issuer,
        string subjectId,
        CancellationToken cancellationToken)
        => _cache.GetOrAddAsync(
            OidcKey(issuer, subjectId),
            ct => _inner.FindByOidcIdentityAsync(issuer, subjectId, ct),
            static profile => [AuthorizationLookupCache.SubjectGroup(profile.Id)],
            cancellationToken);

    /// <inheritdoc />
    public Task<UserProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.FindByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken)
        => _inner.AddAsync(profile, cancellationToken);

    /// <inheritdoc />
    public async Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _inner.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);

        // Defensive: the only writer (UserProfileReferenceService) bypasses this decorator, but if a profile is
        // ever updated through the cached interface, evict its cache group so a later read sees the new metadata.
        _cache.InvalidateSubject(profile.Id);
    }

    /// <inheritdoc />
    public async Task EraseAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _inner.EraseAsync(profile, cancellationToken).ConfigureAwait(false);

        // Erasure HARD-deletes the profile and CASCADE-removes the subject's memberships in the database;
        // invalidating the subject group evicts the cached profile and every membership/role cached for them, so
        // the erased subject can no longer resolve a tenant from a stale cache.
        _cache.InvalidateSubject(profile.Id);
    }

    // A LENGTH-PREFIXED key so the (issuer, subject) boundary is unambiguous without any separator: two identities
    // can never collide on one key even if one value is a prefix of another. The identity pair is the OIDC subject
    // identity, never free-form content (threat T7).
    private static string OidcKey(string issuer, string subjectId)
        => string.Concat(
            _oidcKeyPrefix,
            (issuer?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            ":",
            issuer,
            ":",
            subjectId);
}
