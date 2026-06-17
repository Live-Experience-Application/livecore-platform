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

    // --- Entitlement / store / purchase audit facts (CORE-SPEC-002) ------------

    [Fact]
    public void Create_allows_a_null_organization_for_a_platform_level_fact()
    {
        // CORE-SPEC-002: a platform-level (tenant-less) audit fact carries a null organization.
        var entry = AuditLogEntry.Create(
            organizationId: null,
            workspaceId: null,
            AuditAction.EntitlementGranted,
            actorUserProfileId: null,
            resourceType: "User",
            resourceId: Guid.NewGuid(),
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt: _now);

        Assert.Null(entry.OrganizationId);
        Assert.Equal(AuditAction.EntitlementGranted, entry.Action);
    }

    [Fact]
    public void Create_still_rejects_an_explicitly_empty_organization()
        // Null is the platform sentinel; an all-zeros id is still rejected (never a misleading reference).
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.Create(
            Guid.Empty, null, AuditAction.EntitlementGranted, null,
            null, null, null, null, null, _now));

    [Fact]
    public void ForQuotaExceeded_is_a_tenant_scoped_fact_with_the_subject_as_resource()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var subject = Guid.NewGuid();

        var entry = AuditLogEntry.ForQuotaExceeded(org, ws, actor, "Workspace", subject, _now);

        Assert.Equal(AuditAction.QuotaExceeded, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("Workspace", entry.ResourceType);
        Assert.Equal(subject, entry.ResourceId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public void ForQuotaExceeded_allows_a_null_workspace()
    {
        // A user-subject quota (workspace.active.max) is denied before the workspace exists, so there is no scope.
        var entry = AuditLogEntry.ForQuotaExceeded(Guid.NewGuid(), null, Guid.NewGuid(), "User", Guid.NewGuid(), _now);
        Assert.Null(entry.WorkspaceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForQuotaExceeded_rejects_a_blank_subject_type(string subjectType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForQuotaExceeded(
            Guid.NewGuid(), null, Guid.NewGuid(), subjectType, Guid.NewGuid(), _now));

    [Fact]
    public void ForQuotaExceeded_rejects_an_empty_organization_actor_or_subject()
    {
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForQuotaExceeded(
            Guid.Empty, null, Guid.NewGuid(), "User", Guid.NewGuid(), _now));
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForQuotaExceeded(
            Guid.NewGuid(), null, Guid.Empty, "User", Guid.NewGuid(), _now));
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForQuotaExceeded(
            Guid.NewGuid(), null, Guid.NewGuid(), "User", Guid.Empty, _now));
    }

    [Fact]
    public void ForEntitlementGranted_is_a_platform_system_fact_with_the_subject_as_resource()
    {
        var subject = Guid.NewGuid();

        var entry = AuditLogEntry.ForEntitlementGranted("User", subject, _now);

        Assert.Equal(AuditAction.EntitlementGranted, entry.Action);
        Assert.Null(entry.OrganizationId); // platform-level (deployment-spanning, not tenant-scoped)
        Assert.Null(entry.WorkspaceId);
        Assert.Null(entry.ActorUserProfileId); // system-initiated by a verified purchase
        Assert.Equal("User", entry.ResourceType);
        Assert.Equal(subject, entry.ResourceId);
    }

    [Fact]
    public void ForEntitlementRevoked_is_a_platform_system_fact()
    {
        var subject = Guid.NewGuid();

        var entry = AuditLogEntry.ForEntitlementRevoked("User", subject, _now);

        Assert.Equal(AuditAction.EntitlementRevoked, entry.Action);
        Assert.Null(entry.OrganizationId);
        Assert.Null(entry.ActorUserProfileId);
        Assert.Equal(subject, entry.ResourceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForEntitlementGranted_rejects_a_blank_subject_type(string subjectType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForEntitlementGranted(subjectType, Guid.NewGuid(), _now));

    [Fact]
    public void ForEntitlementGranted_rejects_an_empty_subject_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForEntitlementGranted("User", Guid.Empty, _now));

    [Fact]
    public void ForPurchaseVerification_succeeded_names_the_recorded_purchase()
    {
        var actor = Guid.NewGuid();
        var transaction = Guid.NewGuid();

        var entry = AuditLogEntry.ForPurchaseVerification(
            AuditAction.PurchaseVerificationSucceeded, actor, transaction, _now);

        Assert.Equal(AuditAction.PurchaseVerificationSucceeded, entry.Action);
        Assert.Null(entry.OrganizationId); // platform-level
        Assert.Equal(actor, entry.ActorUserProfileId); // the buyer
        Assert.Equal("PurchaseTransaction", entry.ResourceType);
        Assert.Equal(transaction, entry.ResourceId);
    }

    [Theory]
    [InlineData(AuditAction.PurchaseVerificationSubmitted)]
    [InlineData(AuditAction.PurchaseVerificationFailed)]
    public void ForPurchaseVerification_submitted_or_failed_has_no_recorded_purchase(AuditAction action)
    {
        var entry = AuditLogEntry.ForPurchaseVerification(action, Guid.NewGuid(), purchaseTransactionId: null, _now);

        Assert.Equal(action, entry.Action);
        Assert.Null(entry.OrganizationId);
        Assert.Null(entry.ResourceType);
        Assert.Null(entry.ResourceId);
    }

    [Fact]
    public void ForPurchaseVerification_rejects_a_non_verification_action()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AuditLogEntry.ForPurchaseVerification(
            AuditAction.EntitlementGranted, Guid.NewGuid(), null, _now));

    [Fact]
    public void ForPurchaseVerification_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForPurchaseVerification(
            AuditAction.PurchaseVerificationSubmitted, Guid.Empty, null, _now));

    [Fact]
    public void ForStoreNotificationReceived_records_the_type_as_a_platform_system_fact()
    {
        var entry = AuditLogEntry.ForStoreNotificationReceived("Refunded", _now);

        Assert.Equal(AuditAction.StoreNotificationReceived, entry.Action);
        Assert.Null(entry.OrganizationId);
        Assert.Null(entry.ActorUserProfileId);
        Assert.Null(entry.ResourceId);
        Assert.Equal("Refunded", entry.NewState);
    }

    [Fact]
    public void ForStoreNotificationProcessed_records_the_outcome()
    {
        var entry = AuditLogEntry.ForStoreNotificationProcessed("Applied", _now);

        Assert.Equal(AuditAction.StoreNotificationProcessed, entry.Action);
        Assert.Null(entry.OrganizationId);
        Assert.Equal("Applied", entry.NewState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForStoreNotification_rejects_a_blank_descriptor(string descriptor)
    {
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForStoreNotificationReceived(descriptor, _now));
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForStoreNotificationProcessed(descriptor, _now));
    }

    [Fact]
    public void ToString_of_a_platform_fact_renders_the_platform_sentinel()
    {
        var entry = AuditLogEntry.ForEntitlementGranted("User", Guid.NewGuid(), _now);

        var text = entry.ToString();

        Assert.Contains("org=platform", text, StringComparison.Ordinal);
        Assert.Contains(nameof(AuditAction.EntitlementGranted), text, StringComparison.Ordinal);
    }

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

    // --- Member joined / invitation redemption factory (CORE-WS-006) -----------

    [Fact]
    public void ForMemberJoined_sets_the_action_resource_and_granted_role_as_the_new_state()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var member = Guid.NewGuid();

        var entry = AuditLogEntry.ForMemberJoined(
            org, ws, actor, "WorkspaceMember", member, grantedRole: "Host", createdAt: _now);

        Assert.Equal(AuditAction.MemberJoined, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        // The actor is the caller who redeemed the token and became the member (the bearer grant).
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("WorkspaceMember", entry.ResourceType);
        Assert.Equal(member, entry.ResourceId);
        // A redemption GRANTS access, so — unlike a removal recording the revoked role as the previous state —
        // it records the granted role as the NEW state, with no previous state.
        Assert.Null(entry.PreviousState);
        Assert.Equal("Host", entry.NewState);
        Assert.Null(entry.TargetParticipantId);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void ForMemberJoined_rejects_an_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "WorkspaceMember", Guid.NewGuid(), "Host", _now));

    [Fact]
    public void ForMemberJoined_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "WorkspaceMember", Guid.NewGuid(), "Host", _now));

    [Fact]
    public void ForMemberJoined_rejects_an_empty_member_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceMember", Guid.Empty, "Host", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForMemberJoined_rejects_a_blank_resource_type(string resourceType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), resourceType, Guid.NewGuid(), "Host", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForMemberJoined_rejects_a_blank_granted_role(string grantedRole)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceMember", Guid.NewGuid(), grantedRole, _now));

    [Fact]
    public void ForMemberJoined_normalizes_created_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.ForMemberJoined(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceMember", Guid.NewGuid(), "Host", local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void ForMemberInvitationRevoked_sets_the_action_resource_and_status_transition()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        var entry = AuditLogEntry.ForMemberInvitationRevoked(
            org, ws, actor, "WorkspaceInvitation", invitation, previousState: "Pending", newState: "Revoked",
            createdAt: _now);

        Assert.Equal(AuditAction.MemberInvitationRevoked, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        // The actor is the admin who revoked the invitation.
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("WorkspaceInvitation", entry.ResourceType);
        Assert.Equal(invitation, entry.ResourceId);
        // A revoke is a real state transition (the invitation row survives), so it records the before/after
        // status names exactly like a workspace archive.
        Assert.Equal("Pending", entry.PreviousState);
        Assert.Equal("Revoked", entry.NewState);
        Assert.Null(entry.TargetParticipantId);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void ForMemberInvitationRevoked_rejects_an_empty_workspace()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "WorkspaceInvitation", Guid.NewGuid(), "Pending", "Revoked", _now));

    [Fact]
    public void ForMemberInvitationRevoked_rejects_an_empty_actor()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "WorkspaceInvitation", Guid.NewGuid(), "Pending", "Revoked", _now));

    [Fact]
    public void ForMemberInvitationRevoked_rejects_an_empty_invitation_id()
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceInvitation", Guid.Empty, "Pending", "Revoked", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForMemberInvitationRevoked_rejects_a_blank_resource_type(string resourceType)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), resourceType, Guid.NewGuid(), "Pending", "Revoked", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForMemberInvitationRevoked_rejects_a_blank_previous_state(string previousState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceInvitation", Guid.NewGuid(), previousState, "Revoked", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForMemberInvitationRevoked_rejects_a_blank_new_state(string newState)
        => Assert.Throws<ArgumentException>(() => AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceInvitation", Guid.NewGuid(), "Pending", newState, _now));

    [Fact]
    public void ForMemberInvitationRevoked_normalizes_created_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.ForMemberInvitationRevoked(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "WorkspaceInvitation", Guid.NewGuid(), "Pending", "Revoked", local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void ForUserProfileErasure_records_an_organization_level_fact_by_id_only()
    {
        // CORE-PRIV-001 (GDPR Art.17): the erasure is audited by id only — actor + erased subject id — with no
        // workspace and no before/after state, and never the erased PII.
        var organizationId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var erasedSubject = Guid.NewGuid();

        var entry = AuditLogEntry.ForUserProfileErasure(
            organizationId, actor, "UserProfile", erasedSubject, _now);

        Assert.Equal(organizationId, entry.OrganizationId);
        Assert.Equal(AuditAction.UserProfileErased, entry.Action);
        Assert.Equal(actor, entry.ActorUserProfileId);
        Assert.Equal("UserProfile", entry.ResourceType);
        Assert.Equal(erasedSubject, entry.ResourceId);
        Assert.Null(entry.WorkspaceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public void ForUserProfileErasure_rejects_an_empty_actor_subject_or_blank_resource_type()
    {
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForUserProfileErasure(
            Guid.NewGuid(), Guid.Empty, "UserProfile", Guid.NewGuid(), _now));
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForUserProfileErasure(
            Guid.NewGuid(), Guid.NewGuid(), "UserProfile", Guid.Empty, _now));
        Assert.Throws<ArgumentException>(() => AuditLogEntry.ForUserProfileErasure(
            Guid.NewGuid(), Guid.NewGuid(), " ", Guid.NewGuid(), _now));
    }

    [Fact]
    public void ForUserProfileErasure_normalizes_created_at_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var entry = AuditLogEntry.ForUserProfileErasure(
            Guid.NewGuid(), Guid.NewGuid(), "UserProfile", Guid.NewGuid(), local);

        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, entry.CreatedAt.UtcDateTime);
    }
}
