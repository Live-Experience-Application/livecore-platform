// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The calling principal's OWN session participant context (CORE-PSELF-001, the "Participant
/// Self-Identification" epic), returned by <c>GET /api/v1/sessions/{sessionId}/me</c>. It lets an audience
/// surface learn its OWN surrogate participant id IN-SCOPE so it can then call the participant-keyed reads
/// (<c>getParticipantVisibleFeed</c>, the roster) for itself — without the host having to pass the surrogate
/// participant id out of band. The principal is mapped to its participant ENTIRELY server-side, via the existing
/// principal-to-participant mapping (<see cref="Participants.IParticipantRepository.FindByUserAsync"/>), never a
/// client-supplied id, so a caller can only ever resolve ITSELF (raised by a vertical adopter, ARC-GAP-007).
///
/// AUDIENCE-SAFE / SELF-ONLY by construction (docs/08_API_CONTRACTS.md DTO design rules; threats T2/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>It carries ONLY the caller's own participant identity — the surrogate
///   <see cref="ParticipantId"/>, the session-facing <see cref="DisplayName"/> and the
///   <see cref="Present"/> presence flag — and NEVER another participant, nor any host-only field (the
///   participant's user-account link is absent; an audience member never learns which user backs any participant,
///   not even itself, because it already knows it is itself).</item>
///   <item>It carries NO internal authorization rationale and no reason for a denial — a caller who is not a
///   participant of the session is hidden as <c>404</c>, never told why (threat T7).</item>
/// </list>
/// </summary>
/// <param name="SessionId">The session this participant context is scoped to (echoed for correlation).</param>
/// <param name="ParticipantId">
/// The caller's OWN surrogate participant id in this session (UUIDv7) — the value participant-keyed reads take.
/// </param>
/// <param name="DisplayName">The caller's own session-facing display identity (audience-facing metadata).</param>
/// <param name="Present">
/// Whether the caller currently holds at least one live realtime connection to this session on this API
/// instance (the presence signal; docs/11_REALTIME_SYNC.md). The same per-instance, never-over-reported signal
/// the roster's presence flag uses.
/// </param>
public sealed record SessionParticipantContext(
    Guid SessionId,
    Guid ParticipantId,
    string DisplayName,
    bool Present);
