// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the scene reorder route (CORE-SCENE-006, the "Authoring Completeness" epic):
/// <c>POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder</c>. They drive the real application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON; or the PostgreSQL leg on CI), so the documented request flow (authentication -> tenant context
/// resolver -> workspace resolution -> endpoint -> inline authorization -> reorder service) is exercised
/// end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH / "reordering a scene yields the expected (scene_order, id) sequence": a host moves a
///   scene -> 200, the response body and the persisted rows show the re-packed contiguous, gap-free,
///   duplicate-free ordering.</item>
///   <item>401 unauthenticated.</item>
///   <item>The reorder-ROLE sweep: allowed {Owner, Admin, Host, CoHost} -> 200 vs denied {Participant,
///   Observer, Auditor} -> 403, every denial asserting the order is unchanged (no side effect) and no
///   rationale/body leak.</item>
///   <item>"out-of-range or foreign scene is hidden-404": an unknown scene id and a real scene in another
///   tenant both -> 404 (never 403), the ordering unchanged.</item>
///   <item>A non-member of the route's workspace (an org Owner who is not a workspace member) -> 404-hidden,
///   never 403; malformed/empty ids -> 404; 400 missing organizationSlug; 400 negative target index; 409 on
///   an archived (read-only) workspace.</item>
///   <item>"two concurrent reorders converge without duplicate orders (with CORE-CONC-006)": two concurrent
///   reorders leave a valid permutation (contiguous, no duplicates), every response a well-formed 200/409;
///   and, on the Postgres leg, an interleaved reorder whose row is held by a concurrent writer surfaces a
///   deterministic 409 over HTTP.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison.
/// </summary>
public sealed class SceneReorderEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset _commandTime = TestData.SeedTime.AddMinutes(5);

    // A distinctive scene title so the leak assertions can prove host-prepared content never appears in a
    // denial (threat T7).
    private const string _secretTitle = "top-secret-scene-title";

    /// <summary>The scene-reorder roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> ReorderRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT reorder a scene (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonReorderRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task Reorder_scene_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/scenes/{Guid.CreateVersion7()}/reorder",
            new ReorderSceneRequest(_orgA, 0));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — reorder yields the expected (scene_order, id) sequence.
    // =====================================================================

    [Fact]
    public async Task Reorder_scene_is_200_and_yields_the_expected_sequence()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        Guid sceneC = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
            sceneC = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene C", 2)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Move the LAST scene (C, order 2) to the front (index 0) -> [C, A, B].
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneC}/reorder",
            new ReorderSceneRequest(_orgA, 0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The response body is the re-packed list in the new (scene_order, id) sequence.
        var body = await response.Content.ReadFromJsonAsync<SceneRow[]>(_json);
        Assert.NotNull(body);
        Assert.Equal(new[] { sceneC, sceneA, sceneB }, body!.Select(s => s.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, body.Select(s => s.Order).ToArray());

        // The persisted rows match the response.
        await AssertOrderingAsync(factory, workspaceId, (sceneC, 0), (sceneA, 1), (sceneB, 2));
    }

    [Fact]
    public async Task Reorder_scene_to_a_too_large_index_clamps_to_the_end()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        Guid sceneC = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
            sceneC = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene C", 2)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Move the FIRST scene (A) to index 99 -> clamped to the end -> [B, C, A].
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, 99));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertOrderingAsync(factory, workspaceId, (sceneB, 0), (sceneC, 1), (sceneA, 2));
    }

    // =====================================================================
    // REORDER-ROLE SWEEP — allowed roles get 200.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ReorderRoles))]
    public async Task Reorder_scene_is_200_for_a_reorder_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, 1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertOrderingAsync(factory, workspaceId, (sceneB, 0), (sceneA, 1));
    }

    // =====================================================================
    // NON-REORDER-ROLE SWEEP — denied roles get 403, order unchanged.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonReorderRoles))]
    public async Task Reorder_scene_is_403_for_a_non_reorder_role_and_keeps_the_order(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        // The order is untouched.
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    // =====================================================================
    // OUT-OF-RANGE / FOREIGN SCENE — hidden 404, order unchanged.
    // =====================================================================

    [Fact]
    public async Task Reorder_unknown_scene_is_404_and_keeps_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{Guid.CreateVersion7()}/reorder",
            new ReorderSceneRequest(_orgA, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    [Fact]
    public async Task Reorder_scene_in_another_tenant_is_404_and_keeps_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid sceneInBId = Guid.Empty;
        Guid siblingInBId = Guid.Empty;
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
            sceneInBId = (await db.AddSceneAsync(orgB.Id, workspaceInB.Id, _secretTitle, 0)).Id;
            siblingInBId = (await db.AddSceneAsync(orgB.Id, workspaceInB.Id, "Sibling", 1)).Id;
        });

        // The caller holds both tenants in the token but addresses org B's scene with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/scenes/{sceneInBId}/reorder",
            new ReorderSceneRequest(_orgA, 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        // Org B's ordering is untouched.
        await AssertOrderingAsync(factory, workspaceInBId, (sceneInBId, 0), (siblingInBId, 1));
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's
    // workspace must not learn the scene exists.
    // =====================================================================

    [Fact]
    public async Task Reorder_scene_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, _secretTitle, 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Reorder_scene_is_404_for_a_malformed_or_empty_scene_id(string sceneId)
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
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder",
            new ReorderSceneRequest(_orgA, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Reorder_scene_is_404_for_a_malformed_or_empty_workspace_id(string workspaceId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{Guid.CreateVersion7()}/reorder",
            new ReorderSceneRequest(_orgA, 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug / negative target index.
    // =====================================================================

    [Fact]
    public async Task Reorder_scene_is_400_without_the_organization_slug_and_keeps_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(OrganizationSlug: null, TargetIndex: 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    [Fact]
    public async Task Reorder_scene_is_400_for_a_negative_target_index_and_keeps_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, -1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    // =====================================================================
    // 409 — archived (read-only) workspace.
    // =====================================================================

    [Fact]
    public async Task Reorder_scene_is_409_when_the_workspace_is_archived_and_keeps_the_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneA}/reorder",
            new ReorderSceneRequest(_orgA, 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    // =====================================================================
    // CONCURRENCY — two concurrent reorders converge without duplicate orders.
    // =====================================================================

    [Fact]
    public async Task Two_concurrent_reorders_converge_without_duplicate_orders()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        Guid sceneC = Guid.Empty;
        Guid sceneD = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
            sceneC = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene C", 2)).Id;
            sceneD = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene D", 3)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Two reorders that both move a scene to the front, so their re-pack write-sets OVERLAP. On the
        // Postgres leg they race truly; the xmin token (CORE-CONC-006) makes the loser a 409 rather than a
        // silent corrupting overwrite. On SQLite (one shared connection) they serialize, exercising the same
        // invariant without the true race.
        var (statusC, statusD) = await RaceAsync(
            () => PostReorderStatusAsync(client, workspaceId, sceneC, 0),
            () => PostReorderStatusAsync(client, workspaceId, sceneD, 0));

        // Every response is a well-formed outcome — a success or a concurrency conflict, never a 500.
        Assert.Contains(statusC, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Contains(statusD, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        // At least one reorder committed.
        Assert.True(statusC == HttpStatusCode.OK || statusD == HttpStatusCode.OK);

        // THE CROWN-JEWEL INVARIANT: whatever interleaving occurred, the workspace's scene ordering is still a
        // valid permutation — contiguous 0..n-1 with NO duplicates and NO gaps (the story's "concurrent
        // reorders do not corrupt the order; converge without duplicate orders").
        await AssertContiguousOrderingAsync(factory, workspaceId, 4);
    }

    [Fact]
    public async Task An_interleaved_reorder_surfaces_a_409_over_http()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid sceneA = Guid.Empty;
        Guid sceneB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            organizationId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sceneA = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene A", 0)).Id;
            sceneB = (await db.AddSceneAsync(org.Id, workspace.Id, "Scene B", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        if (!PostgresTestDatabase.IsConfigured)
        {
            // SQLite carries no xmin row-version token, so a cross-context reorder race is last-write-wins,
            // never a 409 (the persistence-layer divergence is covered by
            // RemainingAggregatesOptimisticConcurrencyTokenTests). Here we only assert the single-writer happy
            // path so the test stays green on the default provider; the real end-to-end 409 runs on the
            // Postgres leg below.
            var happyPath = await PostReorderStatusAsync(client, workspaceId, sceneB, 0);
            Assert.Equal(HttpStatusCode.OK, happyPath);
            await AssertOrderingAsync(factory, workspaceId, (sceneB, 0), (sceneA, 1));
            return;
        }

        // A SECOND, NON-retrying connection string off this test's database gives the out-of-band winner its
        // own pool and lets it open a user transaction (the production retrying strategy forbids one); the xmin
        // token is still mapped because it is an Npgsql connection. (Mirrors SavepointRecoveryAndHttpConflictTests.)
        var outOfBandConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionStringFor(factory))
        {
            ApplicationName = "livecore-scene-reorder-oob",
        }.ConnectionString;

        // WINNER (out of band): a concurrent writer RENAMES scene A and HOLDS its transaction open, advancing
        // A's row version (xmin V -> V', not yet visible) and holding A's write lock — without changing any
        // order, so the final state stays valid whichever way the race resolves.
        var winnerOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(outOfBandConnectionString)
            .Options;
        await using var winner = new LiveCoreDbContext(winnerOptions);
        await using var winnerTransaction = await winner.Database.BeginTransactionAsync();
        var winnerScene = await winner.Scenes.SingleAsync(s => s.Id == sceneA);
        winnerScene.Rename("Renamed A", _commandTime);
        await winner.SaveChangesAsync();

        // LOSER: the real reorder over HTTP moves scene B to the front -> re-pack writes B (1->0) then A
        // (0->1). Its UPDATE of A reloads the still-committed row (xmin = V) and PARKS on the winner's lock.
        var loserTask = Task.Run(() => PostReorderStatusAsync(client, workspaceId, sceneB, 0));

        // Wait until the loser's UPDATE is genuinely parked on the winner's lock, so committing the winner can
        // only resolve as a row-version conflict.
        await WaitUntilOneBackendBlockedAsync(outOfBandConnectionString, TimeSpan.FromSeconds(30));

        // The winner COMMITS: A's version advances to V'. The loser's parked UPDATE re-evaluates its
        // WHERE xmin = V against the now-V' row, matches zero rows, and EF raises a DbUpdateConcurrencyException
        // -> ConcurrencyConflictMiddleware maps it to a 409 end to end, rolling back the whole re-pack.
        await winnerTransaction.CommitAsync();

        var loserStatus = await loserTask;
        Assert.Equal(HttpStatusCode.Conflict, loserStatus);

        // The loser changed nothing (it rolled back with the 409), and the winner changed only the title, so
        // the ordering is intact and uncorrupted.
        await AssertOrderingAsync(factory, workspaceId, (sceneA, 0), (sceneB, 1));
    }

    // =====================================================================
    // Race + HTTP helpers.
    // =====================================================================

    /// <summary>
    /// Runs two colliding reorders and returns both outcomes. On the PostgreSQL leg they run TRULY
    /// concurrently (each request on its own pooled Npgsql connection); on SQLite (one shared in-memory
    /// connection that cannot overlap) they run sequentially. Mirrors
    /// <see cref="SavepointRecoveryAndHttpConflictTests"/>.
    /// </summary>
    private static async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        if (PostgresTestDatabase.IsConfigured)
        {
            var results = await Task.WhenAll(Task.Run(first), Task.Run(second)).ConfigureAwait(false);
            return (results[0], results[1]);
        }

        var firstResult = await first().ConfigureAwait(false);
        var secondResult = await second().ConfigureAwait(false);
        return (firstResult, secondResult);
    }

    private static async Task<HttpStatusCode> PostReorderStatusAsync(
        HttpClient client,
        Guid workspaceId,
        Guid sceneId,
        int targetIndex)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder",
            new ReorderSceneRequest(_orgA, targetIndex),
            _json);
        return response.StatusCode;
    }

    /// <summary>
    /// The per-test database connection string the production registration is bound to, WITH credentials. Read
    /// from the factory's configured source (not a live, already-opened connection, whose string Npgsql strips
    /// the password from) so the out-of-band connection can authenticate.
    /// </summary>
    private static string ConnectionStringFor(WorkspaceApiFactory factory)
        => factory.ConfiguredDatabaseConnectionString;

    /// <summary>
    /// Polls <c>pg_stat_activity</c> until at least one OTHER backend in this test's database is waiting on a
    /// heavyweight lock — the loser's UPDATE parked on the winner's row lock. Mirrors
    /// <see cref="SavepointRecoveryAndHttpConflictTests"/>.
    /// </summary>
    private static async Task WaitUntilOneBackendBlockedAsync(string connectionString, TimeSpan timeout)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND pid <> pg_backend_pid();
                    """;
                var blocked = (long)(await command.ExecuteScalarAsync() ?? 0L);
                if (blocked >= 1)
                {
                    return;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The concurrent reorder did not park on the row lock within the timeout.");
            }

            await Task.Delay(25);
        }
    }

    // =====================================================================
    // Persisted-state readers + leak assertion.
    // =====================================================================

    /// <summary>
    /// Asserts the workspace's scenes are stored at exactly the given (id, order) pairs, read back through a
    /// fresh scope in the deterministic (scene_order, id) order.
    /// </summary>
    private static async Task AssertOrderingAsync(
        WorkspaceApiFactory factory,
        Guid workspaceId,
        params (Guid Id, int Order)[] expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var scenes = await context.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderBy(s => s.Order).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.Order })
            .ToListAsync();

        Assert.Equal(expected.Select(e => e.Id).ToArray(), scenes.Select(s => s.Id).ToArray());
        Assert.Equal(expected.Select(e => e.Order).ToArray(), scenes.Select(s => s.Order).ToArray());
    }

    /// <summary>
    /// Asserts the workspace's scenes occupy a contiguous, gap-free, duplicate-free ordering 0..count-1 — the
    /// invariant that must hold no matter how concurrent reorders interleaved.
    /// </summary>
    private static async Task AssertContiguousOrderingAsync(WorkspaceApiFactory factory, Guid workspaceId, int count)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var orders = await context.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => s.Order)
            .ToListAsync();

        Assert.Equal(count, orders.Count);
        Assert.Equal(Enumerable.Range(0, count).ToArray(), orders.OrderBy(o => o).ToArray());
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no scene/tenant existence or authorization rationale,
    /// and never echoes the host-prepared scene title (threat T7).
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_secretTitle, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SceneRow(Guid Id, int Order);
}
