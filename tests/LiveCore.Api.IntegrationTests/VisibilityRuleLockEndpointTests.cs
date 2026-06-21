// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the visibility-rule SEAL (lock) / unseal (unlock) commands (CORE-VSEAL-001,
/// the "Scheduled and Sealed Visibility" epic): <c>POST /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/lock</c>
/// and <c>.../unlock</c>, plus the fail-closed <c>409</c> a reveal/hide returns when it targets a locked rule.
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test
/// authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; authoring authorization -&gt; lock effect)
/// and the gating of the reveal/hide commands are exercised end-to-end.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>Locking a rule then attempting a reveal/hide is <c>409</c> fail-closed (the resource stays
///   unchanged); an unlocked rule still reveals.</item>
///   <item>Only the authoring roles can lock or unlock (the {Owner, Admin, Host, CoHost} -&gt; 200 vs
///   {Participant, Observer, Auditor} -&gt; 403 sweep); 401 unauthenticated.</item>
///   <item>The locked flag is projected on the rule (lock/unlock response and the by-id read) and the lock
///   change is audited (VisibilityRuleLockChanged); the command is idempotent.</item>
///   <item>Fail-closed and hidden-404: a foreign-tenant, cross-session, unknown rule and a non-member of the
///   session's workspace are all an indistinguishable 404; a missing organizationSlug is 400.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class VisibilityRuleLockEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> AuthoringRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonAuthoringRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // Happy path: lock projects the flag + audits; reveal is then 409; unlock restores.
    // =====================================================================

    [Fact]
    public async Task Host_locks_a_rule_the_flag_is_projected_and_audited_and_a_reveal_is_then_409()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var resourceId = Guid.CreateVersion7();
        var ruleId = await SeedRuleAsync(factory, seed, resourceId, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // LOCK: the response carries the updated locked flag.
        var lockResponse = await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        var locked = await lockResponse.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.True(locked!.Locked);

        // AUDITED: a VisibilityRuleLockChanged record with the Unlocked -> Locked transition.
        var lockEntry = Assert.Single(await ListAuditAsync(factory, seed.OrganizationId));
        Assert.Equal(AuditAction.VisibilityRuleLockChanged, lockEntry.Action);
        Assert.Equal(seed.WorkspaceId, lockEntry.WorkspaceId);
        Assert.Equal("Unlocked", lockEntry.PreviousState);
        Assert.Equal("Locked", lockEntry.NewState);

        // The by-id read projects the locked flag too.
        var read = await client.GetFromJsonAsync<RuleDto>(ReadUrl(seed.SessionId, ruleId, _orgA), _json);
        Assert.True(read!.Locked);

        // A reveal targeting the locked rule's resource is refused fail-closed (409) and changes nothing.
        var revealResponse = await PostRevealAsync(client, seed.SessionId, resourceId, "key-1");
        Assert.Equal(HttpStatusCode.Conflict, revealResponse.StatusCode);
        Assert.False(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, resourceId));
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));

        // A hide is refused the same way.
        var hideResponse = await PostHideAsync(client, seed.SessionId, resourceId, "key-2");
        Assert.Equal(HttpStatusCode.Conflict, hideResponse.StatusCode);
    }

    [Fact]
    public async Task Unlocking_restores_the_rule_so_a_reveal_succeeds_again()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var resourceId = Guid.CreateVersion7();
        var ruleId = await SeedRuleAsync(factory, seed, resourceId, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        Assert.Equal(HttpStatusCode.OK, (await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true)).StatusCode);
        // While locked, the reveal is 409.
        Assert.Equal(HttpStatusCode.Conflict, (await PostRevealAsync(client, seed.SessionId, resourceId, "key-1")).StatusCode);

        // UNLOCK restores the rule.
        var unlockResponse = await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: false);
        Assert.Equal(HttpStatusCode.OK, unlockResponse.StatusCode);
        var unlocked = await unlockResponse.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.False(unlocked!.Locked);

        // The reveal now succeeds and the resource becomes visible (an unlocked rule behaves exactly as before).
        var revealResponse = await PostRevealAsync(client, seed.SessionId, resourceId, "key-2");
        Assert.Equal(HttpStatusCode.OK, revealResponse.StatusCode);
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, resourceId));
    }

    [Fact]
    public async Task An_unlocked_rule_reveals_normally_without_any_lock_interaction()
    {
        // Control: a never-locked rule reveals exactly as before (locked defaults false; no regression).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var resourceId = Guid.CreateVersion7();
        var ruleId = await SeedRuleAsync(factory, seed, resourceId, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var read = await client.GetFromJsonAsync<RuleDto>(ReadUrl(seed.SessionId, ruleId, _orgA), _json);
        Assert.False(read!.Locked);

        var revealResponse = await PostRevealAsync(client, seed.SessionId, resourceId, "key-1");
        Assert.Equal(HttpStatusCode.OK, revealResponse.StatusCode);
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, resourceId));
    }

    [Theory]
    [MemberData(nameof(AuthoringRoles))]
    public async Task Lock_and_unlock_are_200_for_an_authoring_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role);
        var ruleId = await SeedRuleAsync(factory, seed, Guid.CreateVersion7(), VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        Assert.Equal(HttpStatusCode.OK, (await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: false)).StatusCode);
    }

    [Fact]
    public async Task Re_locking_an_already_locked_rule_is_idempotent_200_with_no_second_audit()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var ruleId = await SeedRuleAsync(factory, seed, Guid.CreateVersion7(), VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        Assert.Equal(HttpStatusCode.OK, (await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true)).StatusCode);
        // Re-locking is a no-op that still returns 200 (and the rule stays locked).
        var second = await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rule = await second.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.True(rule!.Locked);

        // Exactly ONE audit record: the no-op re-lock audits nothing.
        Assert.Single(await ListAuditAsync(factory, seed.OrganizationId));
    }

    // =====================================================================
    // Negative authorization (fail-closed).
    // =====================================================================

    [Fact]
    public async Task Lock_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostLockAsync(client, Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA, locked: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task Lock_is_403_for_a_non_authoring_role_and_does_not_change_the_lock(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role);
        var ruleId = await SeedRuleAsync(factory, seed, Guid.CreateVersion7(), VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The rule is unchanged: still unlocked, nothing audited.
        Assert.False(await RuleLockedAsync(factory, seed.OrganizationId, seed.WorkspaceId, ruleId));
        Assert.Empty(await ListAuditAsync(factory, seed.OrganizationId));
    }

    [Fact]
    public async Task Lock_is_404_for_an_unknown_rule()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostLockAsync(client, seed.SessionId, Guid.CreateVersion7(), _orgA, locked: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lock_is_404_for_a_rule_of_another_session_in_the_same_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var ruleId = await SeedRuleAsync(factory, seed, Guid.CreateVersion7(), VisibilityState.Hidden);

        Guid otherSessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var otherSession = await db.AddSessionAsync(seed.OrganizationId, seed.WorkspaceId, "Other", SessionStatus.Live);
            otherSessionId = otherSession.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // The rule belongs to seed.SessionId; locking it through otherSessionId's route is a hidden 404.
        var response = await PostLockAsync(client, otherSessionId, ruleId, _orgA, locked: true);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await RuleLockedAsync(factory, seed.OrganizationId, seed.WorkspaceId, ruleId));
    }

    [Fact]
    public async Task Lock_is_404_for_a_rule_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        SeedResult seedB = default;
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B", SessionStatus.Live);
            var rule = await db.AddVisibilityRuleAsync(
                orgB.Id, workspaceInB.Id, session.Id, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
            ruleId = rule.Id;
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, session.Id);
        });

        // Address the org-B session/rule with organizationSlug = A (the caller's own org): hidden 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await PostLockAsync(client, seedB.SessionId, ruleId, _orgA, locked: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await RuleLockedAsync(factory, seedB.OrganizationId, seedB.WorkspaceId, ruleId));
    }

    [Fact]
    public async Task Lock_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        SeedResult seed = default;
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var rule = await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
            ruleId = rule.Id;
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostLockAsync(client, seed.SessionId, ruleId, _orgA, locked: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await RuleLockedAsync(factory, seed.OrganizationId, seed.WorkspaceId, ruleId));
    }

    [Fact]
    public async Task Lock_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var ruleId = await SeedRuleAsync(factory, seed, Guid.CreateVersion7(), VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/sessions/{seed.SessionId}/visibility-rules/{ruleId}/lock", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static string ReadUrl(Guid sessionId, Guid ruleId, string organizationSlug)
        => $"/api/v1/sessions/{sessionId}/visibility-rules/{ruleId}?organizationSlug={organizationSlug}";

    private static Task<HttpResponseMessage> PostLockAsync(
        HttpClient client, Guid sessionId, Guid ruleId, string organizationSlug, bool locked)
    {
        var action = locked ? "lock" : "unlock";
        return client.PostAsync(
            $"/api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/{action}?organizationSlug={organizationSlug}",
            content: null);
    }

    private static Task<HttpResponseMessage> PostRevealAsync(HttpClient client, Guid sessionId, Guid resourceId, string key)
        => PostVisibilityChangeAsync(client, sessionId, "reveal", resourceId, key);

    private static Task<HttpResponseMessage> PostHideAsync(HttpClient client, Guid sessionId, Guid resourceId, string key)
        => PostVisibilityChangeAsync(client, sessionId, "hide", resourceId, key);

    private static Task<HttpResponseMessage> PostVisibilityChangeAsync(
        HttpClient client, Guid sessionId, string action, Guid resourceId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/{action}")
        {
            Content = JsonContent.Create(new { organizationSlug = _orgA, resourceType = "Entity", resourceId }, options: _json),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    /// <summary>Seeds an org + a workspace caller with the given role + a Live session, all in org A.</summary>
    private static async Task<SeedResult> SeedSessionAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    private static async Task<Guid> SeedRuleAsync(
        WorkspaceApiFactory factory, SeedResult seed, Guid resourceId, VisibilityState visibility)
    {
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var rule = await db.AddVisibilityRuleAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Entity, resourceId, visibility);
            ruleId = rule.Id;
        });
        return ruleId;
    }

    private static async Task<bool> RuleLockedAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid ruleId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rule = await context.VisibilityRules.AsNoTracking()
            .SingleAsync(r => r.OrganizationId == organizationId && r.WorkspaceId == workspaceId && r.Id == ruleId);
        return rule.Locked;
    }

    private static async Task<bool> ResourceVisibleAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rules = await context.VisibilityRules.AsNoTracking()
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == VisibilityResourceType.Entity
                && rule.ResourceId == resourceId)
            .ToListAsync();
        return rules.Any(rule => rule.IsVisibleToAudience());
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(WorkspaceApiFactory factory, Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .ToListAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    private sealed record RuleDto(
        Guid Id,
        string ResourceType,
        Guid ResourceId,
        string? ResourceLabel,
        string Visibility,
        Guid? ParticipantId,
        bool Locked,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
