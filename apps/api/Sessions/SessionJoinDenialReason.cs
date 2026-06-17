// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Sessions;

/// <summary>
/// Typed reason why a participant could not be admitted to a session by the
/// <see cref="SessionParticipantJoinService"/> (CORE-SES-003).
///
/// The join service is fail-closed: it admits a participant only when every check
/// passes, and otherwise returns one of these denials, never a partial admission
/// (docs/06_AUTHORIZATION_MATRIX.md: "Role checks are not enough; object-level
/// authorization is required"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). A
/// session in another tenant or workspace, an unknown session or participant, a
/// removed participant or an ended session can never produce an admission.
///
/// Values are deliberately identifier-only: they can be logged and translated into
/// a 403/404 response without ever echoing another tenant's data or any free-form
/// metadata (threat T7 in docs/07_SECURITY_THREAT_MODEL.md; "never leak sensitive
/// content in errors" in docs/02_ARCHITECTURE.md). The two not-found reasons
/// deliberately hide existence: a session or participant that lives in another
/// organization or workspace is reported as "not found", never as "forbidden", so
/// the caller cannot probe for the existence of resources outside its tenant
/// (threat T1/T5). Callers decide whether a particular denial maps to "not found"
/// or "forbidden"; the service only reports why admission was denied.
/// </summary>
public enum SessionJoinDenialReason
{
    /// <summary>Admission succeeded; no denial.</summary>
    None = 0,

    /// <summary>
    /// No session with the requested id exists within the requested organization
    /// and workspace. This also covers a session that exists only under another
    /// organization or workspace: cross-tenant and cross-workspace access is hidden
    /// as not-found, never leaked as a different outcome (threat T1/T5).
    /// </summary>
    SessionNotFound = 1,

    /// <summary>
    /// No participant with the requested id exists within the requested
    /// organization and workspace. This also covers a participant that exists only
    /// under another organization or workspace: cross-tenant and cross-workspace
    /// access is hidden as not-found, never leaked (threat T1/T5).
    /// </summary>
    ParticipantNotFound = 2,

    /// <summary>
    /// The participant exists in the workspace but holds no standing: it has been
    /// removed (<see cref="Participants.ParticipantStatus.Removed"/>), so it may not
    /// join a session. A removed participant is a soft delete that retains the record
    /// for history while denying it standing (CORE-SES-001).
    /// </summary>
    ParticipantNotActive = 3,

    /// <summary>
    /// The session exists in the workspace but is not joinable: it has ended
    /// (<see cref="SessionStatus.Ended"/>), so its live timeline is closed and no
    /// participant may join it. A session is joinable only while it is
    /// <see cref="SessionStatus.Prepared"/> or <see cref="SessionStatus.Live"/>
    /// (CORE-SES-002).
    /// </summary>
    SessionNotJoinable = 4,

    /// <summary>
    /// The participant could otherwise join, but the session is already at its plan
    /// participant limit — the server-enforced <c>session.participant.max</c> quota
    /// (CORE-MON-005; the documented free-tier participant cap,
    /// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). Unlike the not-found
    /// reasons this does NOT hide existence: the caller is authorized and the session
    /// and participant are real and in scope, but admitting one more participant would
    /// exceed the cap, so the join is refused fail-closed. The atomic check-and-consume
    /// (CORE-CONC-004) makes this hold even under concurrent joins, so the cap can never
    /// be overrun. A caller maps it to a quota-exceeded outcome (a 409 conflict, like the
    /// session start/workspace create quota gate) rather than a 403/404 — the limit, not
    /// the caller, is the reason. The decision carries only the generic quota key,
    /// never another tenant's data (threat T7).
    /// </summary>
    QuotaExceeded = 5,
}
