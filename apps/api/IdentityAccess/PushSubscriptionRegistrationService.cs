// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Application service that registers a principal's Web Push subscription (CORE-PUSH-001). It is the
/// idempotent upsert behind <c>POST /api/v1/me/push-subscriptions</c>: a re-registration of the SAME browser
/// endpoint by the same principal REFRESHES the existing row's encryption keys (a browser may rotate
/// <c>p256dh</c>/<c>auth</c>) rather than creating a duplicate, and a first registration of a new endpoint
/// creates the row — so the (user, endpoint) natural key the unique index enforces is honoured without the
/// endpoint having to round-trip a duplicate-key failure.
///
/// It is scoped ENTIRELY to the resolved caller's user-profile id (the endpoint resolves it server-side from
/// the authenticated principal), so a caller can only ever register a subscription for ITSELF — never another
/// principal (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class PushSubscriptionRegistrationService
{
    private readonly IPushSubscriptionRepository _repository;
    private readonly TimeProvider _timeProvider;

    public PushSubscriptionRegistrationService(IPushSubscriptionRepository repository, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Registers (or refreshes) the caller's subscription for the given endpoint and keys, returning the stored
    /// <see cref="PushSubscription"/>. The endpoint and keys are validated by the aggregate factory; an invalid
    /// value throws <see cref="ArgumentException"/> (the endpoint validates the request shape first and returns
    /// 400, so this is defence in depth).
    /// </summary>
    /// <exception cref="ArgumentException">The user id is empty, or the endpoint/keys violate their invariants.</exception>
    public async Task<PushSubscription> RegisterAsync(
        Guid userProfileId,
        string endpoint,
        string p256dh,
        string auth,
        CancellationToken cancellationToken)
    {
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("User profile id must not be empty.", nameof(userProfileId));
        }

        var now = _timeProvider.GetUtcNow();

        var existing = await _repository
            .FindByUserAndEndpointAsync(userProfileId, endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // Same browser endpoint, possibly rotated keys: refresh in place so the principal keeps one row per
            // endpoint. The owning principal and the endpoint never change (a subscription is never reassigned).
            existing.RefreshKeys(p256dh, auth, now);
            await _repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var created = PushSubscription.Register(userProfileId, endpoint, p256dh, auth, now);
        await _repository.AddAsync(created, cancellationToken).ConfigureAwait(false);
        return created;
    }
}
