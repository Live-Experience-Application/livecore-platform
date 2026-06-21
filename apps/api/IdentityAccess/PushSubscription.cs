// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// A browser Web Push subscription registered by a principal (CORE-PUSH-001, the "Closed-App Push
/// Notifications" epic). It is the Core-owned, per-principal record of a single push channel a user's
/// browser handed Core so a later, content-free delivery (CORE-PUSH-002) can reach that user while the app
/// is closed (csv/database_tables.csv: table <c>push_subscriptions</c>). It lives in the IdentityAccess
/// module because it is scoped to — and authorized by — the authenticated principal, exactly where the user
/// profile and the principal authorization live, so a vertical adopter need not duplicate Core authorization
/// (the story's "the subscription store must live where the principal and authorization live").
///
/// IDENTITY / SCOPE invariant (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). A subscription belongs to
/// exactly one <see cref="UserProfileId"/> (the registering principal) and is unique per
/// (<see cref="UserProfileId"/>, <see cref="Endpoint"/>): the same browser endpoint re-registered by the same
/// principal updates the existing row's keys rather than creating a duplicate, and a subscription is NEVER
/// addressable across principals (a caller can only ever register, read or delete its OWN). The
/// <c>push_subscriptions.user_id</c> foreign key into <c>users(id)</c> is <c>ON DELETE CASCADE</c>, so the
/// data-subject erasure (CORE-PRIV-001) removes a subject's subscriptions automatically — the subscription is
/// per-principal personal data.
///
/// PERSONAL DATA, NOT CONTENT. The fields are the W3C Push API subscription a browser produces: the push
/// service <see cref="Endpoint"/> URL plus the client's <see cref="P256dh"/> public key and <see cref="Auth"/>
/// secret used to encrypt a future push payload. They are the subject's own per-device personal data (deleted
/// on erasure, included in the user-data export); the <see cref="Auth"/> encryption secret is never echoed back
/// to a client and never logged (threat T7). This type stores no projected session content — closed-app
/// delivery (CORE-PUSH-002) is content-free by design.
/// </summary>
public sealed class PushSubscription
{
    /// <summary>Maximum stored length of a push service endpoint URL.</summary>
    public const int MaxEndpointLength = 2048;

    /// <summary>Maximum stored length of a subscription key (the client public key or auth secret).</summary>
    public const int MaxKeyLength = 255;

    private PushSubscription(
        Guid id,
        Guid userProfileId,
        string endpoint,
        string p256dh,
        string auth,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Subscription id must not be empty.", nameof(id));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("User profile id must not be empty.", nameof(userProfileId));
        }

        if (!IsValidEndpoint(endpoint))
        {
            throw new ArgumentException("Endpoint violates the push endpoint invariants.", nameof(endpoint));
        }

        if (!IsValidKey(p256dh))
        {
            throw new ArgumentException("p256dh violates the subscription key invariants.", nameof(p256dh));
        }

        if (!IsValidKey(auth))
        {
            throw new ArgumentException("auth violates the subscription key invariants.", nameof(auth));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException("A subscription cannot be updated before it was created.", nameof(updatedAt));
        }

        Id = id;
        UserProfileId = userProfileId;
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PushSubscription()
    {
        Endpoint = null!;
        P256dh = null!;
        Auth = null!;
    }

    /// <summary>Surrogate key of the subscription row (UUID version 7, time-ordered).</summary>
    public Guid Id { get; }

    /// <summary>The principal (user profile id) the subscription is scoped to. Immutable.</summary>
    public Guid UserProfileId { get; }

    /// <summary>
    /// The push service endpoint URL the browser registered. Part of the per-principal natural key
    /// (<see cref="UserProfileId"/>, <see cref="Endpoint"/>); immutable for the lifetime of the row.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>The client's P-256 ECDH public key (base64url), used to encrypt a future push payload.</summary>
    public string P256dh { get; private set; }

    /// <summary>
    /// The client's auth secret (base64url), used to encrypt a future push payload. A secret: never echoed
    /// back to a client and never logged (threat T7).
    /// </summary>
    public string Auth { get; private set; }

    /// <summary>When this subscription was first registered (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this subscription's keys were last refreshed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Registers a new push subscription for the given principal. The endpoint and keys are validated; the
    /// (<paramref name="userProfileId"/>, <paramref name="endpoint"/>) pair is the natural key the database
    /// enforces unique, so two registrations of the same browser endpoint by the same principal can never
    /// create two rows.
    /// </summary>
    /// <exception cref="ArgumentException">An identifier, the endpoint or a key violates its invariant.</exception>
    public static PushSubscription Register(
        Guid userProfileId,
        string endpoint,
        string p256dh,
        string auth,
        DateTimeOffset registeredAt)
        => new(Guid.CreateVersion7(), userProfileId, endpoint, p256dh, auth, registeredAt, registeredAt);

    /// <summary>
    /// Refreshes the subscription's encryption keys (the browser can rotate <see cref="P256dh"/>/<see cref="Auth"/>
    /// while keeping the same endpoint), mirroring a re-registration of the same endpoint. The endpoint and the
    /// owning principal never change — a subscription is never reassigned across principals (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">A key violates its invariant.</exception>
    public void RefreshKeys(string p256dh, string auth, DateTimeOffset updatedAt)
    {
        if (!IsValidKey(p256dh))
        {
            throw new ArgumentException("p256dh violates the subscription key invariants.", nameof(p256dh));
        }

        if (!IsValidKey(auth))
        {
            throw new ArgumentException("auth violates the subscription key invariants.", nameof(auth));
        }

        P256dh = p256dh;
        Auth = auth;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the value is a valid push service endpoint: a non-blank, bounded, absolute http/https URL. A
    /// near-match or a non-absolute value can never address a real push service, so it is rejected at the edge.
    /// </summary>
    public static bool IsValidEndpoint(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxEndpointLength
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Whether the value is a valid subscription key: non-blank and within <see cref="MaxKeyLength"/>. The
    /// content is opaque base64url crypto material the browser produced; only presence and bound are enforced.
    /// </summary>
    public static bool IsValidKey(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxKeyLength;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the subscription id and owning
    /// principal. The endpoint and the encryption keys are deliberately excluded (threat T7: logs carry
    /// identifiers, never the push channel or its secret).
    /// </summary>
    public override string ToString() => $"PushSubscription {Id} user={UserProfileId}";
}
