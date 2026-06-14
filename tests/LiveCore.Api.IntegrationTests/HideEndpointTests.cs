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
/// HTTP integration tests for the hide / un-reveal command (CORE-REV-001, the "Reveal Lifecycle" hide,
/// <c>POST /api/v1/sessions/{sessionId}/hide</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so
/// the documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization -> idempotent command -> audit + event) is exercised end-to-end.
///
/// Coverage, per the story's required tests ("reveal then hide removes visibility for audience and
/// selected participant; negative role/tenant tests; idempotent double-hide"):
/// <list type="bullet">
///   <item>REVEAL THEN HIDE: an audience-wide reveal then hide leaves the resource hidden from the
///   audience; a selected reveal then hide leaves it hidden from that participant. Both audit the
///   Visible -> Hidden change and emit a durable <c>ContentHidden</c> event with NO visibility subject
///   (so the audience that must remove it is reached by coarse routing).</item>
///   <item>IDEMPOTENT DOUBLE-HIDE: the same <c>Idempotency-Key</c> twice returns 200 Applied then 200
///   AlreadyApplied with exactly ONE rule (now hidden) and exactly ONE <c>ContentHidden</c> event.</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the workspace role sweep (hide roles {Owner,
///   Admin, Host, CoHost} -> 200 vs {Participant, Observer, Auditor} -> 403 with NO effect); a non-member,
///   a cross-tenant session and a malformed session id hidden as 404.</item>
///   <item>VALIDATION: a missing organizationSlug, a missing Idempotency-Key header, an unknown or
///   numeric resourceType, an empty resourceId, an empty participantId, and a participant of another
///   workspace are each rejected (400/404) with NO effect.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class HideEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> HideRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonHideRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // --- Reveal then hide (the acceptance scenario) ----------------------------

    [Fact]
    public async Task Reveal_then_hide_removes_audience_wide_visibility_and_audits_and_emits()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var revealed = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-reveal");
        Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));

        var hidden = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-hide");

        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var body = await hidden.Content.ReadFromJsonAsync<HideDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Entity", body.ResourceType);
        Assert.Equal(resourceId, body.ResourceId);
        Assert.False(body.Visible);
        Assert.Equal(nameof(HideOutcome.Applied), body.Outcome);

        // The audience no longer sees the resource: the rule is now hidden.
        Assert.False(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));

        // CORE-VIS-006: the Visible -> Hidden change is audited alongside the reveal's audit entry.
        var audit = await ListAuditAsync(factory, seed.OrganizationId);
        Assert.Equal(2, audit.Count);
        var hideEntry = Assert.Single(audit, entry => entry.NewState == nameof(VisibilityState.Hidden));
        Assert.Equal(AuditAction.VisibilityRuleChanged, hideEntry.Action);
        Assert.Equal("Entity", hideEntry.ResourceType);
        Assert.Equal(resourceId, hideEntry.ResourceId);
        Assert.Null(hideEntry.TargetParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), hideEntry.PreviousState);
        Assert.Equal(await SingleHostProfileIdAsync(factory), hideEntry.ActorUserProfileId);

        // CORE-EVT-003: the reveal appended ContentRevealed + VisibilityRuleChanged(Visible); the hide
        // appends ContentHidden + VisibilityRuleChanged(Hidden) — four events total. The ContentHidden is
        // audience-wide (no target) and carries NO visibility subject, so the recipient resolver delivers it
        // to the observers and every active participant by coarse routing (not gated on the now-hidden
        // resource). The hide's VisibilityRuleChanged, by contrast, DOES carry the now-hidden subject, so it
        // is gated to the hosts only — a participant never receives a hidden-resource event.
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ContentRevealed);
        var hideEvent = Assert.Single(events, e => e.EventType == SessionEventTypes.ContentHidden);
        Assert.Equal(seed.SessionId, hideEvent.SessionId);
        Assert.Null(hideEvent.TargetParticipantId);
        Assert.Contains(resourceId.ToString(), hideEvent.Payload, StringComparison.Ordinal);
        Assert.Null(hideEvent.VisibilitySubjectType);
        Assert.Null(hideEvent.VisibilitySubjectId);

        var ruleHidden = Assert.Single(
            events,
            e => e.EventType == SessionEventTypes.VisibilityRuleChanged
                && e.Payload.Contains(nameof(VisibilityState.Hidden), StringComparison.Ordinal));
        Assert.Equal("Entity", ruleHidden.VisibilitySubjectType);
        Assert.Equal(resourceId, ruleHidden.VisibilitySubjectId);
        Assert.Null(ruleHidden.TargetParticipantId);
    }

    [Fact]
    public async Task Reveal_then_hide_removes_visibility_for_the_selected_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var selected = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var other = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        await PostRevealAsync(client, seed.SessionId, BodyForParticipant(_orgA, "ContentBlock", resourceId, selected), "key-reveal");
        Assert.True(await ParticipantCanViewAsync(factory, seed.OrganizationId, seed.WorkspaceId, selected, VisibilityResourceType.ContentBlock, resourceId));

        var hidden = await PostHideAsync(client, seed.SessionId, BodyForParticipant(_orgA, "ContentBlock", resourceId, selected), "key-hide");

        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var body = await hidden.Content.ReadFromJsonAsync<HideDto>(_json);
        Assert.Equal(selected, body!.ParticipantId);

        // The selected participant no longer sees it; the other participant never did.
        Assert.False(await ParticipantCanViewAsync(factory, seed.OrganizationId, seed.WorkspaceId, selected, VisibilityResourceType.ContentBlock, resourceId));
        Assert.False(await ParticipantCanViewAsync(factory, seed.OrganizationId, seed.WorkspaceId, other, VisibilityResourceType.ContentBlock, resourceId));

        // The ContentHidden event is routed to the selected participant (coarse target), no subject.
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        var hideEvent = Assert.Single(events, e => e.EventType == SessionEventTypes.ContentHidden);
        Assert.Equal(selected, hideEvent.TargetParticipantId);
        Assert.Null(hideEvent.VisibilitySubjectType);
        Assert.Null(hideEvent.VisibilitySubjectId);
    }

    // --- Idempotent double-hide ------------------------------------------------

    [Fact]
    public async Task The_same_idempotency_key_does_not_apply_a_second_hide()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        await SeedVisibleRuleAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.ContentBlock, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await PostHideAsync(client, seed.SessionId, Body(_orgA, "ContentBlock", resourceId), "key-1");
        var second = await PostHideAsync(client, seed.SessionId, Body(_orgA, "ContentBlock", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<HideDto>(_json);
        var secondBody = await second.Content.ReadFromJsonAsync<HideDto>(_json);
        Assert.Equal(nameof(HideOutcome.Applied), firstBody!.Outcome);
        Assert.Equal(nameof(HideOutcome.AlreadyApplied), secondBody!.Outcome);

        // Exactly one rule, now hidden: the retry produced no duplicate effect.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.ContentBlock, resourceId));
        Assert.False(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.ContentBlock, resourceId));

        // The first hide emitted its two durable events (ContentHidden + VisibilityRuleChanged) and the
        // idempotent retry emitted none, so exactly two events total; one audit record too.
        Assert.Equal(2, (await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId)).Count);
        Assert.Single(await ListAuditAsync(factory, seed.OrganizationId));
    }

    [Fact]
    public async Task Hide_of_a_resource_with_no_rule_is_200_with_no_effect()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Absence already means hidden: no rule created, no audit, no event.
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.Empty(await ListAuditAsync(factory, seed.OrganizationId));
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    // --- Authorization + isolation ---------------------------------------------

    [Fact]
    public async Task Hide_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostHideAsync(
            client, Guid.CreateVersion7(), Body(_orgA, "ContentBlock", Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HideRoles))]
    public async Task Hide_is_200_for_a_hide_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, role);
        await SeedVisibleRuleAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Scene, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Scene", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, resourceId));
    }

    [Theory]
    [MemberData(nameof(NonHideRoles))]
    public async Task Hide_is_403_for_a_non_hide_workspace_role_and_leaves_the_rule_visible(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, role);
        await SeedVisibleRuleAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Entity, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The hide did not take effect: the resource is still visible.
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Fact]
    public async Task Hide_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        var resourceId = Guid.CreateVersion7();
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        await SeedVisibleRuleAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Entity, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // A non-member's request never takes effect: the resource stays visible.
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Fact]
    public async Task Hide_is_404_for_a_session_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        var resourceId = Guid.CreateVersion7();
        SeedResult seedB = default;
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
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, session.Id);
        });
        await SeedVisibleRuleAsync(factory, seedB.OrganizationId, seedB.WorkspaceId, seedB.SessionId, VisibilityResourceType.Entity, resourceId);

        // Address the org-B session with organizationSlug = A (the caller's own org).
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await PostHideAsync(client, seedB.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The cross-tenant attempt never takes effect: org-B's resource stays visible.
        Assert.True(await ResourceVisibleAsync(factory, seedB.OrganizationId, seedB.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Hide_is_404_for_a_malformed_or_empty_session_id(string sessionId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/hide")
        {
            Content = JsonContent.Create(Body(_orgA, "Entity", Guid.CreateVersion7()), options: _json),
        };
        request.Headers.Add("Idempotency-Key", "key-1");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Validation ------------------------------------------------------------

    [Fact]
    public async Task Hide_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(null, "Entity", Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hide_is_400_without_the_idempotency_key_header()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        await SeedVisibleRuleAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Entity, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A missing key is rejected before any effect: the resource stays visible.
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("Bogus")]
    [InlineData("")]
    public async Task Hide_is_400_for_an_invalid_resource_type(string resourceType)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, resourceType, Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hide_is_400_for_an_empty_resource_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(client, seed.SessionId, Body(_orgA, "Entity", Guid.Empty), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hide_with_an_empty_participant_id_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", Guid.CreateVersion7(), Guid.Empty), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hide_for_a_participant_of_another_workspace_is_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        // A participant in a DIFFERENT workspace of the same org.
        Guid foreignParticipant = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var otherWorkspace = await db.AddWorkspaceAsync(seed.OrganizationId, "other-ws", "Other");
            var participant = await db.AddParticipantAsync(seed.OrganizationId, otherWorkspace.Id, userProfileId: null);
            foreignParticipant = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostHideAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", resourceId, foreignParticipant), "key-1");

        // The target participant is not in the session's workspace: hidden as 404, no rule created.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, string resourceType, Guid resourceId)
        => new { organizationSlug, resourceType, resourceId };

    private static object BodyForParticipant(string? organizationSlug, string resourceType, Guid resourceId, Guid participantId)
        => new { organizationSlug, resourceType, resourceId, participantId };

    private static async Task<Guid> SeedParticipantAsync(WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId)
    {
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var participant = await db.AddParticipantAsync(organizationId, workspaceId, userProfileId: null);
            participantId = participant.Id;
        });
        return participantId;
    }

    /// <summary>
    /// Seeds an audience-wide VISIBLE rule for the resource IN THE GIVEN SESSION, so a hide has something
    /// to flip (the hide is session-scoped, CORE-SVIS-001, so the rule must be in the session being hidden).
    /// </summary>
    private static async Task SeedVisibleRuleAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rule = VisibilityRule.Create(
            organizationId, workspaceId, sessionId, resourceType, resourceId, VisibilityState.Visible, DateTimeOffset.UtcNow);
        context.VisibilityRules.Add(rule);
        await context.SaveChangesAsync();
    }

    private static async Task<bool> ParticipantCanViewAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        Guid participantId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rules = await context.VisibilityRules.AsNoTracking()
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .ToListAsync();
        return rules.Any(rule => rule.IsVisibleTo(participantId));
    }

    private static async Task<HttpResponseMessage> PostHideAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/hide")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    /// <summary>Seeds an org + caller of the given role in the workspace + a Live session, all in org A.</summary>
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

    private static async Task<int> RuleCountAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.VisibilityRules.AsNoTracking()
            .CountAsync(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId);
    }

    private static async Task<bool> ResourceVisibleAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rules = await context.VisibilityRules.AsNoTracking()
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .ToListAsync();
        return rules.Any(rule => rule.IsVisibleToAudience());
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(
        WorkspaceApiFactory factory,
        Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the id of the single seeded user profile (the host). The happy-path test seeds exactly one
    /// user, so this is the actor the audit record must capture.
    /// </summary>
    private static async Task<Guid> SingleHostProfileIdAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.UserProfiles.AsNoTracking().Select(profile => profile.Id).SingleAsync();
    }

    private static async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    private sealed record HideDto(string ResourceType, Guid ResourceId, bool Visible, string Outcome, Guid? ParticipantId);
}
