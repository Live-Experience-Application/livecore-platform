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
/// HTTP integration tests for the reveal command (CORE-VIS-004,
/// <c>POST /api/v1/sessions/{sessionId}/reveal</c>). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON), so the documented request flow (authentication -> tenant context resolver -> endpoint ->
/// inline authorization -> idempotent command) is exercised end-to-end.
///
/// Coverage, per the story's required tests ("Negative authorization tests, idempotency tests,
/// projection tests"):
/// <list type="bullet">
///   <item>IDEMPOTENCY: the same <c>Idempotency-Key</c> twice returns 200 Applied then 200
///   AlreadyApplied with exactly ONE visibility rule (no duplicate effect).</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the seven-role workspace sweep (reveal
///   roles {Owner, Admin, Host, CoHost} -> 200 vs {Participant, Observer, Auditor} -> 403 with NO
///   effect); a non-member, a cross-tenant session and a malformed session id hidden as 404.</item>
///   <item>VALIDATION: a missing organizationSlug, a missing Idempotency-Key header, an unknown or
///   numeric resourceType, and an empty resourceId are each 400.</item>
/// </list>
/// Every denial/validation case asserts NO visibility rule was created, so a wrong-status pass that
/// also mutated state is caught. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RevealEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> RevealRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonRevealRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    [Fact]
    public async Task Reveal_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostRevealAsync(
            client, Guid.CreateVersion7(), Body(_orgA, "ContentBlock", Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_reveals_a_resource_and_it_becomes_visible()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(
            client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RevealDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Entity", body.ResourceType);
        Assert.Equal(resourceId, body.ResourceId);
        Assert.True(body.Visible);
        Assert.Equal(nameof(RevealOutcome.Applied), body.Outcome);

        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));

        // CORE-VIS-006: the change is audited, with the actor threaded from the authenticated principal
        // (the host's resolved user profile id) — end-to-end, not just at the service layer.
        var audit = await ListAuditAsync(factory, seed.OrganizationId);
        var entry = Assert.Single(audit);
        Assert.Equal(AuditAction.VisibilityRuleChanged, entry.Action);
        Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
        Assert.Equal("Entity", entry.ResourceType);
        Assert.Equal(resourceId, entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), entry.NewState);
        Assert.Equal(await SingleHostProfileIdAsync(factory), entry.ActorUserProfileId);

        // CORE-RT-003: the reveal appended a durable ContentRevealed event to the session's stream
        // (audience-wide, since no participant was targeted), carrying the resource ids in its payload.
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        var sessionEvent = Assert.Single(events);
        Assert.Equal(SessionEventTypes.ContentRevealed, sessionEvent.EventType);
        Assert.Equal(seed.SessionId, sessionEvent.SessionId);
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.Contains(resourceId.ToString(), sessionEvent.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_idempotency_key_does_not_apply_a_second_effect()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "ContentBlock", resourceId), "key-1");
        var second = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "ContentBlock", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<RevealDto>(_json);
        var secondBody = await second.Content.ReadFromJsonAsync<RevealDto>(_json);
        Assert.Equal(nameof(RevealOutcome.Applied), firstBody!.Outcome);
        Assert.Equal(nameof(RevealOutcome.AlreadyApplied), secondBody!.Outcome);

        // Exactly one rule: the retry produced no duplicate effect.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.ContentBlock, resourceId));

        // CORE-RT-003: exactly ONE ContentRevealed event — the idempotent retry emitted no second event.
        Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
    }

    [Theory]
    [MemberData(nameof(RevealRoles))]
    public async Task Reveal_is_200_for_a_reveal_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Scene", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, resourceId));
    }

    [Theory]
    [MemberData(nameof(NonRevealRoles))]
    public async Task Reveal_is_403_for_a_non_reveal_workspace_role_and_creates_no_rule(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Fact]
    public async Task Reveal_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
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

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Fact]
    public async Task Reveal_is_404_for_a_session_in_another_tenant()
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

        // Address the org-B session with organizationSlug = A (the caller's own org).
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await PostRevealAsync(client, seedB.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seedB.OrganizationId, seedB.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Reveal_is_404_for_a_malformed_or_empty_session_id(string sessionId)
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
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(Body(_orgA, "Entity", Guid.CreateVersion7()), options: _json),
        };
        request.Headers.Add("Idempotency-Key", "key-1");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(null, "Entity", Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_is_400_without_the_idempotency_key_header()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("Bogus")]
    [InlineData("")]
    public async Task Reveal_is_400_for_an_invalid_resource_type(string resourceType)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, resourceType, Guid.CreateVersion7()), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reveal_is_400_for_an_empty_resource_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", Guid.Empty), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Selected-participant reveal (CORE-VIS-005) ----------------------------

    [Fact]
    public async Task Host_reveals_to_a_selected_participant_and_only_they_can_see_it()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var selected = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var other = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", resourceId, selected), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RevealDto>(_json);
        Assert.Equal(selected, body!.ParticipantId);

        // The selected participant can see it; the other participant cannot — the crown jewel.
        Assert.True(await ParticipantCanViewAsync(factory, seed.OrganizationId, seed.WorkspaceId, selected, VisibilityResourceType.Entity, resourceId));
        Assert.False(await ParticipantCanViewAsync(factory, seed.OrganizationId, seed.WorkspaceId, other, VisibilityResourceType.Entity, resourceId));

        // CORE-RT-003: the ContentRevealed event is routed to the SELECTED participant, so the publisher
        // delivers it to that participant's group only (verified at the service level in
        // SessionEventPublisherTests); a non-selected participant is not in that group.
        var sessionEvent = Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(SessionEventTypes.ContentRevealed, sessionEvent.EventType);
        Assert.Equal(selected, sessionEvent.TargetParticipantId);
    }

    [Fact]
    public async Task Reveal_to_a_participant_of_another_workspace_is_404()
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
        var response = await PostRevealAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", resourceId, foreignParticipant), "key-1");

        // The target participant is not in the session's workspace: hidden as 404, no rule created.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    [Fact]
    public async Task Reveal_with_an_empty_participant_id_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", Guid.CreateVersion7(), Guid.Empty), "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        // Uses the public aggregate predicate: visible iff some rule is visible AND (audience-wide OR
        // scoped to this participant) — the same rule the Visibility policy applies.
        return rules.Any(rule => rule.IsVisibleTo(participantId));
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Seeds an org + Host-of-workspace caller + a Live session, all in org A.</summary>
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
    /// Returns the id of the single seeded user profile (the host). The reveal happy-path test seeds
    /// exactly one user, so this is the actor the audit record must capture.
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

    private sealed record RevealDto(string ResourceType, Guid ResourceId, bool Visible, string Outcome, Guid? ParticipantId);
}
