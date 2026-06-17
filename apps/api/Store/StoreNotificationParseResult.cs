// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Whether a raw store notification was parsed into an actionable notification, safely ignored, or rejected
/// (CORE-STORE-005).
/// </summary>
public enum StoreNotificationParseStatus
{
    /// <summary>
    /// The payload was authentic and carried an ACTIONABLE notification (a renewal, cancellation, refund or grace
    /// period); the normalized <see cref="StoreNotification"/> is carried on the result.
    /// </summary>
    Parsed = 1,

    /// <summary>
    /// The payload was authentic but is NOT actionable for Core (a notification kind that does not map to a
    /// purchase lifecycle transition — for example a renewal-preference change or a test ping). It is
    /// acknowledged to the provider so it stops retrying, but nothing is recorded and no purchase is changed.
    /// </summary>
    Ignored = 2,

    /// <summary>
    /// The payload could NOT be trusted — an invalid/missing signature, an unexpected source, or an unparseable
    /// body — so it is rejected fail-closed. A forged or replayed notification can never change a purchase
    /// ("Must validate signature/idempotency" / "Must validate source/idempotency",
    /// csv/mobile_store_api_routes.csv).
    /// </summary>
    Rejected = 3,
}

/// <summary>
/// The provider-neutral OUTCOME of validating and parsing a raw store notification (CORE-STORE-005) — what an
/// <see cref="IStoreNotificationParser"/> hands back. It is exactly one of three shapes: a
/// <see cref="StoreNotificationParseStatus.Parsed"/> result carrying the normalized
/// <see cref="StoreNotification"/>, a <see cref="StoreNotificationParseStatus.Ignored"/> result (authentic but
/// not actionable, no notification), or a <see cref="StoreNotificationParseStatus.Rejected"/> result carrying a
/// generic, log-safe <see cref="RejectionReason"/> and no notification. The factory methods make the shapes
/// mutually exclusive by construction, so a caller can never read a <see cref="Notification"/> off a
/// rejected/ignored result (fail-closed; mirrors <see cref="PurchaseVerificationResult"/>).
///
/// Like the rest of the abstraction this is provider-neutral: every provider adapter reduces its own raw payload
/// to this one shape, so Core domain logic branches on a single result type and never on an Apple- or
/// Google-specific payload. The <see cref="RejectionReason"/> is a GENERIC, client-safe phrase (e.g. "invalid or
/// unverifiable notification") — never the raw provider error, the payload, or any receipt content — so a denial
/// can be surfaced and logged without leaking sensitive material (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class StoreNotificationParseResult
{
    private StoreNotificationParseResult(
        StoreNotificationParseStatus status,
        StoreNotification? notification,
        string? rejectionReason)
    {
        Status = status;
        Notification = notification;
        RejectionReason = rejectionReason;
    }

    /// <summary>Whether the payload was parsed, ignored or rejected.</summary>
    public StoreNotificationParseStatus Status { get; }

    /// <summary>
    /// The normalized notification when <see cref="Status"/> is <see cref="StoreNotificationParseStatus.Parsed"/>;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public StoreNotification? Notification { get; }

    /// <summary>
    /// A generic, client-safe and log-safe reason when <see cref="Status"/> is
    /// <see cref="StoreNotificationParseStatus.Rejected"/>; otherwise <see langword="null"/>. Never carries the raw
    /// provider error, the payload or receipt content (threat T7).
    /// </summary>
    public string? RejectionReason { get; }

    /// <summary>Whether the payload was parsed into an actionable notification (a convenience over <see cref="Status"/>).</summary>
    public bool IsParsed => Status == StoreNotificationParseStatus.Parsed;

    /// <summary>A parsed result carrying the normalized, actionable <see cref="StoreNotification"/>.</summary>
    /// <param name="notification">The normalized notification.</param>
    /// <exception cref="ArgumentNullException">The notification is null.</exception>
    public static StoreNotificationParseResult Parsed(StoreNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return new StoreNotificationParseResult(StoreNotificationParseStatus.Parsed, notification, rejectionReason: null);
    }

    /// <summary>
    /// An ignored result: the payload was authentic but is not actionable for Core, so nothing is recorded or
    /// changed (the notification is acknowledged to the provider).
    /// </summary>
    public static StoreNotificationParseResult Ignored()
        => new(StoreNotificationParseStatus.Ignored, notification: null, rejectionReason: null);

    /// <summary>
    /// A rejected result carrying a generic, client-safe reason and no notification. The payload could not be
    /// trusted, so no purchase is changed (fail-closed).
    /// </summary>
    /// <param name="reason">A generic, log-safe reason (never the raw provider error, the payload or receipt content).</param>
    /// <exception cref="ArgumentException">The reason is blank.</exception>
    public static StoreNotificationParseResult Rejected(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason must be provided.", nameof(reason));
        }

        return new StoreNotificationParseResult(StoreNotificationParseStatus.Rejected, notification: null, reason.Trim());
    }

    /// <summary>
    /// Log-safe representation: the status, plus the parsed notification identifiers or the generic rejection
    /// reason. Every branch carries only identifiers / a generic phrase, never the raw payload or receipt content
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => Status switch
        {
            StoreNotificationParseStatus.Parsed => $"StoreNotificationParseResult status={Status} notification=[{Notification}]",
            StoreNotificationParseStatus.Rejected => $"StoreNotificationParseResult status={Status} reason={RejectionReason}",
            _ => $"StoreNotificationParseResult status={Status}",
        };
}
