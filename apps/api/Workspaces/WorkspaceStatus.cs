namespace LiveCore.Api.Workspaces;

/// <summary>
/// Lifecycle status of a <see cref="Workspace"/> (CORE-LIFE-009, the "Resource
/// Lifecycle and Deletion" epic). A workspace previously had create/read/update
/// but no lifecycle end-state; this adds a soft, audited archive status so an
/// owner can take a workspace out of active use without a hard delete that would
/// orphan its child sessions, scenes, content and audit trail.
///
/// SOFT ARCHIVE, NOT HARD DELETE (the decision recorded for this story). Archiving
/// keeps the workspace row and ALL of its children intact — it only flips this
/// status — because a workspace owns sessions, scenes, content blocks, entities,
/// assets and an append-only audit trail whose history must survive. A status is
/// reversible in principle, but Core deliberately models archive as a CLEARLY
/// TERMINAL end-state here: there is no un-archive command in this story (a
/// re-activate, if ever needed, is a separate, explicitly-scoped story), so the
/// only transition is <see cref="Active"/> -&gt; <see cref="Archived"/>
/// (<see cref="Workspace.Archive"/>).
///
/// The status is persisted by its stable NAME (not its numeric value), exactly
/// like <c>SessionStatus</c>, <c>ParticipantStatus</c> and every other enum in the
/// model, so the integers below are only in-memory storage discriminators: they
/// carry no ordering meaning and must not be compared with &gt;/&lt;. The legal
/// transition is expressed by the <see cref="Workspace"/> aggregate
/// (<see cref="Workspace.Archive"/>), never by integer order.
/// </summary>
public enum WorkspaceStatus
{
    /// <summary>
    /// The workspace is in active use: it accepts authoring mutations (rename,
    /// member invitations, session and scene creation) and appears in the active
    /// workspace list. This is the state a workspace is created in and the only
    /// state from which it may be archived.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The workspace has been archived (CORE-LIFE-009): it is read-only — its
    /// authoring mutations are rejected — and it is excluded from the active
    /// workspace list, while its children and audit trail are preserved. This is
    /// the terminal lifecycle state for this story; an archived workspace is never
    /// archived again (a re-archive is a no-op conflict) and there is no
    /// un-archive command.
    /// </summary>
    Archived = 2,
}
