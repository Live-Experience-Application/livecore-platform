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
/// HTTP integration tests for the visibility-rule authoring API (CORE-SVIS-005): create, list and read
/// visibility rules under <c>/api/v1/sessions/{sessionId}/visibility-rules</c>. They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF
/// Core SQLite, foreign keys ON), so the documented request flow (authentication -&gt; tenant context
/// resolver -&gt; endpoint -&gt; authoring authorization -&gt; same-workspace-enforced create) is exercised
/// end-to-end.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>An authoring role creates a rule and lists/reads it; creation is AUDITED.</item>
///   <item>SAME-WORKSPACE invariant: a rule referencing a resource from another workspace (or an unknown
///   resource) is rejected with 400, creating nothing.</item>
///   <item>A PARTICIPANT cannot author OR enumerate rules (403 on create, list and read).</item>
///   <item>Negative authorization fails closed: 401 unauthenticated; a non-member, a cross-tenant session
///   and a malformed session id hidden as 404; a foreign/unknown/cross-session rule hidden as 404.</item>
///   <item>Validation: a missing organizationSlug, an unknown resourceType/visibility, an empty resourceId
///   and an empty participantId are each 400; a duplicate dimension is 409.</item>
/// </list>
/// Every denial/validation case asserts NO rule was created, so a wrong-status pass that also mutated state
/// is caught. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class VisibilityRuleEndpointTests
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
    // Happy path: create, list, read, audit.
    // =====================================================================

    [Fact]
    public async Task Host_creates_a_visibility_rule_and_it_is_audited_listed_and_read()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // CREATE.
        var createResponse = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Hidden"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Scene", created.ResourceType);
        Assert.Equal(sceneId, created.ResourceId);
        Assert.Equal(nameof(VisibilityState.Hidden), created.Visibility);
        Assert.Null(created.ParticipantId);

        // Exactly one rule persisted.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));

        // AUDITED (the story's "created rules are audited" criterion): one VisibilityRuleChanged record with
        // the actor threaded from the authenticated principal, previousState null (a fresh create), newState
        // the created visibility.
        var audit = await ListAuditAsync(factory, seed.OrganizationId);
        var entry = Assert.Single(audit);
        Assert.Equal(AuditAction.VisibilityRuleChanged, entry.Action);
        Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
        Assert.Equal("Scene", entry.ResourceType);
        Assert.Equal(sceneId, entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Null(entry.PreviousState);
        Assert.Equal(nameof(VisibilityState.Hidden), entry.NewState);
        Assert.Equal(await SingleHostProfileIdAsync(factory), entry.ActorUserProfileId);

        // Authoring is NOT a live reveal: no durable session events are emitted by the create.
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));

        // LIST returns the created rule.
        var listResponse = await client.GetAsync(ListUrl(seed.SessionId, _orgA));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var page = await listResponse.Content.ReadFromJsonAsync<PageDto<RuleDto>>(_json);
        Assert.NotNull(page);
        Assert.False(page.HasMore);
        var listed = Assert.Single(page.Items);
        Assert.Equal(created.Id, listed.Id);

        // READ by id returns the created rule.
        var readResponse = await client.GetAsync(ReadUrl(seed.SessionId, created.Id, _orgA));
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var read = await readResponse.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.Equal(created.Id, read!.Id);
        Assert.Equal(sceneId, read.ResourceId);
    }

    [Theory]
    [MemberData(nameof(AuthoringRoles))]
    public async Task Create_is_201_for_an_authoring_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Visible"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));
    }

    [Fact]
    public async Task Host_creates_a_rule_for_a_content_block_in_the_workspace()
    {
        // Exercises the ContentBlock branch of the same-workspace resource locator.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        Guid contentBlockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var scene = await db.AddSceneAsync(seed.OrganizationId, seed.WorkspaceId, "Scene", 0);
            var block = await db.AddContentBlockAsync(
                seed.OrganizationId, seed.WorkspaceId, scene.Id, LiveCore.Api.Content.ContentBlockType.Text, "hello");
            contentBlockId = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "ContentBlock", contentBlockId, "Visible"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.ContentBlock, contentBlockId));
    }

    [Fact]
    public async Task Host_creates_a_selected_participant_rule()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var participantId = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Scene", sceneId, "Visible", participantId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<RuleDto>(_json);
        Assert.Equal(participantId, created!.ParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), created.Visibility);
    }

    // =====================================================================
    // Same-workspace invariant (the story headline).
    // =====================================================================

    [Fact]
    public async Task Create_referencing_a_resource_from_another_workspace_is_400_and_creates_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        // A scene in a DIFFERENT workspace of the SAME organization.
        Guid foreignSceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var otherWorkspace = await db.AddWorkspaceAsync(seed.OrganizationId, "other-ws", "Other");
            var scene = await db.AddSceneAsync(seed.OrganizationId, otherWorkspace.Id, "Foreign", 0);
            foreignSceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", foreignSceneId, "Visible"));

        // The referenced resource is not in the session's workspace: rejected (400), nothing created.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, foreignSceneId));
    }

    [Fact]
    public async Task Create_referencing_an_unknown_resource_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Entity", Guid.CreateVersion7(), "Visible"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_a_resource_id_of_the_wrong_kind_is_400()
    {
        // A real scene id, but addressed as an Entity — the (type, id) pair does not resolve in the workspace
        // because the locator looks in the entities table, so the same-workspace check rejects it (no
        // cross-table id confusion).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Entity", sceneId, "Visible"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_for_a_participant_of_another_workspace_is_404_and_creates_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        // A participant in a DIFFERENT workspace of the same org.
        Guid foreignParticipant = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var otherWorkspace = await db.AddWorkspaceAsync(seed.OrganizationId, "other-ws", "Other");
            var participant = await db.AddParticipantAsync(seed.OrganizationId, otherWorkspace.Id, userProfileId: null);
            foreignParticipant = participant.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Scene", sceneId, "Visible", foreignParticipant));

        // The target participant is not in the session's workspace: hidden as 404, no rule created.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));
    }

    // =====================================================================
    // Duplicate.
    // =====================================================================

    [Fact]
    public async Task A_second_rule_in_the_same_dimension_is_409()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var first = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Hidden"));
        var second = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Visible"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Exactly one rule: the duplicate created nothing.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));
    }

    // =====================================================================
    // Negative authorization (fail-closed).
    // =====================================================================

    [Fact]
    public async Task Create_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostRuleAsync(
            client, Guid.CreateVersion7(), Body(_orgA, "Scene", Guid.CreateVersion7(), "Visible"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task Create_is_403_for_a_non_authoring_role_and_creates_no_rule(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Visible"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task A_participant_cannot_enumerate_or_read_rules(MembershipRole role)
    {
        // A non-authoring member exists in the session's workspace, but visibility rules are an authoring
        // artifact: they may neither LIST nor READ them (403), so a participant cannot enumerate rules.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedSessionAsync(factory, subject, role);

        // Seed a rule directly so a wrong-status pass that actually returned data would be visible.
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var rule = await db.AddVisibilityRuleAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
            ruleId = rule.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var listResponse = await client.GetAsync(ListUrl(seed.SessionId, _orgA));
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);

        var readResponse = await client.GetAsync(ReadUrl(seed.SessionId, ruleId, _orgA));
        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
    }

    [Fact]
    public async Task Create_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        SeedResult seed = default;
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 0);
            sceneId = scene.Id;
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, "Visible"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Scene, sceneId));
    }

    [Fact]
    public async Task Read_is_404_for_a_rule_in_another_tenant()
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

        // Address the org-B session/rule with organizationSlug = A (the caller's own org).
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(ReadUrl(seedB.SessionId, ruleId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_rule()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(ReadUrl(seed.SessionId, Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_a_rule_of_another_session_in_the_same_workspace()
    {
        // The by-id read is SESSION-scoped: a rule created in session 1 is not reachable through session 2's
        // route, even in the same workspace (the cross-session isolation).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        Guid otherSessionId = Guid.Empty;
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var otherSession = await db.AddSessionAsync(seed.OrganizationId, seed.WorkspaceId, "Other", SessionStatus.Live);
            otherSessionId = otherSession.Id;
            var rule = await db.AddVisibilityRuleAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
            ruleId = rule.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // The rule belongs to seed.SessionId; reading it through otherSessionId's route is a hidden 404.
        var response = await client.GetAsync(ReadUrl(otherSessionId, ruleId, _orgA));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // ...but reading it through its OWN session succeeds (proves the 404 above is the session scope, not a
        // missing rule).
        var ownResponse = await client.GetAsync(ReadUrl(seed.SessionId, ruleId, _orgA));
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
    }

    // =====================================================================
    // Validation (after authorization).
    // =====================================================================

    [Fact]
    public async Task Create_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(null, "Scene", Guid.CreateVersion7(), "Visible"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Create_is_404_for_a_malformed_or_empty_session_id(string sessionId)
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/visibility-rules", Body(_orgA, "Scene", Guid.CreateVersion7(), "Visible"), _json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("Bogus")]
    [InlineData("")]
    public async Task Create_is_400_for_an_invalid_resource_type(string resourceType)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, resourceType, Guid.CreateVersion7(), "Visible"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("Bogus")]
    [InlineData("")]
    public async Task Create_is_400_for_an_invalid_visibility(string visibility)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", sceneId, visibility));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_400_for_an_empty_resource_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(client, seed.SessionId, Body(_orgA, "Scene", Guid.Empty, "Visible"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_an_empty_participant_id_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var sceneId = await SeedSceneAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRuleAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Scene", sceneId, "Visible", Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_is_400_for_a_malformed_limit_after_authorization()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"{ListUrl(seed.SessionId, _orgA)}&limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, string resourceType, Guid resourceId, string visibility)
        => new { organizationSlug, resourceType, resourceId, visibility };

    private static object BodyForParticipant(
        string? organizationSlug, string resourceType, Guid resourceId, string visibility, Guid participantId)
        => new { organizationSlug, resourceType, resourceId, visibility, participantId };

    private static string ListUrl(Guid sessionId, string organizationSlug)
        => $"/api/v1/sessions/{sessionId}/visibility-rules?organizationSlug={organizationSlug}";

    private static string ReadUrl(Guid sessionId, Guid ruleId, string organizationSlug)
        => $"/api/v1/sessions/{sessionId}/visibility-rules/{ruleId}?organizationSlug={organizationSlug}";

    private static Task<HttpResponseMessage> PostRuleAsync(HttpClient client, Guid sessionId, object body)
        => client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/visibility-rules", body, _json);

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

    private static async Task<Guid> SeedSceneAsync(WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId)
    {
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var scene = await db.AddSceneAsync(organizationId, workspaceId, "Scene", 0);
            sceneId = scene.Id;
        });
        return sceneId;
    }

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
            .ToListAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    private sealed record RuleDto(
        Guid Id,
        string ResourceType,
        Guid ResourceId,
        string Visibility,
        Guid? ParticipantId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record PageDto<T>(int Offset, int Limit, bool HasMore, IReadOnlyList<T> Items);
}
