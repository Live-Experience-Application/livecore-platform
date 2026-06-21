// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Resolves the RECIPIENT USERS of a session event for the closed-app push fan-out (CORE-PUSH-002) — the set of
/// user-profile ids that the in-app realtime delivery of the same event reaches, so a content-free push can be
/// fanned out to EXACTLY that audience and never wider (the story's "the push reaches exactly the audience the
/// in-app event reaches").
///
/// It REUSES the SAME central Visibility gate (<see cref="LiveCore.Api.Visibility.IEventRecipientVisibility"/>) and
/// the SAME audience population (the session's active participants) the realtime
/// <see cref="SessionEventRecipientResolver"/> uses, and mirrors that resolver's routing decisions exactly — so the
/// push audience can never diverge from, or exceed, the realtime audience (threats T2/T3 in
/// docs/07_SECURITY_THREAT_MODEL.md). Visibility is NEVER recomputed here; it is delegated to the central engine.
/// The realtime resolver delivers an audience-wide event to a shared group without enumerating its members
/// (CORE-PERF-001); push has no shared channel, so this resolver must enumerate the recipient PARTICIPANTS and map
/// them to their linked users — the inherent cost of an outbound per-recipient fan-out.
/// </summary>
internal interface ISessionEventPushRecipientResolver
{
    /// <summary>
    /// Resolves the DISTINCT user-profile ids that should receive a closed-app push for the given event: the same
    /// audience the in-app event reaches, mapped to the recipient participants' linked users. An anonymous
    /// participant (no user link) cannot be pushed and is omitted; a host-only event has no participant audience
    /// and yields none; a recipient the central Visibility engine does not authorize is never included
    /// (fail-closed).
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task<IReadOnlyList<Guid>> ResolveRecipientUserIdsAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken);
}
