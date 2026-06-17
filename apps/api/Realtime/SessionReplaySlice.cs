// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// One BOUNDED, CURSORED page of a reconnecting caller's recipient-safe replay (CORE-PERF-002), produced by
/// <see cref="SessionReplayService.ReplayAsync"/>. The replay reads only the post-cursor rows up to a fixed
/// cap (<see cref="SessionReplayService.MaxReplayEvents"/>), so a reconnect can never load an unbounded
/// stream (threat T9 abuse/DoS in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Events">
/// The recipient-safe envelopes the caller is entitled to within this page, in append (sequence) order —
/// already gated and projected by the central Visibility engine through the recipient resolver, so nothing
/// appears here that live delivery would have withheld (threat T3). Empty when the caller may see nothing in
/// the page.
/// </param>
/// <param name="NextSequence">
/// The cursor to pass back as <c>afterSequence</c> to fetch the NEXT page, or <see langword="null"/> when the
/// caller has caught up (the page was not full, so no more events exist after it). It is the highest RAW
/// per-session sequence in this page — independent of which events the caller may see — so a client always
/// pages FORWARD even across a full page that happens to contain no event it is entitled to: the cap never
/// silently drops unacknowledged events. The client deduplicates already-seen events (docs/11_REALTIME_SYNC.md
/// "duplicate event handling"). A sequence number is not sensitive content (threat T7): the host already sees
/// every sequence, and a participant already detects gaps by design.
/// </param>
internal sealed record SessionReplaySlice(
    IReadOnlyList<SessionEventEnvelope> Events,
    long? NextSequence)
{
    /// <summary>The empty page: nothing to replay and nothing more to page to.</summary>
    public static SessionReplaySlice Empty { get; } = new([], null);
}
