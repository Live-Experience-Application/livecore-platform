// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Unit tests for the <see cref="VisibilityRule"/> aggregate (CORE-VIS-001) — the structural
/// invariants, the resource/tenant/workspace guards, the <see cref="VisibilityState"/> read and the
/// <see cref="VisibilityRule.ChangeVisibility"/> state-transition primitive, plus the log-safe
/// <see cref="VisibilityRule.ToString"/>. The rule binds a generic Core resource (named by type +
/// id) to a base audience visibility state; all fixtures are generic — no vertical vocabulary
/// appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class VisibilityRuleTests
{
    private static readonly Guid _organizationId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid _workspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid _sessionId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid _resourceId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static VisibilityRule CreateRule(
        VisibilityResourceType resourceType = VisibilityResourceType.ContentBlock,
        VisibilityState visibility = VisibilityState.Hidden)
        => VisibilityRule.Create(
            _organizationId, _workspaceId, _sessionId, resourceType, _resourceId, visibility, _createdAt);

    [Fact]
    public void Create_sets_all_fields()
    {
        var rule = CreateRule(VisibilityResourceType.Scene, VisibilityState.Hidden);

        Assert.NotEqual(Guid.Empty, rule.Id);
        Assert.Equal(_organizationId, rule.OrganizationId);
        Assert.Equal(_workspaceId, rule.WorkspaceId);
        Assert.Equal(_sessionId, rule.SessionId);
        Assert.Equal(VisibilityResourceType.Scene, rule.ResourceType);
        Assert.Equal(_resourceId, rule.ResourceId);
        Assert.Equal(VisibilityState.Hidden, rule.Visibility);
        Assert.Equal(_createdAt, rule.CreatedAt);
        Assert.Equal(_createdAt, rule.UpdatedAt);
    }

    [Fact]
    public void Create_rejects_empty_organization_id()
    {
        var exception = Assert.Throws<ArgumentException>(() => VisibilityRule.Create(
            Guid.Empty, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, VisibilityState.Hidden, _createdAt));
        Assert.Equal("organizationId", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_empty_workspace_id()
    {
        var exception = Assert.Throws<ArgumentException>(() => VisibilityRule.Create(
            _organizationId, Guid.Empty, _sessionId, VisibilityResourceType.Entity, _resourceId, VisibilityState.Hidden, _createdAt));
        Assert.Equal("workspaceId", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_empty_session_id()
    {
        var exception = Assert.Throws<ArgumentException>(() => VisibilityRule.Create(
            _organizationId, _workspaceId, Guid.Empty, VisibilityResourceType.Entity, _resourceId, VisibilityState.Hidden, _createdAt));
        Assert.Equal("sessionId", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_empty_resource_id()
    {
        var exception = Assert.Throws<ArgumentException>(() => VisibilityRule.Create(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, Guid.Empty, VisibilityState.Hidden, _createdAt));
        Assert.Equal("resourceId", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_undefined_resource_type()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => VisibilityRule.Create(
            _organizationId, _workspaceId, _sessionId, (VisibilityResourceType)999, _resourceId, VisibilityState.Hidden, _createdAt));
        Assert.Equal("resourceType", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_undefined_visibility()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => VisibilityRule.Create(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, (VisibilityState)999, _createdAt));
        Assert.Equal("visibility", exception.ParamName);
    }

    [Fact]
    public void ChangeVisibility_updates_state_and_timestamp()
    {
        var rule = CreateRule(visibility: VisibilityState.Hidden);

        rule.ChangeVisibility(VisibilityState.Visible, _updatedAt);

        Assert.Equal(VisibilityState.Visible, rule.Visibility);
        Assert.Equal(_updatedAt, rule.UpdatedAt);
        Assert.True(rule.IsVisibleToAudience());
    }

    [Fact]
    public void ChangeVisibility_rejects_undefined_state_with_no_mutation()
    {
        var rule = CreateRule(visibility: VisibilityState.Hidden);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => rule.ChangeVisibility((VisibilityState)999, _updatedAt));

        // No mutation: the prior state and timestamp are intact.
        Assert.Equal(VisibilityState.Hidden, rule.Visibility);
        Assert.Equal(_createdAt, rule.UpdatedAt);
    }

    [Fact]
    public void IsVisibleToAudience_reflects_the_state()
    {
        Assert.False(CreateRule(visibility: VisibilityState.Hidden).IsVisibleToAudience());
        Assert.True(CreateRule(visibility: VisibilityState.Visible).IsVisibleToAudience());
    }

    [Fact]
    public void BelongsToOrganization_matches_only_the_owning_tenant()
    {
        var rule = CreateRule();

        Assert.True(rule.BelongsToOrganization(_organizationId));
        Assert.False(rule.BelongsToOrganization(Guid.NewGuid()));
        Assert.False(rule.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void BelongsToWorkspace_matches_only_the_owning_workspace()
    {
        var rule = CreateRule();

        Assert.True(rule.BelongsToWorkspace(_workspaceId));
        Assert.False(rule.BelongsToWorkspace(Guid.NewGuid()));
        Assert.False(rule.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void BelongsToSession_matches_only_the_owning_session()
    {
        var rule = CreateRule();

        Assert.True(rule.BelongsToSession(_sessionId));
        // A reveal is session-scoped: a rule never belongs to another concurrent session (CORE-SVIS-001).
        Assert.False(rule.BelongsToSession(Guid.NewGuid()));
        Assert.False(rule.BelongsToSession(Guid.Empty));
    }

    [Fact]
    public void Governs_matches_only_the_exact_resource_type_and_id()
    {
        var rule = CreateRule(VisibilityResourceType.ContentBlock);

        Assert.True(rule.Governs(VisibilityResourceType.ContentBlock, _resourceId));
        // Wrong type, same id.
        Assert.False(rule.Governs(VisibilityResourceType.Scene, _resourceId));
        // Right type, wrong id.
        Assert.False(rule.Governs(VisibilityResourceType.ContentBlock, Guid.NewGuid()));
        // Empty id matches nothing.
        Assert.False(rule.Governs(VisibilityResourceType.ContentBlock, Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_all_three_to_match()
    {
        var rule = CreateRule();

        Assert.True(rule.Identifies(_organizationId, _workspaceId, rule.Id));
        Assert.False(rule.Identifies(Guid.NewGuid(), _workspaceId, rule.Id));
        Assert.False(rule.Identifies(_organizationId, Guid.NewGuid(), rule.Id));
        Assert.False(rule.Identifies(_organizationId, _workspaceId, Guid.NewGuid()));
        Assert.False(rule.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    [Theory]
    [InlineData(VisibilityResourceType.Scene)]
    [InlineData(VisibilityResourceType.ContentBlock)]
    [InlineData(VisibilityResourceType.Entity)]
    public void IsValidResourceType_accepts_defined_values(VisibilityResourceType resourceType)
        => Assert.True(VisibilityRule.IsValidResourceType(resourceType));

    [Fact]
    public void IsValidResourceType_rejects_undefined_value()
        => Assert.False(VisibilityRule.IsValidResourceType((VisibilityResourceType)999));

    [Theory]
    [InlineData(VisibilityState.Hidden)]
    [InlineData(VisibilityState.Visible)]
    public void IsValidVisibility_accepts_defined_values(VisibilityState visibility)
        => Assert.True(VisibilityRule.IsValidVisibility(visibility));

    [Fact]
    public void IsValidVisibility_rejects_undefined_value()
        => Assert.False(VisibilityRule.IsValidVisibility((VisibilityState)999));

    [Fact]
    public void ToString_exposes_identifiers_and_state_only()
    {
        var rule = CreateRule(VisibilityResourceType.Entity, VisibilityState.Visible);

        var text = rule.ToString();

        // Identifiers, the resource reference and the state are present; there is no free-form
        // content on this aggregate to leak (threat T7).
        Assert.Contains(rule.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_sessionId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Entity", text, StringComparison.Ordinal);
        Assert.Contains(_resourceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Visible", text, StringComparison.Ordinal);
    }

    // --- Selected-participant target (CORE-VIS-005) ----------------------------

    [Fact]
    public void Create_is_audience_wide_with_no_target()
    {
        var rule = CreateRule();

        Assert.Null(rule.TargetParticipantId);
        Assert.True(rule.IsAudienceWide);
        Assert.False(rule.TargetsParticipant(Guid.NewGuid()));
    }

    [Fact]
    public void CreateForParticipant_scopes_the_rule_to_the_participant()
    {
        var participantId = Guid.NewGuid();

        var rule = VisibilityRule.CreateForParticipant(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, participantId,
            VisibilityState.Visible, _createdAt);

        Assert.Equal(participantId, rule.TargetParticipantId);
        Assert.False(rule.IsAudienceWide);
        Assert.True(rule.TargetsParticipant(participantId));
        Assert.False(rule.TargetsParticipant(Guid.NewGuid()));
    }

    [Fact]
    public void CreateForParticipant_rejects_an_empty_participant_id()
    {
        var exception = Assert.Throws<ArgumentException>(() => VisibilityRule.CreateForParticipant(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, Guid.Empty,
            VisibilityState.Visible, _createdAt));
        Assert.Equal("targetParticipantId", exception.ParamName);
    }

    [Fact]
    public void IsVisibleTo_respects_the_audience_wide_and_participant_dimensions()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();

        // An audience-wide visible rule is visible to anyone.
        var audienceWide = CreateRule(visibility: VisibilityState.Visible);
        Assert.True(audienceWide.IsVisibleTo(selected));
        Assert.True(audienceWide.IsVisibleTo(other));

        // A participant-scoped visible rule is visible ONLY to its target.
        var targeted = VisibilityRule.CreateForParticipant(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, selected,
            VisibilityState.Visible, _createdAt);
        Assert.True(targeted.IsVisibleTo(selected));
        Assert.False(targeted.IsVisibleTo(other));

        // A hidden rule is visible to no one, regardless of target.
        var hidden = VisibilityRule.CreateForParticipant(
            _organizationId, _workspaceId, _sessionId, VisibilityResourceType.Entity, _resourceId, selected,
            VisibilityState.Hidden, _createdAt);
        Assert.False(hidden.IsVisibleTo(selected));
    }
}
