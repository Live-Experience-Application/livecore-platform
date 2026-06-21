// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// One pending CONTENT-FREE closed-app Web Push delivery (CORE-PUSH-002, the "Closed-App Push Notifications"
/// epic, raised by a vertical adopter — ARC-GAP-005, the delivery). It is the durable hand-off — an outbox row —
/// that the API writes AFTER an already-authorized, recipient-filtered session event commits (commit-then-publish,
/// CORE-CONC-002) and the worker drains by sending a content-free push to the recipient's registered subscriptions
/// (csv/database_tables.csv: table <c>push_notification_deliveries</c>).
///
/// WHY AN OUTBOX. The recipient set is resolved at PUBLISH time by reusing the SAME recipient resolution the
/// in-app realtime delivery uses (<see cref="ISessionEventPushRecipientResolver"/>, gated through the central
/// Visibility engine), so the push reaches EXACTLY the audience the in-app event reaches — captured at the instant
/// the event was delivered, never recomputed later (so it cannot drift). The slow OUTBOUND HTTP send is deferred to
/// the worker (docs/02_ARCHITECTURE.md: the worker owns async jobs), so a failed or slow push can never block the
/// in-session realtime delivery (the story's requirement). The row is the worker's to-do item, not the message: it
/// records only the SIGNAL the recipient is owed.
///
/// CONTENT-FREE BY DESIGN (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). The row carries only IDENTIFIERS —
/// the tenant, the session, the source <see cref="SessionEventId"/> and the recipient — never any projected title,
/// body or content. Nothing sensitive is stored here and nothing sensitive is ever sent: the delivered push is a
/// content-free signal to open the app and fetch the event through the authorized, visibility-gated channel
/// (CORE-RT-005), so nothing can surface on a lock screen.
///
/// TENANT + LIFECYCLE. The row is tenant-scoped (<see cref="OrganizationId"/>) and hangs off the recipient's
/// <c>users(id)</c> profile (<see cref="RecipientUserProfileId"/>); both foreign keys are <c>ON DELETE CASCADE</c>,
/// so a tenant teardown (CORE-PRIV-002) and a data-subject erasure (CORE-PRIV-001) drop a subject's pending pushes
/// automatically. The row is transient: the worker DELETES it once it has attempted delivery (best-effort, like the
/// realtime transport — a missed signal is recovered when the client next opens the app and replays).
/// </summary>
public sealed class PushNotificationDelivery
{
    private PushNotificationDelivery(
        Guid id,
        Guid organizationId,
        Guid sessionId,
        Guid sessionEventId,
        Guid recipientUserProfileId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Push delivery id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (sessionEventId == Guid.Empty)
        {
            throw new ArgumentException("Session event id must not be empty.", nameof(sessionEventId));
        }

        if (recipientUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Recipient user profile id must not be empty.", nameof(recipientUserProfileId));
        }

        Id = id;
        OrganizationId = organizationId;
        SessionId = sessionId;
        SessionEventId = sessionEventId;
        RecipientUserProfileId = recipientUserProfileId;
        // Normalized to UTC so the persisted timestamptz value is offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PushNotificationDelivery()
    {
    }

    /// <summary>Surrogate key of the outbox row (UUID version 7, time-ordered so the FIFO drain is chronological).</summary>
    public Guid Id { get; }

    /// <summary>Tenant boundary of the delivery (the <c>organization_id</c> foreign key to <c>organizations</c>).</summary>
    public Guid OrganizationId { get; }

    /// <summary>The session whose event triggered the push (an identifier; never any session content).</summary>
    public Guid SessionId { get; }

    /// <summary>
    /// The source session event the recipient is being signalled about (an identifier only — the worker sends it
    /// as the content-free signal so the client can fetch the event through the authorized channel; never content).
    /// </summary>
    public Guid SessionEventId { get; }

    /// <summary>
    /// The recipient's user-profile id (the <c>user_id</c> foreign key to <c>users</c>). The worker resolves this
    /// principal's registered push subscriptions at dispatch time and delivers the content-free signal to each.
    /// </summary>
    public Guid RecipientUserProfileId { get; }

    /// <summary>When this delivery was enqueued (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Enqueues a new content-free push delivery for one recipient of an already-authorized session event. The
    /// caller (the publish-time fan-out) has already resolved the recipient through the central recipient
    /// resolution, so this only records the signal; it carries no projected content (threats T2/T7).
    /// </summary>
    /// <exception cref="ArgumentException">A required identifier is empty.</exception>
    public static PushNotificationDelivery Create(
        Guid organizationId,
        Guid sessionId,
        Guid sessionEventId,
        Guid recipientUserProfileId,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(createdAt),
            organizationId,
            sessionId,
            sessionEventId,
            recipientUserProfileId,
            createdAt);

    /// <summary>
    /// Identifier-only representation safe for structured logs: the delivery id, tenant, session, source event and
    /// recipient. Every field is already an identifier, so there is no content to exclude (threat T7).
    /// </summary>
    public override string ToString()
        => $"PushNotificationDelivery {Id} org={OrganizationId} session={SessionId} "
            + $"event={SessionEventId} recipient={RecipientUserProfileId}";
}
