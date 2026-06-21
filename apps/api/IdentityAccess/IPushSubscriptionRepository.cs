// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Persistence contract for the per-principal Web Push subscription store (CORE-PUSH-001). The IdentityAccess
/// module owns the <c>push_subscriptions</c> table; other modules access subscriptions only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations).
///
/// Every lookup, update and delete is SCOPED to the owning principal's user-profile id, so a caller can only
/// ever address its OWN subscriptions — a subscription is never reachable across principals (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The registration upsert keys on the (user, endpoint) natural key the
/// unique index enforces.
/// </summary>
public interface IPushSubscriptionRepository
{
    /// <summary>
    /// Finds the caller's subscription for exactly the given endpoint, or <see langword="null"/> when none
    /// exists. Scoped to <paramref name="userProfileId"/>, so it never returns another principal's row even if
    /// two principals registered the same endpoint string. Backs the registration upsert (refresh-or-create).
    /// </summary>
    Task<PushSubscription?> FindByUserAndEndpointAsync(
        Guid userProfileId,
        string endpoint,
        CancellationToken cancellationToken);

    /// <summary>Persists a new subscription. The unique (user, endpoint) index rejects a duplicate.</summary>
    Task AddAsync(PushSubscription subscription, CancellationToken cancellationToken);

    /// <summary>Persists a refreshed subscription (its rotated keys) previously loaded through this repository.</summary>
    Task UpdateAsync(PushSubscription subscription, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the subscription identified by <paramref name="subscriptionId"/> ONLY when it belongs to
    /// <paramref name="userProfileId"/>, and returns whether a row was removed. The delete predicate leads with
    /// the owning principal, so a caller can NEVER delete another principal's subscription: a foreign or unknown
    /// id removes nothing and returns <see langword="false"/> (the endpoint maps that to an indistinguishable
    /// hidden 404, fail-closed; threats T1/T5).
    /// </summary>
    Task<bool> DeleteByIdForUserAsync(Guid subscriptionId, Guid userProfileId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all of a principal's subscriptions, oldest first, for the data-subject access/portability export
    /// (CORE-PRIV-004). Scoped to <paramref name="userProfileId"/> — the subject's own per-device personal data.
    /// </summary>
    Task<IReadOnlyList<PushSubscription>> ListByUserAsync(Guid userProfileId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns, from the given candidate principals, the DISTINCT subset that have at least one registered push
    /// subscription (CORE-PUSH-002). The closed-app push fan-out uses this to enqueue a content-free delivery only
    /// for a recipient that actually has a registered subscription — "to each recipient WITH a registered
    /// subscription" — so the outbox never fills with rows for unsubscribed recipients. It reads only the user-id
    /// column (never an endpoint or the auth secret; threat T7) and returns an empty set for an empty candidate
    /// list. Empty candidate ids are ignored (they can never address a stored subscription).
    /// </summary>
    /// <exception cref="ArgumentNullException">The candidate collection is null.</exception>
    Task<IReadOnlyList<Guid>> ListUserIdsWithSubscriptionsAsync(
        IReadOnlyCollection<Guid> userProfileIds,
        CancellationToken cancellationToken);
}
