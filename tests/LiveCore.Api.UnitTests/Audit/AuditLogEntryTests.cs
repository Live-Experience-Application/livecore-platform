using LiveCore.Api.Audit;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Tests for <see cref="AuditLogEntry"/> (CORE-VIS-006) — the append-only audit log aggregate. The
/// suite pins the structural invariants of the only factory this story exposes,
/// <see cref="AuditLogEntry.ForVisibilityRuleChange"/>: required ids are non-empty, optional ids are
/// either null or non-empty (never an "all zeros" reference), the new state is required, and timestamps
/// are normalized to UTC. It also checks that <see cref="AuditLogEntry.ToString"/> is identifier-only
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
}
