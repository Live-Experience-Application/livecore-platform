// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="VisibilityPolicy"/> (CORE-VIS-002) — the <c>CanViewResource</c> decision. The
/// policy is a fail-closed decision service over the real EF Core visibility-rule repository, driven
/// against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the tenant/workspace/session-scoped rule lookups run against genuinely persisted rules — exactly
/// like the CORE-SES-003 join service and CORE-ENT-005 search service tests.
///
/// EVERY decision is SESSION-SCOPED (CORE-SVIS-001, completed by CORE-SVIS-004): the workspace-wide,
/// session-agnostic overloads have been removed, so a decision can never fold a reveal across sessions.
/// This is the NEGATIVE-AUTHORIZATION-heavy suite the policy demands:
/// <list type="bullet">
///   <item>HOST-content roles (Owner/Admin/Host/CoHost) may view a resource whether it is hidden or
///   visible — allowed even with NO rule (docs/06 "View host-only content" = yes).</item>
///   <item>AUDIENCE roles (Participant/Observer) may view a resource ONLY when a rule of THEIR SESSION
///   makes it visible ("if visible"); a hidden-only or rule-less resource is denied (deny-by-default).</item>
///   <item>The audit role and any undefined role are DENIED even when the resource is visible
///   (Auditor is audit-only, not a live content grant; threats T1/T5).</item>
///   <item>ISOLATION: a visible rule in another WORKSPACE, TENANT or SESSION never makes the resource
///   visible to an audience viewer of this workspace/tenant/session (organization boundary before
///   workspace boundary before session before resource-level visibility; threats T5/T3).</item>
/// </list>
///
/// All fixtures are GENERIC — no vertical vocabulary appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class VisibilityPolicyTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public VisibilityPolicyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private VisibilityPolicy CreatePolicy(LiveCoreDbContext context)
        => new(new VisibilityRuleRepository(context));

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    private async Task<Session> SeedSessionAsync(Guid organizationId, Guid workspaceId, string title = "Live Session")
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    /// <summary>Lazily seeds and caches ONE session per workspace, so rules sharing a workspace share a session.</summary>
    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var session = await SeedSessionAsync(organizationId, workspaceId);
        _sessionByWorkspace[workspaceId] = session.Id;
        return session.Id;
    }

    private async Task SeedRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility,
        Guid? sessionId = null)
    {
        var session = sessionId ?? await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.Create(organizationId, workspaceId, session, resourceType, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        Assert.Equal(VisibilityRuleAddResult.Added, await repository.AddAsync(rule, CancellationToken.None));
    }

    private async Task<Participant> SeedParticipantAsync(Guid organizationId, Guid workspaceId)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, "Participant", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(ParticipantAddResult.Added, await new ParticipantRepository(context).AddAsync(participant, CancellationToken.None));
        return participant;
    }

    private async Task SeedParticipantRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid targetParticipantId,
        VisibilityState visibility,
        Guid? sessionId = null)
    {
        var session = sessionId ?? await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.CreateForParticipant(
            organizationId, workspaceId, session, resourceType, resourceId, targetParticipantId, visibility, _createdAt);
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        Assert.Equal(VisibilityRuleAddResult.Added, await repository.AddAsync(rule, CancellationToken.None));
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization.Id, workspace.Id);
    }

    // --- Host-content roles see everything (no rule needed) --------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_role_can_view_a_resource_with_no_rule(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // No rule seeded: a hidden-by-default resource is still visible to a host role. A host short-circuits
        // before any rule lookup, so the session is irrelevant (host content access is session-agnostic).

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, Guid.NewGuid(), role, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByHostRole, decision.Reason);
    }

    [Fact]
    public async Task Host_role_can_view_a_hidden_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Host, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByHostRole, decision.Reason);
    }

    // --- Audience roles: only if visible in their session ----------------------

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_can_view_a_visible_resource(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, role, VisibilityResourceType.Scene, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByVisibleRule, decision.Reason);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_cannot_view_a_hidden_resource(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, role, VisibilityResourceType.Scene, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    [Fact]
    public async Task Audience_role_cannot_view_a_resource_with_no_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var session = (await SeedSessionAsync(org, ws)).Id;
        // No rule at all -> host-only by default -> denied to the audience.

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    // --- Audit role / undefined role: denied even when visible -----------------

    [Fact]
    public async Task Auditor_is_denied_even_for_a_visible_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Auditor, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        // Auditor is audit-only on both content rows: a visible resource does not grant it live access.
        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedRoleNotPermitted, decision.Reason);
    }

    [Fact]
    public async Task Undefined_role_is_denied_even_for_a_visible_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, session, (MembershipRole)999, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedRoleNotPermitted, decision.Reason);
    }

    // --- Isolation: a visible rule elsewhere does not grant visibility ---------

    [Fact]
    public async Task A_visible_rule_in_another_workspace_does_not_grant_visibility()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        // The resource is visible in workspace B only.
        await SeedRuleAsync(organization.Id, workspaceB.Id, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);
        var sessionInA = (await SeedSessionAsync(organization.Id, workspaceA.Id)).Id;

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        // An audience viewer of workspace A (in a session of A) must NOT see it (the rule is in another workspace).
        var decision = await policy.CanViewResourceAsync(
            organization.Id, workspaceA.Id, sessionInA, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    [Fact]
    public async Task A_visible_rule_in_another_tenant_does_not_grant_visibility()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        // The resource is visible in tenant B only.
        await SeedRuleAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);
        var sessionInA = (await SeedSessionAsync(orgA, wsA)).Id;

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        // An audience viewer of tenant A must NOT see it (the rule is in another tenant). The
        // organization boundary is checked before the workspace boundary (threat T5).
        var decision = await policy.CanViewResourceAsync(
            orgA, wsA, sessionInA, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    // --- Selected-participant visibility (CORE-VIS-005) ------------------------

    [Fact]
    public async Task A_selected_participant_sees_a_reveal_targeted_at_them()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, selected, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanParticipantViewResourceAsync(
            org, ws, session, selected, VisibilityResourceType.Entity, resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_non_selected_participant_does_not_see_a_targeted_reveal()
    {
        // THE crown jewel: a resource revealed ONLY to participant `selected` must not be visible to a
        // different participant `other`.
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        var other = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, selected, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanParticipantViewResourceAsync(
            org, ws, session, other, VisibilityResourceType.Entity, resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task An_audience_wide_reveal_is_visible_to_any_participant()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        // Any participant sees an audience-wide visible resource in that session.
        Assert.True(await policy.CanParticipantViewResourceAsync(
            org, ws, session, Guid.NewGuid(), VisibilityResourceType.Scene, resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_targeted_reveal_is_not_visible_at_the_role_level()
    {
        // CanViewResource is role-level (no participant): a participant-scoped visible rule does NOT
        // make the resource visible to a generic audience role — only audience-wide rules do.
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, selected, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        var decision = await policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Participant, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);
        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);

        // ...but a host still sees it (host-only content access).
        var hostDecision = await policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Host, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);
        Assert.True(hostDecision.CanView);
    }

    [Fact]
    public async Task CanParticipantViewResource_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanParticipantViewResourceAsync(
            Guid.Empty, id, id, id, VisibilityResourceType.Entity, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanParticipantViewResourceAsync(
            id, Guid.Empty, id, id, VisibilityResourceType.Entity, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanParticipantViewResourceAsync(
            id, id, id, Guid.Empty, VisibilityResourceType.Entity, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanParticipantViewResourceAsync(
            id, id, id, id, VisibilityResourceType.Entity, Guid.Empty, CancellationToken.None));
    }

    // --- Session scoping (CORE-SVIS-001, the cross-session leak) ---------------

    [Fact]
    public async Task Session_scoped_audience_decision_does_not_leak_across_sessions()
    {
        // A workspace runs two concurrent sessions. A resource revealed audience-wide in session A is
        // visible to an audience role IN SESSION A, but NOT in session B — a reveal in one session never
        // leaks into a concurrent session of the same workspace (threat T5/T3).
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible, sessionA.Id);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        var inA = await policy.CanViewResourceAsync(
            org, ws, sessionA.Id, MembershipRole.Participant, VisibilityResourceType.Scene, resourceId, CancellationToken.None);
        Assert.True(inA.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByVisibleRule, inA.Reason);

        var inB = await policy.CanViewResourceAsync(
            org, ws, sessionB.Id, MembershipRole.Participant, VisibilityResourceType.Scene, resourceId, CancellationToken.None);
        Assert.False(inB.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, inB.Reason);
    }

    [Fact]
    public async Task Session_scoped_participant_decision_does_not_leak_across_sessions()
    {
        // The per-participant decision is bounded by session too: a resource revealed (audience-wide) in
        // session A is visible to a participant in session A but not in session B.
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var resourceId = Guid.NewGuid();
        var participant = (await SeedParticipantAsync(org, ws)).Id;
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible, sessionA.Id);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanParticipantViewResourceAsync(
            org, ws, sessionA.Id, participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None));
        Assert.False(await policy.CanParticipantViewResourceAsync(
            org, ws, sessionB.Id, participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task Session_scoped_overloads_reject_an_empty_session_id()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var id = Guid.NewGuid();

        var viewException = await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            id, id, Guid.Empty, MembershipRole.Participant, VisibilityResourceType.Entity, id, CancellationToken.None));
        Assert.Equal("sessionId", viewException.ParamName);

        var participantException = await Assert.ThrowsAsync<ArgumentException>(() => policy.CanParticipantViewResourceAsync(
            id, id, Guid.Empty, id, VisibilityResourceType.Entity, id, CancellationToken.None));
        Assert.Equal("sessionId", participantException.ParamName);
    }

    // --- Collapsed audience resolution (CORE-PERF-001) -------------------------

    [Fact]
    public async Task Audience_resolution_reports_the_whole_audience_for_an_audience_wide_visible_rule()
    {
        // An audience-wide visible rule means the WHOLE audience may see it, so there is no
        // individually-entitled participant to list (the shared audience group covers them).
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var audience = await policy.ResolveAudienceVisibilityAsync(
            org, ws, session, VisibilityResourceType.Scene, resourceId, CancellationToken.None);

        Assert.True(audience.AudienceVisible);
        Assert.Empty(audience.SelectedVisibleParticipantIds);
    }

    [Fact]
    public async Task Audience_resolution_reports_no_one_for_a_hidden_or_ruleless_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var hidden = Guid.NewGuid();
        var ruleless = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, hidden, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        var hiddenAudience = await policy.ResolveAudienceVisibilityAsync(
            org, ws, session, VisibilityResourceType.Entity, hidden, CancellationToken.None);
        Assert.False(hiddenAudience.AudienceVisible);
        Assert.Empty(hiddenAudience.SelectedVisibleParticipantIds);

        var rulelessAudience = await policy.ResolveAudienceVisibilityAsync(
            org, ws, session, VisibilityResourceType.Entity, ruleless, CancellationToken.None);
        Assert.False(rulelessAudience.AudienceVisible);
        Assert.Empty(rulelessAudience.SelectedVisibleParticipantIds);
    }

    [Fact]
    public async Task Audience_resolution_names_only_the_individually_entitled_participants_when_the_audience_cannot_see_it()
    {
        // No audience-wide rule, but two participants each have a participant-scoped visible rule. The
        // collapsed resolution reports the audience-at-large CANNOT see it and names EXACTLY those two
        // participants — so the resolver delivers the audience-wide event to their groups alone, from ONE
        // lookup, never to a third participant who has no rule (the crown jewel; threat T3).
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var entitledOne = (await SeedParticipantAsync(org, ws)).Id;
        var entitledTwo = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, entitledOne, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, entitledTwo, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var audience = await policy.ResolveAudienceVisibilityAsync(
            org, ws, session, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(audience.AudienceVisible);
        Assert.Equal(
            new HashSet<Guid> { entitledOne, entitledTwo },
            audience.SelectedVisibleParticipantIds.ToHashSet());
    }

    [Fact]
    public async Task Audience_resolution_excludes_a_participant_whose_scoped_rule_is_hidden()
    {
        // A participant-scoped HIDDEN rule does not entitle the participant — only a visible scoped rule
        // does (fail-closed; threat T3).
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var hiddenParticipant = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, hiddenParticipant, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var audience = await policy.ResolveAudienceVisibilityAsync(
            org, ws, session, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(audience.AudienceVisible);
        Assert.Empty(audience.SelectedVisibleParticipantIds);
    }

    [Fact]
    public async Task Audience_resolution_is_session_scoped()
    {
        // A reveal in session A does not contribute to session B's audience resolution (CORE-SVIS-001).
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible, sessionA.Id);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True((await policy.ResolveAudienceVisibilityAsync(
            org, ws, sessionA.Id, VisibilityResourceType.Scene, resourceId, CancellationToken.None)).AudienceVisible);
        Assert.False((await policy.ResolveAudienceVisibilityAsync(
            org, ws, sessionB.Id, VisibilityResourceType.Scene, resourceId, CancellationToken.None)).AudienceVisible);
    }

    [Fact]
    public async Task Audience_resolution_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var id = Guid.NewGuid();

        var sessionException = await Assert.ThrowsAsync<ArgumentException>(() => policy.ResolveAudienceVisibilityAsync(
            id, id, Guid.Empty, VisibilityResourceType.Entity, id, CancellationToken.None));
        Assert.Equal("sessionId", sessionException.ParamName);
        await Assert.ThrowsAsync<ArgumentException>(() => policy.ResolveAudienceVisibilityAsync(
            Guid.Empty, id, id, VisibilityResourceType.Entity, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.ResolveAudienceVisibilityAsync(
            id, Guid.Empty, id, VisibilityResourceType.Entity, id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.ResolveAudienceVisibilityAsync(
            id, id, id, VisibilityResourceType.Entity, Guid.Empty, CancellationToken.None));
    }

    // --- In-memory participant visible-set gate (CORE-PERF-004) ----------------

    private static VisibilityRule AudienceWideRule(
        Guid sessionId, VisibilityResourceType resourceType, Guid resourceId, VisibilityState visibility)
        => VisibilityRule.Create(Guid.NewGuid(), Guid.NewGuid(), sessionId, resourceType, resourceId, visibility, _createdAt);

    private static VisibilityRule ParticipantRule(
        Guid sessionId, VisibilityResourceType resourceType, Guid resourceId, Guid targetParticipantId, VisibilityState visibility)
        => VisibilityRule.CreateForParticipant(
            Guid.NewGuid(), Guid.NewGuid(), sessionId, resourceType, resourceId, targetParticipantId, visibility, _createdAt);

    private static VisibilityPolicy InMemoryPolicy() => new(new ThrowingVisibilityRuleRepository());

    [Fact]
    public void Gate_includes_an_audience_wide_visible_resource_for_any_participant()
    {
        var session = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var rules = new[] { AudienceWideRule(session, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible) };

        var visible = InMemoryPolicy().ComputeVisibleResourcesForParticipant(session, Guid.NewGuid(), rules);

        Assert.Equal(new[] { new VisibleResource(VisibilityResourceType.Scene, resourceId) }, visible);
    }

    [Fact]
    public void Gate_includes_a_private_reveal_only_for_its_target()
    {
        // The selected-participant guarantee, computed in memory: visible to the target, excluded for another.
        var session = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var rules = new[] { ParticipantRule(session, VisibilityResourceType.Entity, resourceId, selected, VisibilityState.Visible) };

        Assert.Equal(
            new[] { new VisibleResource(VisibilityResourceType.Entity, resourceId) },
            InMemoryPolicy().ComputeVisibleResourcesForParticipant(session, selected, rules));
        Assert.Empty(InMemoryPolicy().ComputeVisibleResourcesForParticipant(session, other, rules));
    }

    [Fact]
    public void Gate_excludes_hidden_rules()
    {
        // A hidden rule grants nothing — even a hidden private rule scoped to the participant (fail-closed).
        var session = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var rules = new[]
        {
            AudienceWideRule(session, VisibilityResourceType.Scene, Guid.NewGuid(), VisibilityState.Hidden),
            ParticipantRule(session, VisibilityResourceType.Entity, Guid.NewGuid(), participant, VisibilityState.Hidden),
        };

        Assert.Empty(InMemoryPolicy().ComputeVisibleResourcesForParticipant(session, participant, rules));
    }

    [Fact]
    public void Gate_is_session_scoped_and_excludes_another_sessions_rule()
    {
        // A visible rule of a CONCURRENT session of the same workspace never contributes (the cross-session
        // leak; threat T5/T3): the gate filters by BelongsToSession in memory.
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var inA = Guid.NewGuid();
        var inB = Guid.NewGuid();
        var rules = new[]
        {
            AudienceWideRule(sessionA, VisibilityResourceType.Scene, inA, VisibilityState.Visible),
            AudienceWideRule(sessionB, VisibilityResourceType.Scene, inB, VisibilityState.Visible),
        };

        Assert.Equal(
            new[] { new VisibleResource(VisibilityResourceType.Scene, inA) },
            InMemoryPolicy().ComputeVisibleResourcesForParticipant(sessionA, participant, rules));
    }

    [Fact]
    public void Gate_dedups_a_resource_with_several_granting_rules_and_orders_deterministically()
    {
        var session = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var scene = Guid.NewGuid();
        var content = Guid.NewGuid();
        var entity = Guid.NewGuid();
        var rules = new[]
        {
            // The same entity made visible by BOTH an audience-wide and a participant-scoped rule -> appears once.
            AudienceWideRule(session, VisibilityResourceType.Entity, entity, VisibilityState.Visible),
            ParticipantRule(session, VisibilityResourceType.Entity, entity, participant, VisibilityState.Visible),
            AudienceWideRule(session, VisibilityResourceType.ContentBlock, content, VisibilityState.Visible),
            AudienceWideRule(session, VisibilityResourceType.Scene, scene, VisibilityState.Visible),
        };

        var visible = InMemoryPolicy().ComputeVisibleResourcesForParticipant(session, participant, rules);

        // Deterministic order: by resource type (Scene=1, ContentBlock=2, Entity=3) then id; the entity once.
        Assert.Equal(
            new[]
            {
                new VisibleResource(VisibilityResourceType.Scene, scene),
                new VisibleResource(VisibilityResourceType.ContentBlock, content),
                new VisibleResource(VisibilityResourceType.Entity, entity),
            },
            visible);
    }

    [Fact]
    public void Gate_rejects_empty_ids_and_null_rules()
    {
        var policy = InMemoryPolicy();
        var rules = Array.Empty<VisibilityRule>();

        Assert.Throws<ArgumentException>(() => policy.ComputeVisibleResourcesForParticipant(Guid.Empty, Guid.NewGuid(), rules));
        Assert.Throws<ArgumentException>(() => policy.ComputeVisibleResourcesForParticipant(Guid.NewGuid(), Guid.Empty, rules));
        Assert.Throws<ArgumentNullException>(() => policy.ComputeVisibleResourcesForParticipant(Guid.NewGuid(), Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task Gate_matches_the_per_candidate_computation_over_the_same_rules()
    {
        // EQUIVALENCE (CORE-PERF-004 "feed correctness is unchanged"): the in-memory gate over a single
        // workspace-rule load returns EXACTLY the set the old per-candidate CanParticipantViewResourceAsync
        // loop produced, for a mixed workspace (audience-wide, private-to-me, private-to-other, hidden,
        // another session) and for two different participants.
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var me = (await SeedParticipantAsync(org, ws)).Id;
        var other = (await SeedParticipantAsync(org, ws)).Id;

        var audienceWide = Guid.NewGuid();
        var privateToMe = Guid.NewGuid();
        var privateToOther = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var inOtherSession = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, audienceWide, VisibilityState.Visible, sessionA.Id);
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, privateToMe, me, VisibilityState.Visible, sessionA.Id);
        await SeedParticipantRuleAsync(org, ws, VisibilityResourceType.Entity, privateToOther, other, VisibilityState.Visible, sessionA.Id);
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, hidden, VisibilityState.Hidden, sessionA.Id);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, inOtherSession, VisibilityState.Visible, sessionB.Id);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var policy = new VisibilityPolicy(repository);

        foreach (var participant in new[] { me, other })
        {
            // New path: single load + in-memory gate.
            var workspaceRules = await repository.ListByWorkspaceAsync(org, ws, CancellationToken.None);
            var gated = policy.ComputeVisibleResourcesForParticipant(sessionA.Id, participant, workspaceRules);

            // Old path: distinct candidates, each decided by the session-scoped per-participant policy.
            var candidates = workspaceRules
                .Select(rule => new VisibleResource(rule.ResourceType, rule.ResourceId))
                .Distinct()
                .OrderBy(resource => resource.ResourceType)
                .ThenBy(resource => resource.ResourceId)
                .ToArray();
            var perCandidate = new List<VisibleResource>();
            foreach (var candidate in candidates)
            {
                if (await policy.CanParticipantViewResourceAsync(
                        org, ws, sessionA.Id, participant, candidate.ResourceType, candidate.ResourceId, CancellationToken.None))
                {
                    perCandidate.Add(candidate);
                }
            }

            Assert.Equal(perCandidate, gated);
        }
    }

    // --- Guards ----------------------------------------------------------------

    [Fact]
    public async Task Empty_ids_are_rejected()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var session = Guid.NewGuid();
        var resource = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            Guid.Empty, ws, session, MembershipRole.Owner, VisibilityResourceType.Entity, resource, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            org, Guid.Empty, session, MembershipRole.Owner, VisibilityResourceType.Entity, resource, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            org, ws, session, MembershipRole.Owner, VisibilityResourceType.Entity, Guid.Empty, CancellationToken.None));
    }

    /// <summary>
    /// A repository whose every method throws — backs the in-memory gate tests, proving
    /// <see cref="VisibilityPolicy.ComputeVisibleResourcesForParticipant"/> issues NO database lookup
    /// (it computes purely over the rules passed in; CORE-PERF-004).
    /// </summary>
    private sealed class ThrowingVisibilityRuleRepository : IVisibilityRuleRepository
    {
        public Task<VisibilityRule?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<VisibilityRule?> FindByIdInSessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, Guid id, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<IReadOnlyList<VisibilityRule>> ListPageBySessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, int skip, int take, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
            Guid organizationId, Guid workspaceId, Guid sessionId, VisibilityResourceType resourceType, Guid resourceId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
            Guid organizationId, Guid workspaceId, Guid sessionId, IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");

        public Task<int> RemoveByResourceAsync(
            Guid organizationId, Guid workspaceId, VisibilityResourceType resourceType, Guid resourceId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The in-memory gate must not query the repository.");
    }
}
