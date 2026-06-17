// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Text.Json;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the by-scene-id read (CORE-API-007,
/// <c>GET /api/v1/scenes/{sceneId}</c>). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> role projection) is exercised end-to-end.
///
/// Coverage, per the story's required integration + authorization-negative tests:
/// <list type="bullet">
///   <item>401 unauthenticated.</item>
///   <item>HOST-VS-PARTICIPANT DTO PROJECTION (the existing scene projection): the SAME route
///   returns the FULL host shape to {Owner, Admin, Host, CoHost, Auditor} ("View workspace
///   metadata" = yes) and the host-only-field-STRIPPED participant shape to
///   {Participant, Observer} ("View workspace metadata" = limited). The participant
///   response's EXACT top-level JSON property set is asserted to be {id, title, order}, so the
///   test FAILS if any host-only field or authorization rationale ever leaks (threats
///   T2/T7).</item>
///   <item>TENANT + WORKSPACE + SCENE isolation negatives: cross-tenant (a scene in org B
///   addressed with org A), cross-workspace (a Host of workspace X reading a scene in sibling
///   workspace Y of the same org), an org member who is not a member of the scene's workspace,
///   an unknown scene, and a malformed/empty scene id -> ALL hidden as 404 (never 403),
///   leaking no existence or rationale.</item>
///   <item>400 missing organizationSlug.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of
/// the allowed/denied sets, never an ordering comparison.
/// </summary>
public sealed class SceneByIdEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The workspace roles that receive the FULL host scene shape: docs/06 "View workspace
    /// metadata" = yes (Owner, Admin, Host, CoHost AND Auditor).
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
    /// The audience workspace roles that receive the STRIPPED participant scene shape: docs/06
    /// "View workspace metadata" = limited (Participant, Observer).
    /// </summary>
    public static TheoryData<MembershipRole> ParticipantShapeRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    // =====================================================================
    // 401 — unauthenticated. RequireAuthorization() challenges before any
    // handler runs (docs/08: missing/invalid auth).
    // =====================================================================

    [Fact]
    public async Task Get_scene_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/scenes/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HOST-VS-PARTICIPANT DTO PROJECTION by workspace role.
    // =====================================================================

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public async Task Get_scene_returns_the_host_shape_to_a_host_or_metadata_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sceneId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The host shape carries the host-only fields (the tenant/workspace boundary ids and
        // the host preparation timestamps).
        var properties = ScenePropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "title", "order", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));

        var scene = Deserialize<SceneDto>(body);
        Assert.Equal(sceneId, scene.Id);
        Assert.Equal("Opening", scene.Title);
        Assert.Equal(workspaceId, scene.WorkspaceId);
        Assert.NotEqual(Guid.Empty, scene.OrganizationId);
    }

    [Theory]
    [MemberData(nameof(ParticipantShapeRoles))]
    public async Task Get_scene_returns_the_stripped_participant_shape_to_an_audience_role(MembershipRole role)
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The EXACT top-level JSON property set of the participant shape is {id, title, order}
        // — and NOTHING else. This FAILS if any host-only field (organizationId, workspaceId,
        // createdAt, updatedAt) or any authorization-rationale field is ever added to the
        // participant DTO (docs/08; threats T2/T7).
        var properties = ScenePropertyNames(body);
        Assert.Equal(
            new[] { "id", "title", "order" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));

        Assert.DoesNotContain("organizationId", properties);
        Assert.DoesNotContain("workspaceId", properties);
        Assert.DoesNotContain("createdAt", properties);
        Assert.DoesNotContain("updatedAt", properties);

        // The whole body carries no authorization rationale wording (threat T7).
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visib", body, StringComparison.OrdinalIgnoreCase);

        var scene = Deserialize<ParticipantSceneDto>(body);
        Assert.Equal(sceneId, scene.Id);
        Assert.Equal("Opening", scene.Title);
        Assert.Equal(0, scene.Order);
    }

    // =====================================================================
    // TENANT + WORKSPACE + SCENE isolation negatives (404-hidden).
    // =====================================================================

    [Fact]
    public async Task Get_scene_is_404_for_a_scene_in_another_tenant()
    {
        // T5: a real scene in org B, owned by a caller who is a Host of its workspace, but
        // addressed with organizationSlug = A. The cross-tenant id is hidden as 404, never 403.
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Get_scene_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // T1/T5 cross-workspace WITHIN one tenant: a Host of workspace X cannot read a scene
        // that lives in sibling workspace Y of the SAME org. Workspace membership is checked
        // against the SCENE'S own workspace, so a control role in X never confers standing in
        // Y: hidden as 404 (not 403).
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Get_scene_is_404_for_an_org_member_who_is_not_a_member_of_the_scenes_workspace()
    {
        // T1: the caller is an org Owner in org A and the scene is in org A, but the caller is
        // NOT a member of the scene's workspace. A non-member must not learn the scene exists
        // -> hidden 404, not 403.
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Get_scene_is_404_for_an_unknown_scene_in_the_callers_org()
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
            $"/api/v1/scenes/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_scene_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_scene_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller IS a Host of the scene's workspace in org A but the token only asserts
        // org B. The claim mismatch denies before any membership is consulted; hidden as 404.
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

        // Token asserts only org B, not the targeted org A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Get_scene_is_400_without_the_organization_query_parameter()
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
        var response = await client.GetAsync($"/api/v1/scenes/{sceneId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("workspace-x", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace-y", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    private static T Deserialize<T>(string body)
    {
        var value = JsonSerializer.Deserialize<T>(body, _json);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Returns the EXACT set of top-level JSON property names on the scene response body (a
    /// single JSON object). This is the shape-leak guard that fails if a host-only field is
    /// ever added to the participant projection.
    /// </summary>
    private static string[] ScenePropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
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
}
