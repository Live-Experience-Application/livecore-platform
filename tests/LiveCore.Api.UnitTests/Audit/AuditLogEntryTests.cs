using LiveCore.Api.Audit;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Tests for <see cref="AuditLogEntry"/> — the append-only audit log aggregate. The suite pins the
/// invariants of the visibility-specific factory <see cref="AuditLogEntry.ForVisibilityRuleChange"/>
/// (CORE-VIS-006) and of the generic <see cref="AuditLogEntry.Create"/> factory (CORE-AUD-001): required
/// ids are non-empty, optional ids are either null or non-empty (never an "all zeros" reference), the
/// resource reference is a (type, id) pair or absent entirely, the before/after state is optional for a
/// generic action but required for a visibility change, and timestamps are normalized to UTC. It also
/// checks that <see cref="AuditLogEntry.ToString"/> is identifier-only and renders absent parts
/// (threat T7 — no free-form content in logs). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogEntryTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static AuditLogEntry CreateAudienceWideChange()
        => AuditLogEntry.ForVisibilityRuleChange(
            organizationId: Guid.NewGuid(),
            workspaceId: Guid.NewGuid(),
            actorUserProfileId: Guid.NewGuid(),
            resourceType: "ContentBlock",
            resourceId: Guid.NewGuid(),
            targetParticipantId: null,
            previousState: null,
            newState: "Visible",
            createdAt: _now);

    [Fact]
    public void ForVisibilityRuleChange_sets_the_action_and_fields()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var resource = Guid.NewGuid();

        var entry = AuditLogEntry.ForVisibilityRuleChange(
            org, ws, actor, "Entity", resource, targetParticipantId: null,
            previousState: "Hidden", newState: "Visible", createdAt: _now);

        Assert.Equal(AuditAction.VisibilityRuleChanged, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("Entity", entry.ResourceType);
        Assert.Equal(resource, entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Equal("Hidden", entry.PreviousState);
        Assert.Equal("Visible", entry.NewState);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void A_selected_participant_target_is_kept()
    {
        var participant = Guid.NewGuid();

        var entry = AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Scene", Guid.NewGuid(),
            targetParticipantId: participant, previousState: null, newState: "Visible", createdAt: _now);

        Assert.Equal(participant, entry.TargetParticipantId);
    }

    [Fact]
    public void CreatedAt_is_normalized_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.NewGuid(),
            targetParticipantId: null, previousState: null, newState: "Visible", createdAt: local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void ForVisibilityRuleChange_rejects_an_empty_organization()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.NewGuid(),
            null, null, "Visible", _now));

    [Fact]
    public void ForVisibilityRuleChange_rejects_an_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "Entity", Guid.NewGuid(),
            null, null, "Visible", _now));

    [Fact]
    public void ForVisibilityRuleChange_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "Entity", Guid.NewGuid(),
            null, null, "Visible", _now));

    [Fact]
    public void ForVisibilityRuleChange_rejects_an_empty_resource_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.Empty,
            null, null, "Visible", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForVisibilityRuleChange_rejects_a_blank_resource_type(string resourceType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), resourceType, Guid.NewGuid(),
            null, null, "Visible", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForVisibilityRuleChange_rejects_a_blank_new_state(string newState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.NewGuid(),
            null, null, newState, _now));

    [Fact]
    public void ForVisibilityRuleChange_rejects_an_explicitly_empty_target_participant()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.NewGuid(),
            targetParticipantId: Guid.Empty, previousState: null, newState: "Visible", createdAt: _now));

    [Fact]
    public void ToString_is_identifier_only()
    {
        var entry = CreateAudienceWideChange();

        var text = entry.ToString();

        // Carries identifiers/enum names only — the action, the row id and the state transition — never
        // free-form content (threat T7).
        Assert.Contains(nameof(AuditAction.VisibilityRuleChanged), text, StringComparison.Ordinal);
        Assert.Contains(entry.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Visible", text, StringComparison.Ordinal);
    }

    // --- Generic Create factory (CORE-AUD-001) ---

    [Fact]
    public void Create_records_a_generic_organization_level_system_action()
    {
        var org = Guid.NewGuid();

        // A generic action with no workspace (org-level), no actor (system), no resource and no state.
        var entry = AuditLogEntry.Create(
            organizationId: org,
            workspaceId: null,
            action: AuditAction.MemberInvited,
            actorUserProfileId: null,
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt: _now);

        Assert.Equal(org, entry.OrganizationId);
        Assert.Null(entry.WorkspaceId);
        Assert.Equal(AuditAction.MemberInvited, entry.Action);
        Assert.Null(entry.ActorUserProfileId);
        Assert.Null(entry.ResourceType);
        Assert.Null(entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void Create_keeps_a_workspace_scoped_user_action_with_a_resource_and_state()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var resource = Guid.NewGuid();

        var entry = AuditLogEntry.Create(
            org, ws, AuditAction.VisibilityRuleChanged, actor,
            resourceType: "Scene", resourceId: resource, targetParticipantId: null,
            previousState: "Hidden", newState: "Visible", createdAt: _now);

        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("Scene", entry.ResourceType);
        Assert.Equal(resource, entry.ResourceId);
        Assert.Equal("Hidden", entry.PreviousState);
        Assert.Equal("Visible", entry.NewState);
    }

    [Fact]
    public void Create_normalizes_created_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.Create(
            Guid.NewGuid(), workspaceId: null, AuditAction.SessionStarted, actorUserProfileId: Guid.NewGuid(),
            resourceType: null, resourceId: null, targetParticipantId: null,
            previousState: null, newState: null, createdAt: local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void Create_rejects_an_empty_organization()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.Empty, null, AuditAction.SessionStarted, null,
            null, null, null, null, null, _now));

    [Fact]
    public void Create_rejects_an_explicitly_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), Guid.Empty, AuditAction.SessionStarted, null,
            null, null, null, null, null, _now));

    [Fact]
    public void Create_rejects_an_explicitly_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), null, AuditAction.SessionStarted, Guid.Empty,
            null, null, null, null, null, _now));

    [Fact]
    public void Create_rejects_an_explicitly_empty_target_participant()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), null, AuditAction.SessionStarted, null,
            null, null, targetParticipantId: Guid.Empty, previousState: null, newState: null, createdAt: _now));

    [Fact]
    public void Create_rejects_an_undefined_action()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), null, (AuditAction)999, null,
            null, null, null, null, null, _now));

    [Fact]
    public void Create_rejects_a_resource_type_without_an_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), AuditAction.VisibilityRuleChanged, Guid.NewGuid(),
            resourceType: "Scene", resourceId: null, targetParticipantId: null,
            previousState: null, newState: "Visible", createdAt: _now));

    [Fact]
    public void Create_rejects_a_resource_id_without_a_type()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), AuditAction.VisibilityRuleChanged, Guid.NewGuid(),
            resourceType: null, resourceId: Guid.NewGuid(), targetParticipantId: null,
            previousState: null, newState: "Visible", createdAt: _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_resource_type(string resourceType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), AuditAction.VisibilityRuleChanged, Guid.NewGuid(),
            resourceType: resourceType, resourceId: Guid.NewGuid(), targetParticipantId: null,
            previousState: null, newState: "Visible", createdAt: _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_new_state(string blank)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), null, AuditAction.SessionStarted, null,
            null, null, null, null, newState: blank, createdAt: _now));

    [Fact]
    public void Create_allows_a_null_new_state_for_a_non_transition_action()
    {
        var entry = AuditLogEntry.Create(
            Guid.NewGuid(), null, AuditAction.SessionStarted, null,
            null, null, null, null, newState: null, createdAt: _now);

        Assert.Null(entry.NewState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_previous_state(string blank)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), AuditAction.VisibilityRuleChanged, Guid.NewGuid(),
            resourceType: "Scene", resourceId: Guid.NewGuid(), targetParticipantId: null,
            previousState: blank, newState: "Visible", createdAt: _now));

    [Fact]
    public void ForVisibilityRuleChange_still_requires_a_new_state_even_though_Create_makes_it_optional()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForVisibilityRuleChange(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Entity", Guid.NewGuid(),
            targetParticipantId: null, previousState: null, newState: null!, createdAt: _now));

    // --- Session cancellation factory (CORE-LIFE-010) ---

    [Fact]
    public void ForSessionCancellation_sets_the_action_resource_and_state_transition()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();

        var entry = AuditLogEntry.ForSessionCancellation(
            org, ws, actor, "Session", session,
            previousState: "Prepared", newState: "Cancelled", createdAt: _now);

        Assert.Equal(AuditAction.SessionCancelled, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        // The session is both the scope (workspace) and the governed resource (its id).
        Assert.Equal("Session", entry.ResourceType);
        Assert.Equal(session, entry.ResourceId);
        // A cancel is a surviving STATE TRANSITION, so it records before/after status names.
        Assert.Equal("Prepared", entry.PreviousState);
        Assert.Equal("Cancelled", entry.NewState);
        Assert.Null(entry.TargetParticipantId);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void ForSessionCancellation_rejects_an_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "Session", Guid.NewGuid(),
            "Prepared", "Cancelled", _now));

    [Fact]
    public void ForSessionCancellation_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "Session", Guid.NewGuid(),
            "Prepared", "Cancelled", _now));

    [Fact]
    public void ForSessionCancellation_rejects_an_empty_session_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.Empty,
            "Prepared", "Cancelled", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSessionCancellation_rejects_a_blank_resource_type(string resourceType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), resourceType, Guid.NewGuid(),
            "Prepared", "Cancelled", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSessionCancellation_rejects_a_blank_previous_state(string previousState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.NewGuid(),
            previousState, "Cancelled", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSessionCancellation_rejects_a_blank_new_state(string newState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.NewGuid(),
            "Prepared", newState, _now));

    [Fact]
    public void ForSessionCancellation_normalizes_created_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.ForSessionCancellation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.NewGuid(),
            "Prepared", "Cancelled", local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }

    // --- Session start / end lifecycle transitions (CORE-EVT-001) --------------

    [Fact]
    public void ForSessionStart_sets_the_action_resource_and_prepared_to_live_transition()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();

        var entry = AuditLogEntry.ForSessionStart(
            org, ws, actor, "Session", session,
            previousState: "Prepared", newState: "Live", createdAt: _now);

        Assert.Equal(AuditAction.SessionStarted, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        // The session is both the scope (workspace) and the governed resource (its id).
        Assert.Equal("Session", entry.ResourceType);
        Assert.Equal(session, entry.ResourceId);
        // A start is a surviving STATE TRANSITION, so it records before/after status names.
        Assert.Equal("Prepared", entry.PreviousState);
        Assert.Equal("Live", entry.NewState);
        Assert.Null(entry.TargetParticipantId);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void ForSessionEnd_sets_the_action_resource_and_live_to_ended_transition()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();

        var entry = AuditLogEntry.ForSessionEnd(
            org, ws, actor, "Session", session,
            previousState: "Live", newState: "Ended", createdAt: _now);

        Assert.Equal(AuditAction.SessionEnded, entry.Action);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("Session", entry.ResourceType);
        Assert.Equal(session, entry.ResourceId);
        Assert.Equal("Live", entry.PreviousState);
        Assert.Equal("Ended", entry.NewState);
        Assert.Null(entry.TargetParticipantId);
    }

    [Fact]
    public void ForSessionStart_rejects_an_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionStart(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "Session", Guid.NewGuid(),
            "Prepared", "Live", _now));

    [Fact]
    public void ForSessionStart_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionStart(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "Session", Guid.NewGuid(),
            "Prepared", "Live", _now));

    [Fact]
    public void ForSessionStart_rejects_an_empty_session_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionStart(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.Empty,
            "Prepared", "Live", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSessionStart_rejects_a_blank_previous_state(string previousState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionStart(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.NewGuid(),
            previousState, "Live", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSessionEnd_rejects_a_blank_new_state(string newState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForSessionEnd(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Session", Guid.NewGuid(),
            "Live", newState, _now));

    [Fact]
    public void ToString_of_a_generic_entry_renders_absent_parts()
    {
        var entry = AuditLogEntry.Create(
            Guid.NewGuid(), null, AuditAction.MemberInvited, null,
            null, null, null, null, null, _now);

        var text = entry.ToString();

        // Identifier/enum names only, and every absent optional part renders as a sentinel — never a
        // misleading "all zeros" id and never free-form content (threat T7).
        Assert.Contains(nameof(AuditAction.MemberInvited), text, StringComparison.Ordinal);
        Assert.Contains("ws=none", text, StringComparison.Ordinal);
        Assert.Contains("actor=system", text, StringComparison.Ordinal);
        Assert.Contains("resource=none:none", text, StringComparison.Ordinal);
        Assert.Contains("target=audience", text, StringComparison.Ordinal);
        Assert.Contains("none->none", text, StringComparison.Ordinal);
    }
}
