// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the content-block list/read APIs (CORE-CB-001, the "Vertical Authoring and Read
/// API Completeness" epic): the two routes
/// <c>GET /api/v1/scenes/{sceneId}/content-blocks</c> and
/// <c>GET /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}</c>. They drive the real application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON), so the documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: an authoring (host) role lists a scene's content blocks (200, full host shape WITH the
///   body, deterministic id order) and reads one back by id (200, full host shape with the body).</item>
///   <item>A participant gets only the projected, body-stripped content via the projection — exactly
///   {id, type}, never the body content or any internal field (T2).</item>
///   <item>The host vs participant responses differ EXACTLY on the hidden fields: the host carries the full
///   shape (with the body) and the participant carries only {id, type}; the difference is precisely the
///   host-only fields, and the body content appears only in the host response.</item>
///   <item>A foreign-tenant or wrong-scene block is hidden-404 (read by id and list, addressed with the wrong
///   org or through a sibling scene/workspace).</item>
///   <item>List returns only SAME-SCENE blocks (a sibling scene's block is never included).</item>
///   <item>Negatives: 401 unauthenticated; non-member hidden-404; unknown/malformed block 404; missing
///   organizationSlug 400.</item>
/// </list>
///
/// A content block IS content, so the host-vs-participant DTO split is the "View host-only content" row of
/// docs/06 (Owner/Admin/Host/CoHost get the full shape WITH the body; Participant/Observer/Auditor get the
/// body-stripped shape) — the same alignment as the entity projection. <see cref="MembershipRole"/> is
/// non-linear, so the role sweeps are explicit enumerations, never an ordering comparison; every denial
/// asserts the SPECIFIC status code and that the Problem Details body leaks no existence/rationale
/// (threats T1/T5/T7).
/// </summary>
public sealed class ContentBlockListReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive marker embedded in the content block's body, used to prove the stripped participant
    // projection never echoes the body content (T2).
    private const string _bodyMarker = "do-not-leak-body-marker";
    private static readonly string _body = $"{_bodyMarker} hidden host content";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The workspace roles that receive the FULL host content-block shape: docs/06 "View host-only content"
    /// = yes (Owner, Admin, Host, CoHost). A content block is content, so — unlike the scene metadata
    /// projection — Auditor is NOT here.
    /// </summary>
    public static TheoryData<MembershipRole> HostShapeRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace roles that receive the STRIPPED, audience-safe content-block shape: the audience roles
    /// (Participant, Observer) AND the audit role (Auditor, "View host-only content" = audit-only, not yes).
    /// </summary>
    public static TheoryData<MembershipRole> StrippedShapeRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on EACH route.
    // =====================================================================

    [Fact]
    public async Task List_content_blocks_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/scenes/{Guid.CreateVersion7()}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_content_block_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/scenes/{Guid.CreateVersion7()}/content-blocks/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — a host lists a scene's blocks and reads one back.
    // =====================================================================

    [Fact]
    public async Task List_content_blocks_returns_the_scenes_blocks_in_id_order_for_a_host()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        var blockIds = new List<Guid>();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            blockIds.Add((await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "First")).Id);
            blockIds.Add((await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Data, "{}")).Id);
            blockIds.Add((await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Media, "asset://x")).Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blocks = await ReadHostBlocksAsync(response);
        Assert.Equal(3, blocks.Length);
        // The repository returns blocks in deterministic (UUIDv7) id order.
        Assert.Equal(blockIds.OrderBy(id => id).ToArray(), blocks.Select(b => b.Id).ToArray());
        // The HOST shape carries the body and the boundaries (the host-only fields).
        Assert.All(blocks, b => Assert.Equal(sceneId, b.SceneId));
        Assert.All(blocks, b => Assert.NotEqual(Guid.Empty, b.OrganizationId));
        Assert.All(blocks, b => Assert.Equal(ContentBlock.InitialRevisionNumber, b.RevisionNumber));
    }

    [Fact]
    public async Task List_content_blocks_for_an_empty_scene_is_200_and_empty()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blocks = await ReadHostBlocksAsync(response);
        Assert.Empty(blocks);
    }

    [Fact]
    public async Task List_content_blocks_returns_only_the_routes_scene_blocks()
    {
        // Two scenes in ONE workspace; the list for scene X must NOT include scene Y's block (scene-scoped).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneXId = Guid.Empty;
        Guid blockXId = Guid.Empty;
        Guid blockYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var sceneX = await db.AddSceneAsync(org.Id, workspace.Id, "Scene X", 0);
            sceneXId = sceneX.Id;
            blockXId = (await db.AddContentBlockAsync(org.Id, workspace.Id, sceneX.Id, ContentBlockType.Text, "X1")).Id;
            var sceneY = await db.AddSceneAsync(org.Id, workspace.Id, "Scene Y", 1);
            blockYId = (await db.AddContentBlockAsync(org.Id, workspace.Id, sceneY.Id, ContentBlockType.Text, "Y1")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneXId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blocks = await ReadHostBlocksAsync(response);
        var ids = blocks.Select(b => b.Id).ToArray();
        Assert.Single(ids);
        Assert.Contains(blockXId, ids);
        Assert.DoesNotContain(blockYId, ids);
    }

    [Fact]
    public async Task Get_content_block_by_id_returns_the_host_shape_with_the_body_to_a_host()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            blockId = (await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var block = await ReadHostBlockAsync(response);
        Assert.Equal(blockId, block.Id);
        Assert.Equal(orgId, block.OrganizationId);
        Assert.Equal(workspaceId, block.WorkspaceId);
        Assert.Equal(sceneId, block.SceneId);
        Assert.Equal(nameof(ContentBlockType.Text), block.Type);
        Assert.Equal(_body, block.Body);
        Assert.Equal(ContentBlock.InitialRevisionNumber, block.RevisionNumber);
    }

    // =====================================================================
    // LIST/READ — host-vs-participant DTO PROJECTION by workspace role.
    // =====================================================================

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public async Task List_content_blocks_returns_the_host_shape_to_a_host_content_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var properties = FirstElementPropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "sceneId", "type", "body", "revisionNumber", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));

        // The host shape DOES carry the body content.
        Assert.Contains(_bodyMarker, body, StringComparison.Ordinal);
        var blocks = Deserialize<PageDto<ContentBlockDto>>(body).Items;
        var block = Assert.Single(blocks);
        Assert.Equal(_body, block.Body);
    }

    [Theory]
    [MemberData(nameof(StrippedShapeRoles))]
    public async Task List_content_blocks_returns_the_stripped_participant_shape_to_an_audience_or_audit_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The EXACT top-level property set of the participant shape is {id, type} — and NOTHING else. This
        // FAILS if any host-only field (the body content, the tenant/workspace/scene ids, the revision number,
        // the host timestamps) or any authorization rationale is ever added to the participant DTO (T2/T7).
        var properties = FirstElementPropertyNames(body);
        Assert.Equal(
            new[] { "id", "type" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("body", properties);
        Assert.DoesNotContain("organizationId", properties);
        Assert.DoesNotContain("workspaceId", properties);
        Assert.DoesNotContain("sceneId", properties);
        Assert.DoesNotContain("revisionNumber", properties);
        Assert.DoesNotContain("createdAt", properties);
        Assert.DoesNotContain("updatedAt", properties);

        // The body CONTENT never appears anywhere in the response (a direct T2 content-leak guard).
        Assert.DoesNotContain(_bodyMarker, body, StringComparison.Ordinal);

        // The participant still receives the block (the SET is unchanged; only the SHAPE is stripped).
        var blocks = Deserialize<PageDto<ParticipantContentBlockDto>>(body).Items;
        var block = Assert.Single(blocks);
        Assert.Equal(nameof(ContentBlockType.Text), block.Type);
        Assert.NotEqual(Guid.Empty, block.Id);
    }

    [Fact]
    public async Task Get_content_block_by_id_returns_the_stripped_participant_shape_to_a_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            blockId = (await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var properties = ObjectPropertyNames(body);
        Assert.Equal(
            new[] { "id", "type" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(_bodyMarker, body, StringComparison.Ordinal);

        var block = Deserialize<ParticipantContentBlockDto>(body);
        Assert.Equal(blockId, block.Id);
        Assert.Equal(nameof(ContentBlockType.Text), block.Type);
    }

    [Fact]
    public async Task Host_and_participant_by_id_responses_differ_exactly_on_the_hidden_fields()
    {
        // The SAME block, read by a host and by a participant: the host carries the full shape (with the body);
        // the participant carries only {id, type}; the difference is EXACTLY the host-only/hidden fields, and
        // the body content appears only in the host response (T2).
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        const string participantSubject = "participant-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var host = await db.AddUserAsync(_issuer, hostSubject);
            var participant = await db.AddUserAsync(_issuer, participantSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, host.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, participant.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, host.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, participant.Id, MembershipRole.Participant);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            blockId = (await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body)).Id;
        });

        var url = $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}";

        using var hostClient = factory.CreateClientFor(hostSubject, _issuer, _orgA);
        var hostResponse = await hostClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, hostResponse.StatusCode);
        var hostBody = await hostResponse.Content.ReadAsStringAsync();
        var hostProps = ObjectPropertyNames(hostBody);

        using var participantClient = factory.CreateClientFor(participantSubject, _issuer, _orgA);
        var participantResponse = await participantClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, participantResponse.StatusCode);
        var participantBody = await participantResponse.Content.ReadAsStringAsync();
        var participantProps = ObjectPropertyNames(participantBody);

        // The participant shape is exactly the audience-safe subset {id, type}.
        Assert.Equal(
            new[] { "id", "type" }.OrderBy(n => n, StringComparer.Ordinal),
            participantProps.OrderBy(n => n, StringComparer.Ordinal));

        // The host shape is the full shape.
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "sceneId", "type", "body", "revisionNumber", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            hostProps.OrderBy(n => n, StringComparer.Ordinal));

        // The two responses differ EXACTLY on the hidden (host-only) fields.
        var difference = hostProps.Except(participantProps).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "body", "createdAt", "organizationId", "revisionNumber", "sceneId", "updatedAt", "workspaceId" },
            difference);
        // The shared fields are exactly the audience-safe identity {id, type}.
        Assert.Equal(
            new[] { "id", "type" }.OrderBy(n => n, StringComparer.Ordinal),
            hostProps.Intersect(participantProps).OrderBy(n => n, StringComparer.Ordinal));

        // The body content is present for the host and absent for the participant (T2).
        Assert.Contains(_bodyMarker, hostBody, StringComparison.Ordinal);
        Assert.DoesNotContain(_bodyMarker, participantBody, StringComparison.Ordinal);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real block in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Get_and_list_content_blocks_are_404_for_a_scene_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sceneInBId = Guid.Empty;
        Guid blockInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(orgB.Id, workspaceInB.Id, "B Opening", 0);
            sceneInBId = scene.Id;
            blockInBId = (await db.AddContentBlockAsync(orgB.Id, workspaceInB.Id, scene.Id, ContentBlockType.Text, _body)).Id;
        });

        // The scene and block are real and in org B, but addressed with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var readResponse = await client.GetAsync(
            $"/api/v1/scenes/{sceneInBId}/content-blocks/{blockInBId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);

        var listResponse = await client.GetAsync(
            $"/api/v1/scenes/{sceneInBId}/content-blocks?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);
        await AssertNoRationaleLeakAsync(listResponse);
    }

    [Fact]
    public async Task Get_content_block_by_id_is_404_when_addressed_through_a_sibling_scene()
    {
        // T1/T5 wrong-scene WITHIN one workspace: a block that lives in scene Y addressed through scene X is
        // hidden as 404 — the lookup is scoped to the route's scene, so the block's own scene id (Y) never
        // matches scene X.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneXId = Guid.Empty;
        Guid blockInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var sceneX = await db.AddSceneAsync(org.Id, workspace.Id, "Scene X", 0);
            sceneXId = sceneX.Id;
            var sceneY = await db.AddSceneAsync(org.Id, workspace.Id, "Scene Y", 1);
            blockInYId = (await db.AddContentBlockAsync(org.Id, workspace.Id, sceneY.Id, ContentBlockType.Text, _body)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneXId}/content-blocks/{blockInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Get_content_block_by_id_is_404_when_addressed_through_a_sibling_workspace()
    {
        // T1/T5 cross-workspace WITHIN one tenant: a block in sibling workspace Y addressed through a scene of
        // workspace X (which the caller hosts) is hidden as 404 — the scene-id route component belongs to Y, so
        // the scene is never found within X's context, and a control role in X never reaches Y's content.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid sceneInYId = Guid.Empty;
        Guid blockInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var sceneY = await db.AddSceneAsync(org.Id, workspaceY.Id, "Y Opening", 0);
            sceneInYId = sceneY.Id;
            blockInYId = (await db.AddContentBlockAsync(org.Id, workspaceY.Id, sceneY.Id, ContentBlockType.Text, _body)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneInYId}/content-blocks/{blockInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the scene's workspace.
    // =====================================================================

    [Fact]
    public async Task List_content_blocks_is_404_for_an_org_member_who_is_not_a_member_of_the_scenes_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // GET BY ID — safe 404s and 400.
    // =====================================================================

    [Fact]
    public async Task Get_content_block_by_id_is_404_for_an_unknown_block_in_the_callers_scene()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_content_block_by_id_is_404_for_a_malformed_or_empty_block_id(string blockId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks/{blockId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task List_content_blocks_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
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
        var response = await client.GetAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_content_blocks_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}/content-blocks");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_content_block_by_id_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        Guid blockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
            blockId = (await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, _body)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}/content-blocks/{blockId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no block/tenant existence or authorization rationale
    /// (threat T7): it carries only the generic title/detail used for every denial.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_bodyMarker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace-x", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace-y", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ContentBlockDto[]> ReadHostBlocksAsync(HttpResponseMessage response)
    {
        // The content-block list is a bounded page envelope (CORE-DX-003): read the page and return its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<ContentBlockDto>>(_json);
        Assert.NotNull(page);
        return page.Items.ToArray();
    }

    private static async Task<ContentBlockDto> ReadHostBlockAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<ContentBlockDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static T Deserialize<T>(string body)
    {
        var value = JsonSerializer.Deserialize<T>(body, _json);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Returns the EXACT set of top-level JSON property names on the FIRST item of a content-block LIST
    /// response body. The body is the bounded page envelope (CORE-DX-003), so the blocks are under its
    /// <c>items</c> array; this digs into <c>items[0]</c>. The shape-leak guard that fails if a host-only field
    /// is ever added to the participant projection.
    /// </summary>
    private static string[] FirstElementPropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        var first = items[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        return first.EnumerateObject().Select(p => p.Name).ToArray();
    }

    /// <summary>Returns the EXACT set of top-level JSON property names on a single JSON-object response body.</summary>
    private static string[] ObjectPropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private sealed record ContentBlockDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SceneId,
        string Type,
        string Body,
        int RevisionNumber,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ParticipantContentBlockDto(
        Guid Id,
        string Type);
}
