// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Sessions;

namespace LiveCore.Api.UnitTests.Sessions;

/// <summary>
/// Unit tests for the <see cref="Session"/> invariants and its lifecycle state
/// machine (CORE-SES-002): a session is the generic "live or prepared run of a
/// workspace" (docs/03_DOMAIN_LANGUAGE.md); it is workspace-scoped and
/// tenant-scoped and belongs only to the workspace and organization it names; and
/// its status moves through the guarded Prepared -&gt; Live -&gt; Ended state
/// machine behind the persisted SessionCreated -&gt; SessionStarted -&gt;
/// SessionEnded events (docs/09_EVENT_CATALOG.md). The state machine is the heart
/// of the story: the negative-transition cases (the "Lifecycle tests and
/// authorization tests" required column, csv/core_epics_stories.csv) are exhaustive
/// and assert NO mutation happens on a rejected transition. ToString stays log-safe
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). No vertical-specific terms
/// appear anywhere — generic Session vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class SessionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endedAt = new(2026, 6, 11, 10, 30, 0, TimeSpan.Zero);

    private static Session CreatePrepared()
        => Session.Create(_organizationId, _workspaceId, "Opening Run", _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_starts_prepared_with_no_live_timeline()
    {
        var session = CreatePrepared();

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(_organizationId, session.OrganizationId);
        Assert.Equal(_workspaceId, session.WorkspaceId);
        Assert.Equal("Opening Run", session.Title);
        // A session begins prepared (the state behind SessionCreated): no live
        // timeline yet.
        Assert.Equal(SessionStatus.Prepared, session.Status);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.False(session.IsLive);
        Assert.Equal(_createdAt, session.CreatedAt);
        Assert.Equal(_createdAt, session.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreatePrepared();
        var second = CreatePrepared();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_title()
    {
        var session = Session.Create(_organizationId, _workspaceId, "  Trimmed Title  ", _createdAt);

        Assert.Equal("Trimmed Title", session.Title);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var session = Session.Create(_organizationId, _workspaceId, "Title", localCreatedAt);

        Assert.Equal(TimeSpan.Zero, session.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), session.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => Session.Create(Guid.Empty, _workspaceId, "Title", _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => Session.Create(_organizationId, Guid.Empty, "Title", _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_title(string? title)
        => Assert.Throws<ArgumentException>(
            () => Session.Create(_organizationId, _workspaceId, title!, _createdAt));

    [Fact]
    public void Create_rejects_a_title_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => Session.Create(_organizationId, _workspaceId, "bad\ttitle", _createdAt));

    [Fact]
    public void Create_rejects_a_title_over_the_length_bound()
    {
        var tooLong = new string('a', Session.MaxTitleLength + 1);

        Assert.Throws<ArgumentException>(
            () => Session.Create(_organizationId, _workspaceId, tooLong, _createdAt));
    }

    [Fact]
    public void IsValidStatus_rejects_an_undefined_status()
    {
        Assert.True(Session.IsValidStatus(SessionStatus.Prepared));
        Assert.True(Session.IsValidStatus(SessionStatus.Live));
        Assert.True(Session.IsValidStatus(SessionStatus.Ended));
        Assert.True(Session.IsValidStatus(SessionStatus.Cancelled));
        Assert.False(Session.IsValidStatus((SessionStatus)999));
    }

    // --- Tenant / workspace boundary -------------------------------------------

    [Fact]
    public void Session_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a session belongs only to the
        // organization it names. The organization boundary is checked before the
        // workspace boundary.
        var session = CreatePrepared();

        Assert.True(session.BelongsToOrganization(_organizationId));
        Assert.False(session.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(session.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Session_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): a session in workspace W1 is not in
        // workspace W2.
        var session = CreatePrepared();

        Assert.True(session.BelongsToWorkspace(_workspaceId));
        Assert.False(session.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(session.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A session is the one identified by its id only WITHIN its tenant and
        // workspace: the surrogate id is never honored across a foreign tenant or
        // workspace (threat T1/T5).
        var session = CreatePrepared();

        Assert.True(session.Identifies(_organizationId, _workspaceId, session.Id));
        Assert.False(session.Identifies(_foreignOrganizationId, _workspaceId, session.Id));
        Assert.False(session.Identifies(_organizationId, _foreignWorkspaceId, session.Id));
        Assert.False(session.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(session.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    [Fact]
    public void Rename_changes_only_the_title_and_timestamp()
    {
        var session = CreatePrepared();

        session.Rename("New Title", _startedAt);

        Assert.Equal("New Title", session.Title);
        Assert.Equal(_startedAt, session.UpdatedAt);
        Assert.Equal(_createdAt, session.CreatedAt);
        // The tenant boundary, the workspace and the status are immutable across a
        // rename (threat T5).
        Assert.Equal(_organizationId, session.OrganizationId);
        Assert.Equal(_workspaceId, session.WorkspaceId);
        Assert.Equal(SessionStatus.Prepared, session.Status);
    }

    [Fact]
    public void Rename_rejects_a_blank_title_and_leaves_the_session_untouched()
    {
        var session = CreatePrepared();

        Assert.Throws<ArgumentException>(() => session.Rename("   ", _startedAt));

        Assert.Equal("Opening Run", session.Title);
        Assert.Equal(_createdAt, session.UpdatedAt);
    }

    // --- State machine: HAPPY path ---------------------------------------------

    [Fact]
    public void Start_transitions_prepared_to_live_and_opens_the_timeline()
    {
        // Happy path (state behind SessionStarted, "starts live timeline"): the
        // session goes Prepared -> Live and StartedAt is stamped.
        var session = CreatePrepared();

        Assert.True(session.CanStart);
        session.Start(_startedAt);

        Assert.Equal(SessionStatus.Live, session.Status);
        Assert.True(session.IsLive);
        Assert.Equal(_startedAt, session.StartedAt);
        Assert.Equal(_startedAt, session.UpdatedAt);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public void End_transitions_live_to_ended_and_closes_the_timeline()
    {
        // Happy path (state behind SessionEnded, "ends live timeline"): a live
        // session goes Live -> Ended and EndedAt is stamped at or after StartedAt.
        var session = CreatePrepared();
        session.Start(_startedAt);

        Assert.True(session.CanEnd);
        session.End(_endedAt);

        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.False(session.IsLive);
        Assert.Equal(_startedAt, session.StartedAt);
        Assert.Equal(_endedAt, session.EndedAt);
        Assert.Equal(_endedAt, session.UpdatedAt);
        // Invariant: EndedAt >= StartedAt.
        Assert.True(session.EndedAt >= session.StartedAt);
    }

    [Fact]
    public void Full_lifecycle_normalizes_timeline_timestamps_to_utc()
    {
        var localStart = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.FromHours(2));
        var localEnd = new DateTimeOffset(2026, 6, 11, 13, 0, 0, TimeSpan.FromHours(2));
        var session = CreatePrepared();

        session.Start(localStart);
        session.End(localEnd);

        Assert.Equal(TimeSpan.Zero, session.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, session.EndedAt!.Value.Offset);
        Assert.Equal(localStart.ToUniversalTime(), session.StartedAt);
        Assert.Equal(localEnd.ToUniversalTime(), session.EndedAt);
    }

    [Fact]
    public void End_at_exactly_the_start_instant_is_allowed()
    {
        // The invariant is EndedAt >= StartedAt, so a zero-length live timeline
        // (end == start) is a well-formed boundary, not an error.
        var session = CreatePrepared();
        session.Start(_startedAt);

        session.End(_startedAt);

        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.Equal(session.StartedAt, session.EndedAt);
    }

    // --- State machine: NEGATIVE path (exhaustive) -----------------------------

    [Fact]
    public void Start_from_live_is_an_invalid_transition_and_does_not_mutate()
    {
        // A live timeline must reject a re-start as a genuine state error, NOT a
        // no-op (unlike Participant.Remove). No mutation may happen.
        var session = CreatePrepared();
        session.Start(_startedAt);
        var statusBefore = session.Status;
        var startedBefore = session.StartedAt;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(
            () => session.Start(_endedAt));

        Assert.Equal(session.Id, ex.SessionId);
        Assert.Equal(SessionStatus.Live, ex.From);
        Assert.Equal(SessionStatus.Live, ex.To);
        Assert.False(session.CanStart);
        // Nothing changed on the rejected transition.
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(startedBefore, session.StartedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void Start_from_ended_is_an_invalid_transition_and_does_not_mutate()
    {
        var session = CreatePrepared();
        session.Start(_startedAt);
        session.End(_endedAt);
        var statusBefore = session.Status;
        var startedBefore = session.StartedAt;
        var endedBefore = session.EndedAt;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(
            () => session.Start(_endedAt.AddHours(1)));

        Assert.Equal(SessionStatus.Ended, ex.From);
        Assert.Equal(SessionStatus.Live, ex.To);
        Assert.False(session.CanStart);
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(startedBefore, session.StartedAt);
        Assert.Equal(endedBefore, session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void End_from_prepared_is_an_invalid_transition_and_does_not_mutate()
    {
        // Ending presupposes a live session (SessionEnded "ends live timeline"):
        // ending a session that never started is rejected with no mutation.
        var session = CreatePrepared();
        var statusBefore = session.Status;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(
            () => session.End(_endedAt));

        Assert.Equal(SessionStatus.Prepared, ex.From);
        Assert.Equal(SessionStatus.Ended, ex.To);
        Assert.False(session.CanEnd);
        Assert.Equal(statusBefore, session.Status);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void End_from_ended_is_an_invalid_transition_and_does_not_mutate()
    {
        var session = CreatePrepared();
        session.Start(_startedAt);
        session.End(_endedAt);
        var statusBefore = session.Status;
        var endedBefore = session.EndedAt;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(
            () => session.End(_endedAt.AddHours(1)));

        Assert.Equal(SessionStatus.Ended, ex.From);
        Assert.Equal(SessionStatus.Ended, ex.To);
        Assert.False(session.CanEnd);
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(endedBefore, session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void End_before_start_is_rejected_and_does_not_mutate()
    {
        // Invariant EndedAt >= StartedAt: an end timestamp before the start is a
        // malformed timeline and is rejected (ArgumentException), leaving the live
        // session unchanged.
        var session = CreatePrepared();
        session.Start(_startedAt);
        var statusBefore = session.Status;
        var updatedBefore = session.UpdatedAt;

        Assert.Throws<ArgumentException>(() => session.End(_startedAt.AddSeconds(-1)));

        // Still live; the rejected end did not move the status, set EndedAt or
        // re-stamp the update timestamp.
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(SessionStatus.Live, session.Status);
        Assert.Null(session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void CanStart_and_CanEnd_reflect_each_lifecycle_state()
    {
        var session = CreatePrepared();
        // Prepared: may start, may not end.
        Assert.True(session.CanStart);
        Assert.False(session.CanEnd);

        session.Start(_startedAt);
        // Live: may not start again, may end.
        Assert.False(session.CanStart);
        Assert.True(session.CanEnd);

        session.End(_endedAt);
        // Ended (terminal): may neither start nor end.
        Assert.False(session.CanStart);
        Assert.False(session.CanEnd);
    }

    // --- State machine: CANCEL off-ramp (CORE-LIFE-010) ------------------------

    [Fact]
    public void Cancel_transitions_prepared_to_cancelled_without_a_live_timeline()
    {
        // Happy path: a not-yet-started session goes Prepared -> Cancelled (a soft,
        // terminal off-ramp). It never opened a live timeline, so StartedAt/EndedAt
        // stay null; only the status and the update timestamp change.
        var session = CreatePrepared();

        Assert.True(session.CanCancel);
        session.Cancel(_startedAt);

        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.True(session.IsCancelled);
        Assert.False(session.IsLive);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(_startedAt, session.UpdatedAt);
        Assert.Equal(_createdAt, session.CreatedAt);
        // The tenant boundary and the workspace are immutable across a cancel (threat T5).
        Assert.Equal(_organizationId, session.OrganizationId);
        Assert.Equal(_workspaceId, session.WorkspaceId);
    }

    [Fact]
    public void Cancel_normalizes_the_timestamp_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 11, 11, 0, 0, TimeSpan.FromHours(2));
        var session = CreatePrepared();

        session.Cancel(local);

        Assert.Equal(TimeSpan.Zero, session.UpdatedAt.Offset);
        Assert.Equal(local.ToUniversalTime(), session.UpdatedAt);
    }

    [Fact]
    public void A_cancelled_session_can_be_neither_started_ended_nor_cancelled_again()
    {
        // Cancelled is terminal: every further transition is rejected as a genuine
        // state error with NO mutation.
        var session = CreatePrepared();
        session.Cancel(_startedAt);
        var updatedBefore = session.UpdatedAt;

        Assert.False(session.CanStart);
        Assert.False(session.CanEnd);
        Assert.False(session.CanCancel);

        Assert.Throws<InvalidSessionStateTransitionException>(() => session.Start(_endedAt));
        Assert.Throws<InvalidSessionStateTransitionException>(() => session.End(_endedAt));
        var ex = Assert.Throws<InvalidSessionStateTransitionException>(() => session.Cancel(_endedAt));

        Assert.Equal(SessionStatus.Cancelled, ex.From);
        Assert.Equal(SessionStatus.Cancelled, ex.To);
        // Nothing changed on any rejected transition.
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void Cancel_from_live_is_an_invalid_transition_and_does_not_mutate()
    {
        // A live session must be ENDED, not cancelled: cancelling it is rejected with
        // no mutation (a live timeline is never silently discarded).
        var session = CreatePrepared();
        session.Start(_startedAt);
        var statusBefore = session.Status;
        var startedBefore = session.StartedAt;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(() => session.Cancel(_endedAt));

        Assert.Equal(session.Id, ex.SessionId);
        Assert.Equal(SessionStatus.Live, ex.From);
        Assert.Equal(SessionStatus.Cancelled, ex.To);
        Assert.False(session.CanCancel);
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(startedBefore, session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void Cancel_from_ended_is_an_invalid_transition_and_does_not_mutate()
    {
        var session = CreatePrepared();
        session.Start(_startedAt);
        session.End(_endedAt);
        var statusBefore = session.Status;
        var endedBefore = session.EndedAt;
        var updatedBefore = session.UpdatedAt;

        var ex = Assert.Throws<InvalidSessionStateTransitionException>(() => session.Cancel(_endedAt.AddHours(1)));

        Assert.Equal(SessionStatus.Ended, ex.From);
        Assert.Equal(SessionStatus.Cancelled, ex.To);
        Assert.False(session.CanCancel);
        Assert.Equal(statusBefore, session.Status);
        Assert.Equal(endedBefore, session.EndedAt);
        Assert.Equal(updatedBefore, session.UpdatedAt);
    }

    [Fact]
    public void Start_after_cancel_is_rejected_so_a_cancelled_session_never_runs()
    {
        // Explicit guard for the story's promise: a cancelled session cannot be
        // resurrected by a start, so it never opens a live timeline.
        var session = CreatePrepared();
        session.Cancel(_startedAt);

        Assert.Throws<InvalidSessionStateTransitionException>(() => session.Start(_endedAt));

        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Null(session.StartedAt);
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_and_status_but_no_title()
    {
        // Log-safety (threat T7): structured logs carry identifiers, the lifecycle
        // status and the live-timeline timestamps, never the title (free-form
        // metadata content).
        var session = CreatePrepared();
        session.Start(_startedAt);

        var text = session.ToString();

        Assert.Contains(session.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Live", text, StringComparison.Ordinal);
        // The title is never written to logs.
        Assert.DoesNotContain("Opening Run", text, StringComparison.Ordinal);
    }
}
