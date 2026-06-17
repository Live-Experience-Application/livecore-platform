// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Application service that maintains the user profile reference for
/// authenticated user principals (CORE-ID-002).
///
/// Later identity stories (the tenant context resolver and the
/// <c>/api/v1/me</c> endpoint) call this service once per authenticated
/// request flow to guarantee that every known user has exactly one profile
/// row keyed by the OIDC identity pair (issuer, subject).
///
/// The service never crosses identities: profiles are looked up by the full
/// identity pair only, and metadata is refreshed only on the profile that
/// carries exactly the principal's identity (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Concurrent first requests of the same
/// user are resolved through the unique database index: the loser of the
/// create race adopts the winner's row instead of failing or duplicating.
/// </summary>
public sealed class UserProfileReferenceService
{
    private readonly IUserProfileRepository _repository;
    private readonly TimeProvider _timeProvider;

    public UserProfileReferenceService(IUserProfileRepository repository, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the profile reference for the given user principal, creating
    /// it on first sight and mirroring changed display metadata on every
    /// later sight. Service accounts have no user profile and are rejected.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The principal is not a user principal.
    /// </exception>
    public async Task<UserProfile> EnsureUserProfileAsync(OidcPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Type != PrincipalType.User)
        {
            throw new ArgumentException(
                "Only user principals have a user profile reference; service accounts are machine clients.",
                nameof(principal));
        }

        var existing = await _repository
            .FindByOidcIdentityAsync(principal.Issuer, principal.SubjectId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await RefreshMetadataIfChangedAsync(existing, principal, cancellationToken).ConfigureAwait(false);
        }

        var created = UserProfile.CreateFromPrincipal(principal, _timeProvider.GetUtcNow());
        var addResult = await _repository.AddAsync(created, cancellationToken).ConfigureAwait(false);
        if (addResult == UserProfileAddResult.Added)
        {
            return created;
        }

        // Lost the create race: a concurrent request persisted the profile
        // for this identity first. Adopt that row; the unique index already
        // guarantees it carries exactly this identity pair.
        var winner = await _repository
            .FindByOidcIdentityAsync(principal.Issuer, principal.SubjectId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The profile insert reported a duplicate identity, but no profile exists for the identity.");

        return await RefreshMetadataIfChangedAsync(winner, principal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserProfile> RefreshMetadataIfChangedAsync(
        UserProfile profile,
        OidcPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (profile.MetadataMatchesPrincipal(principal))
        {
            return profile;
        }

        profile.RefreshMetadataFromPrincipal(principal, _timeProvider.GetUtcNow());
        await _repository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }
}
