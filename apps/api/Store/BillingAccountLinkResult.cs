// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of linking a verified purchase to a buyer subject through
/// <see cref="BillingAccountLinkService.LinkBuyerAsync"/> (CORE-MON-002).
/// </summary>
public enum BillingAccountLinkOutcome
{
    /// <summary>The purchase was newly linked to the buyer subject (a first-time link).</summary>
    Linked = 1,

    /// <summary>
    /// The purchase was already linked to THIS buyer subject; nothing was persisted (the idempotent retry path — the
    /// same buyer re-submitting their own receipt).
    /// </summary>
    AlreadyLinked = 2,

    /// <summary>
    /// The purchase is already linked to a DIFFERENT subject, so the requested buyer cannot claim it; nothing was
    /// persisted (fail-closed — the same external receipt cannot be claimed by two different subjects; the
    /// acceptance criterion "user B cannot bind user A's transaction/receipt").
    /// </summary>
    ConflictDifferentSubject = 3,
}

/// <summary>
/// Result of linking a verified purchase to a buyer subject (CORE-MON-002). Beyond the <see cref="Outcome"/> it
/// carries the persisted (or already-existing) <see cref="Link"/>, so a caller can act on the recorded link
/// without a second lookup. On a <see cref="BillingAccountLinkOutcome.ConflictDifferentSubject"/> the link is the
/// EXISTING link held by the other subject (the caller must not expose its subject — the conflict is reported
/// generically; threat T5/T7).
/// </summary>
/// <param name="Outcome">Whether the purchase was newly linked, already linked to this buyer, or claimed by another subject.</param>
/// <param name="Link">The persisted or already-existing billing account link.</param>
public sealed record BillingAccountLinkResult(BillingAccountLinkOutcome Outcome, BillingAccountLink Link);
