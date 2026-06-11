namespace LiveCore.Api.Participants;

/// <summary>
/// Lifecycle status of a <see cref="Participant"/> (CORE-SES-001). A participant
/// is a long-lived, session-facing record (docs/03_DOMAIN_LANGUAGE.md:
/// "Session-facing person or role"; docs/05_MODULE_CONTRACTS.md: the Participants
/// module owns "session/workspace participant records"), so it is removed by a
/// status transition rather than a hard delete (docs/10_DATABASE_SCHEMA.md:
/// "avoid hard-delete for business data; use soft delete where needed"). The
/// status preserves the record (and any later session events that reference it)
/// while denying it standing once it leaves a workspace.
///
/// The status is persisted by its stable name (not its numeric value), so the
/// integers below are only in-memory storage discriminators and carry no
/// ordering meaning; they must not be compared with &gt;/&lt;.
/// </summary>
public enum ParticipantStatus
{
    /// <summary>
    /// The participant is active in its workspace. This is the only status in
    /// which the participant currently holds standing; later session-join and
    /// feed stories (CORE-SES-003/005) build on an active participant.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The participant has been removed from its workspace (a soft delete). The
    /// record is retained for history and referential integrity, but a removed
    /// participant holds no standing and is treated as gone; re-admission is a
    /// later-story concern and is deliberately not modelled here.
    /// </summary>
    Removed = 2,
}
