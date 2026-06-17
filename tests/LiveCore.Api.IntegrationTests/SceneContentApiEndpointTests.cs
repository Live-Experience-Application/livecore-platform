// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the scene content APIs (CORE-SCENE-003): the three routes
/// <c>GET /api/v1/workspaces/{workspaceId}/scenes</c>,
/// <c>POST /api/v1/workspaces/{workspaceId}/scenes</c> and
/// <c>POST /api/v1/scenes/{sceneId}/content-blocks</c>. They drive the real application
/// over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme +
/// EF Core SQLite, foreign keys ON), so the documented request flow (authentication ->
/// tenant context resolver -> endpoint -> inline authorization) is exercised end-to-end
/// exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATHS: list scenes (ordered, membership-visible); create scene (201, with
///   the second created scene appended to a HIGHER order than the first); create content
///   block (201, initial revision number = 1).</item>
///   <item>401 unauthenticated on each route.</item>
///   <item>The full seven-role WORKSPACE-membership sweep on BOTH write routes: allowed
///   {Owner, Admin, Host, CoHost} vs denied {Participant, Observer, Auditor} -> 403, every
///   denial asserting NO side effect (nothing persisted).</item>
///   <item>GET list: any workspace member allowed; a non-member is 404-hidden.</item>
///   <item>GET list HOST-VS-PARTICIPANT DTO PROJECTION (CORE-SCENE-004): the SAME route
///   returns the FULL host shape to {Owner, Admin, Host, CoHost, Auditor} ("View
///   workspace metadata" = yes) and the host-only-field-STRIPPED participant shape to
///   {Participant, Observer} ("View workspace metadata" = limited). The participant
///   response's EXACT top-level JSON property set is asserted to be {id, title, order},
///   so the test FAILS if any host-only field (organization/workspace id, host
///   timestamps) or any authorization rationale ever leaks into it (threats T2/T7).</item>
///   <item>TENANT + WORKSPACE + SCENE isolation negatives: cross-tenant (claim mismatch /
///   foreign org), cross-workspace (a Host of workspace X cannot create a scene in sibling
///   workspace Y, and cannot add a content block to a scene in sibling Y), unknown/foreign
///   scene for the content-block route -> ALL hidden as 404 (never 403), each asserting no
///   state change.</item>
///   <item>400 missing organizationSlug on the content-block route.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit
/// enumerations of the allowed/denied sets, never an ordering comparison. Every denial
/// asserts the SPECIFIC status code, asserts no state change where a side effect was
/// possible, and asserts the Problem Details body leaks no existence/rationale.
/// </summary>
public sealed class SceneContentApiEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The scene/content write roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> WriteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace-member roles that may NOT create a scene or content block (the
    /// audience and audit roles). These are the 403 cases.
    /// </summary>
    public static TheoryData<MembershipRole> NonWriteRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on EACH route. RequireAuthorization() challenges
    // before any handler runs (docs/08: missing/invalid auth).
    // =====================================================================

    [Fact]
    public async Task List_scenes_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_scene_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/scenes",
            new CreateSceneRequest(_orgA, "Opening"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_content_block_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{Guid.CreateVersion7()}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATHS.
    // =====================================================================

    [Fact]
    public async Task List_scenes_returns_the_workspaces_scenes_in_order_for_a_host()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // A Host receives the full HOST shape (CORE-SCENE-004 projection by role).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            // Seed out of order to prove the endpoint returns the (scene_order, id) order.
            await db.AddSceneAsync(org.Id, workspace.Id, "Third", 2);
            await db.AddSceneAsync(org.Id, workspace.Id, "First", 0);
            await db.AddSceneAsync(org.Id, workspace.Id, "Second", 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scenes = await ReadHostScenesAsync(response);
        Assert.Equal(3, scenes.Length);
        // Deterministic ordering by Order.
        Assert.Equal(new[] { 0, 1, 2 }, scenes.Select(s => s.Order).ToArray());
        Assert.Equal(new[] { "First", "Second", "Third" }, scenes.Select(s => s.Title).ToArray());
        // The HOST shape carries the boundaries (the host-only fields).
        Assert.All(scenes, s => Assert.Equal(workspaceId, s.WorkspaceId));
        Assert.All(scenes, s => Assert.NotEqual(Guid.Empty, s.OrganizationId));
    }

    [Fact]
    public async Task List_scenes_for_an_empty_workspace_is_200_and_empty()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scenes = await ReadHostScenesAsync(response);
        Assert.Empty(scenes);
    }

    // =====================================================================
    // LIST — host-vs-participant DTO PROJECTION by workspace role
    // (CORE-SCENE-004, csv/api_routes.csv line 15 "Projection by role").
    // The SAME route returns a DIFFERENT DTO SHAPE depending on the caller's
    // workspace role: the FULL host shape to the host-capable/metadata roles
    // (Owner/Admin/Host/CoHost/Auditor, "View workspace metadata" = yes in
    // docs/06) and the host-only-field-STRIPPED participant shape to the audience
    // roles (Participant/Observer, "View workspace metadata" = limited). The SET of
    // scenes is identical for every role — only the per-scene SHAPE differs.
    // =====================================================================

    /// <summary>
    /// The workspace roles that receive the FULL host scene shape: docs/06 "View
    /// workspace metadata" = yes (Owner, Admin, Host, CoHost AND Auditor).
    /// </summary>
    public static TheoryData<MembershipRole> HostShapeRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Auditor,
    ];

    /// <summary>
    /// The audience workspace roles that receive the STRIPPED participant scene
    /// shape: docs/06 "View workspace metadata" = limited (Participant, Observer).
    /// </summary>
    public static TheoryData<MembershipRole> ParticipantShapeRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public async Task List_scenes_returns_the_host_shape_to_a_host_or_metadata_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Read the body ONCE (the HttpContent stream is single-read), then parse the
        // shape and the typed DTOs from it.
        var body = await response.Content.ReadAsStringAsync();

        // The host shape carries the host-only fields (the tenant/workspace boundary
        // ids and the host preparation timestamps).
        var properties = FirstScenePropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "title", "order", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        // The full host DTO deserializes with its boundaries populated (from the page envelope's items).
        var scenes = Deserialize<PageDto<SceneDto>>(body).Items;
        var scene = Assert.Single(scenes);
        Assert.Equal("Opening", scene.Title);
        Assert.Equal(workspaceId, scene.WorkspaceId);
        Assert.NotEqual(Guid.Empty, scene.OrganizationId);
    }

    [Theory]
    [MemberData(nameof(ParticipantShapeRoles))]
    public async Task List_scenes_returns_the_stripped_participant_shape_to_an_audience_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Read the body ONCE (the HttpContent stream is single-read), then parse the
        // shape and the typed DTOs from it.
        var body = await response.Content.ReadAsStringAsync();

        // The EXACT top-level JSON property set of the participant shape is
        // {id, title, order} — and NOTHING else. This assertion FAILS if any
        // host-only field (organizationId, workspaceId, createdAt, updatedAt) or any
        // authorization-rationale field is ever added to the participant DTO
        // (docs/08: "Participant DTOs must not contain hidden content fields";
        // "Never include internal authorization rationale"; threats T2/T7).
        var properties = FirstScenePropertyNames(body);
        Assert.Equal(
            new[] { "id", "title", "order" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));

        // Explicitly assert NONE of the host-only fields leaked into the participant
        // response body (a direct, field-by-field T2 leak guard).
        Assert.DoesNotContain("organizationId", properties);
        Assert.DoesNotContain("workspaceId", properties);
        Assert.DoesNotContain("createdAt", properties);
        Assert.DoesNotContain("updatedAt", properties);

        // The whole body carries no authorization rationale wording (threat T7).
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visib", body, StringComparison.OrdinalIgnoreCase);

        // The participant shape still deserializes its audience-safe fields, and the
        // participant still receives ALL of the workspace's scenes (the SET is
        // unchanged; only the SHAPE is stripped). The scenes are under the page envelope's items.
        var scenes = Deserialize<PageDto<ParticipantSceneDto>>(body).Items;
        var scene = Assert.Single(scenes);
        Assert.Equal("Opening", scene.Title);
        Assert.Equal(0, scene.Order);
        Assert.NotEqual(Guid.Empty, scene.Id);
    }

    [Fact]
    public async Task Create_scene_is_201_and_appends_to_the_end_of_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First scene into an empty workspace -> the first order (0).
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "First Scene"));
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await ReadSceneAsync(firstResponse);
        Assert.Equal("First Scene", first.Title);
        Assert.Equal(0, first.Order);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(workspaceId, first.WorkspaceId);
        Assert.Equal($"/api/v1/scenes/{first.Id}", firstResponse.Headers.Location?.ToString());

        // Second scene -> appended to a strictly HIGHER order than the first.
        var secondResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "Second Scene"));
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = await ReadSceneAsync(secondResponse);
        Assert.True(second.Order > first.Order, "The appended scene must get a higher order than the first.");

        // Both scenes are persisted in the workspace.
        await AssertSceneCountAsync(factory, workspaceId, 2);
    }

    [Fact]
    public async Task Create_content_block_is_201_at_the_initial_revision()
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "Welcome to the show"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var block = await ReadContentBlockAsync(response);
        Assert.Equal(sceneId, block.SceneId);
        Assert.Equal(nameof(ContentBlockType.Text), block.Type);
        Assert.Equal("Welcome to the show", block.Body);
        Assert.Equal(ContentBlock.InitialRevisionNumber, block.RevisionNumber);
        Assert.Equal(1, block.RevisionNumber);
        Assert.NotEqual(Guid.Empty, block.Id);

        await AssertContentBlockCountAsync(factory, sceneId, 1);
    }

    // =====================================================================
    // WRITE-ROLE SWEEP — create scene.
    // =====================================================================

    [Theory]
    [MemberData(nameof(WriteRoles))]
    public async Task Create_scene_is_201_for_a_write_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertSceneCountAsync(factory, workspaceId, 1);
    }

    [Theory]
    [MemberData(nameof(NonWriteRoles))]
    public async Task Create_scene_is_403_for_a_non_write_workspace_role_and_persists_nothing(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        // The denial must not have created a scene.
        await AssertSceneCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // WRITE-ROLE SWEEP — create content block.
    // =====================================================================

    [Theory]
    [MemberData(nameof(WriteRoles))]
    public async Task Create_content_block_is_201_for_a_write_workspace_role(MembershipRole role)
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
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Data), "{}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertContentBlockCountAsync(factory, sceneId, 1);
    }

    [Theory]
    [MemberData(nameof(NonWriteRoles))]
    public async Task Create_content_block_is_403_for_a_non_write_workspace_role_and_persists_nothing(MembershipRole role)
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
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "hidden"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    // =====================================================================
    // GET list — non-member is 404-hidden (never leaks the workspace exists).
    // =====================================================================

    [Fact]
    public async Task List_scenes_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            // The caller is an Owner of the ORG but not a member of the workspace.
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task List_scenes_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: a real workspace with scenes in org B, addressed with organizationSlug = A.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            await db.AddSceneAsync(orgB.Id, workspaceInB.Id, "B Opening", 0);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // CREATE SCENE — tenant + workspace isolation negatives (404-hidden).
    // =====================================================================

    [Fact]
    public async Task Create_scene_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // T1/T5 cross-workspace WITHIN one tenant: a Host of workspace X cannot create a
        // scene in sibling workspace Y of the SAME org. The membership check is against
        // the ROUTE'S workspace, so a control role held in X never confers standing in Y:
        // hidden as 404 (not 403) and no scene is created in Y.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            workspaceYId = workspaceY.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceYId}/scenes",
            new CreateSceneRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertSceneCountAsync(factory, workspaceYId, 0);
    }

    [Fact]
    public async Task Create_scene_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller IS a Host in org A but the token only asserts org B. The claim
        // mismatch denies before any membership is consulted; hidden as 404, no scene.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        // Token asserts only org B, not the targeted org A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSceneCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // CREATE CONTENT BLOCK — tenant + workspace + scene isolation (404-hidden)
    // and 400.
    // =====================================================================

    [Fact]
    public async Task Create_content_block_is_404_for_a_scene_in_another_tenant()
    {
        // T5: a real scene in org B, owned by a caller who is a Host of its workspace, but
        // addressed with organizationSlug = A. The cross-tenant id is hidden as 404, never
        // 403, and no content block is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sceneInBId = Guid.Empty;
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
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneInBId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockCountAsync(factory, sceneInBId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // T1/T5 cross-workspace WITHIN one tenant: a Host of workspace X cannot add a
        // content block to a scene that lives in sibling workspace Y of the SAME org.
        // Workspace membership is checked against the SCENE'S own workspace, so a control
        // role in X never confers standing in Y: hidden as 404 and nothing is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid sceneInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var scene = await db.AddSceneAsync(org.Id, workspaceY.Id, "Y Opening", 0);
            sceneInYId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneInYId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertContentBlockCountAsync(factory, sceneInYId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_404_for_an_org_member_who_is_not_a_member_of_the_scenes_workspace()
    {
        // T1: the caller is an org Owner in org A and the scene is in org A, but the caller
        // is NOT a member of the scene's workspace. A non-member must not learn the scene
        // exists -> hidden 404, not 403, and nothing is created.
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
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_404_for_an_unknown_scene_in_the_callers_org()
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
            $"/api/v1/scenes/{Guid.CreateVersion7()}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Create_content_block_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
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
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_content_block_is_400_without_the_organization_query_parameter()
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), "x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A 400 on the query parameter must not have created a content block.
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    // =====================================================================
    // 400 VALIDATION — create content block (invalid type / body / payload).
    // Validation runs before tenant/scene/role resolution, so the caller is a
    // legitimate Host of the scene's workspace: only the invalid input blocks the
    // request, and nothing is persisted.
    // =====================================================================

    [Theory]
    [InlineData("999")] // numeric, out-of-range — must never be smuggled past the defined-type check
    [InlineData("1")] // numeric — even a value that maps to a defined ordinal is rejected (names only)
    [InlineData("Bogus")] // unknown name
    [InlineData("")] // blank
    [InlineData(null)] // missing
    public async Task Create_content_block_is_400_for_an_invalid_type(string? type)
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(type, "a valid body"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_content_block_is_400_for_a_blank_body(string? body)
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Text), body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_400_for_a_malformed_json_data_body()
    {
        // Per-type validation (CORE-SCENE-005): a Data body must be well-formed JSON. A
        // malformed-JSON body is a type-aware 400 BEFORE persistence; the bad body must not
        // leak into the Problem Details (threat T7) and nothing is persisted.
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

        const string malformed = "{ this-is-not-valid-json ";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Data), malformed));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertBodyContentDoesNotLeakAsync(response, malformed);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Theory]
    [InlineData(nameof(ContentBlockType.Text))]
    [InlineData(nameof(ContentBlockType.Media))]
    [InlineData(nameof(ContentBlockType.Data))]
    public async Task Create_content_block_is_400_for_an_oversize_body_for_its_type(string type)
    {
        // Per-type size limits (CORE-SCENE-005): a body over its type's maximum length is a
        // type-aware 400 before persistence. The oversize content is never echoed (T7) and
        // nothing is persisted.
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

        var parsedType = Enum.Parse<ContentBlockType>(type);
        var limit = ContentValidator.MaxLengthFor(parsedType);
        // The oversize body opens with a distinctive marker so the no-leak assertion below
        // checks a recognisable slice rather than an incidental run of padding. For Data the
        // body stays a well-formed JSON string literal padded past the Data limit, so only
        // SIZE (not well-formedness) trips the rejection.
        const string leakMarker = "do-not-echo-marker-";
        var oversize = parsedType == ContentBlockType.Data
            ? "\"" + leakMarker + new string('a', limit) + "\""
            : leakMarker + new string('a', limit + 1);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(type, oversize));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertBodyContentDoesNotLeakAsync(response, oversize);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_400_for_a_media_body_with_a_control_character()
    {
        // Per-type validation (CORE-SCENE-005): a Media reference with a disallowed control
        // character is an invalid per-type body -> type-aware 400, no leak, nothing persisted.
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

        // A disallowed control character (NUL) embedded in the reference behind a
        // distinctive marker. PostAsJsonAsync encodes the NUL as a unicode escape, which the
        // server decodes back to a NUL char that the per-type validator rejects (only
        // tab/CR/LF are allowed).
        var badReference = "asset://do-not-echo-marker" + (char)0 + "bad";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}",
            new CreateContentBlockRequest(nameof(ContentBlockType.Media), badReference));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertBodyContentDoesNotLeakAsync(response, badReference);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    [Fact]
    public async Task Create_content_block_is_400_for_a_missing_request_body()
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
        // A literal JSON null body binds to a null request -> 400 (no block created).
        var response = await client.PostAsJsonAsync<CreateContentBlockRequest?>(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertContentBlockCountAsync(factory, sceneId, 0);
    }

    // =====================================================================
    // 400 VALIDATION — create scene (invalid title / missing payload).
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_scene_is_400_for_an_invalid_title(string? title)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, title));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSceneCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_scene_is_400_for_a_missing_request_body()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // A literal JSON null body binds to a null request -> 400 (no scene created).
        var response = await client.PostAsJsonAsync<CreateSceneRequest?>(
            $"/api/v1/workspaces/{workspaceId}/scenes", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSceneCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertSceneCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.Scenes.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    private static async Task AssertContentBlockCountAsync(WorkspaceApiFactory factory, Guid sceneId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.ContentBlocks.AsNoTracking().CountAsync(b => b.SceneId == sceneId);
        Assert.Equal(expected, count);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no scene/tenant existence or
    /// authorization rationale (threat T7): it carries only the generic title/detail used
    /// for every denial, with no id, slug, role or "why" wording.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("workspace-x", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace-y", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asserts a validation (400) Problem Details body never echoes the rejected content
    /// body (threat T7): host-prepared content must not leak back through an error
    /// response. A distinctive marker substring of the submitted body must NOT appear in
    /// the response.
    /// </summary>
    private static async Task AssertBodyContentDoesNotLeakAsync(HttpResponseMessage response, string submittedBody)
    {
        var body = await response.Content.ReadAsStringAsync();
        // Use a distinctive, sufficiently long slice of the submitted body so the check is
        // not fooled by an incidental short overlap with the generic Problem Details text.
        var marker = submittedBody.Length <= 16 ? submittedBody : submittedBody.Substring(0, 16);
        Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
    }

    private static async Task<SceneDto[]> ReadHostScenesAsync(HttpResponseMessage response)
    {
        // The scene list is a bounded page envelope (CORE-DX-003): read the page and return its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<SceneDto>>(_json);
        Assert.NotNull(page);
        return page.Items.ToArray();
    }

    private static async Task<SceneDto> ReadSceneAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<SceneDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    /// <summary>Deserializes a scene-list response body that has already been read once.</summary>
    private static T Deserialize<T>(string body)
    {
        var value = JsonSerializer.Deserialize<T>(body, _json);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Returns the EXACT set of top-level JSON property names on the FIRST scene of a
    /// scene-list response body. The body is the bounded page envelope (CORE-DX-003), so the scenes are under
    /// its <c>items</c> array; this digs into <c>items[0]</c>. This is the shape-leak guard that fails if a
    /// host-only field is ever added to the participant projection.
    /// </summary>
    private static string[] FirstScenePropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        var first = items[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        return first.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static async Task<ContentBlockDto> ReadContentBlockAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<ContentBlockDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private sealed record SceneDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        string Title,
        int Order,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ParticipantSceneDto(
        Guid Id,
        string Title,
        int Order);

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
}
