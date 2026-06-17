// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Acknowledgement body returned to a store for a received notification (CORE-STORE-005, the store notification
/// endpoints <c>POST /api/v1/store-notifications/apple</c> and <c>POST /api/v1/store-notifications/google/rtdn</c>).
/// A store delivers notifications server-to-server and retries on any non-success response, so the endpoint
/// acknowledges every notification it has safely accounted for (handled, deduplicated or ignored) with
/// <c>200 OK</c> and this tiny body, so the store stops re-delivering it.
///
/// It carries only the generic processing <see cref="Outcome"/> NAME — never the raw payload, any receipt content
/// or any internal identifier — so the acknowledgement is log-safe and leaks nothing (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The recipient is the store's own infrastructure, not an end user.
/// </summary>
/// <param name="Outcome">
/// What the endpoint did with the notification: <c>Applied</c> (a purchase status change), <c>Unchanged</c> (the
/// purchase was already in that status), <c>AlreadyProcessed</c> (a duplicate notification), <c>TransactionNotFound</c>
/// (no purchase recorded for it) or <c>Ignored</c> (an authentic but non-actionable notification).
/// </param>
public sealed record StoreNotificationAck(string Outcome)
{
    /// <summary>The acknowledgement for an authentic but non-actionable notification.</summary>
    public static StoreNotificationAck Ignored()
        => new(nameof(StoreNotificationParseStatus.Ignored));

    /// <summary>Projects a handled notification's outcome into the acknowledgement body.</summary>
    public static StoreNotificationAck From(StoreNotificationProcessingOutcome outcome)
        => new(outcome.ToString());
}
