// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the entity-relationship create/list APIs (CORE-ENT-008, the "Entity Graph and
/// Search Completeness" epic): the two routes
/// <c>POST /api/v1/workspaces/{workspaceId}/entity-relationships</c> and
/// <c>GET /api/v1/workspaces/{workspaceId}/entity-relationships</c>, which make the entity-relationship graph
/// AUTHORABLE and READABLE (until this story only the <c>DELETE</c> existed, so an edge could never be created
/// or read — ARC-GAP-118). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented request flow (authentication -> tenant context resolver -> endpoint -> inline authorization) is
/// exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: an authoring role creates two entities, creates a relationship between them (201), then
///   lists relationships and finds it.</item>
///   <item>409 on a DUPLICATE edge (same directed edge of the same kind); only one edge persists.</item>
///   <item>FAIL-CLOSED 400 when an endpoint entity is in a DIFFERENT workspace (the same-workspace-endpoints
///   coupling the database foreign keys cannot enforce); nothing is created.</item>
///   <item>The by-entity LIST filter returns only the edges touching the named entity.</item>
///   <item>Negatives: 401 unauthenticated; the authoring-role sweep (allowed {Owner,Admin,Host,CoHost} -> 201
///   vs denied {Participant,Observer,Auditor} -> 403 on both create and list); foreign-tenant 404; non-member
///   hidden-404; archived workspace 409; self-loop 400; unknown endpoint 400; malformed/missing inputs 400.</item>
/// </list>
///
/// An entity relationship is a structural/authoring graph artifact (no free-form content), so — like the
/// entity-type reads — BOTH routes are restricted to the authoring roles (Owner/Admin/Host/CoHost) and there
/// is a SINGLE projection (no host-vs-participant split). <see cref="MembershipRole"/> is non-linear, so the
/// role sweeps are explicit enumerations, never an ordering comparison; every denial asserts the SPECIFIC
/// status code, asserts no state change where a side effect was possible, and asserts the Problem Details body
/// leaks no existence/rationale (threats T1/T5/T7).
/// </summary>
public sealed class EntityRelationshipCreateListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _kind = "links-to";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The entity-relationship authoring roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> AuthoringRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT author or read edges (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonAuthoringRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on EACH route.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString(), _kind));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_relationships_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-relationships?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — create two entities, create a relationship (201), list and
    // find it.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_201_then_list_finds_it()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid orgId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Create.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadRelationshipAsync(createResponse);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.Equal(workspaceId, created.WorkspaceId);
        Assert.Equal(sourceId, created.SourceEntityId);
        Assert.Equal(targetId, created.TargetEntityId);
        Assert.Equal(_kind, created.RelationshipKind);
        Assert.Equal(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships/{created.Id}",
            createResponse.Headers.Location?.ToString());

        // List — the new edge is found.
        var listResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var edges = await ReadRelationshipsAsync(listResponse);
        var found = Assert.Single(edges);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal(sourceId, found.SourceEntityId);
        Assert.Equal(targetId, found.TargetEntityId);

        await AssertRelationshipCountAsync(factory, workspaceId, 1);
    }

    [Theory]
    [MemberData(nameof(AuthoringRoles))]
    public async Task Create_relationship_is_201_for_an_authoring_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 1);
    }

    // =====================================================================
    // AUTHORING-ROLE SWEEP — non-authoring roles get 403 on BOTH routes.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task Create_relationship_is_403_for_a_non_authoring_role_and_persists_nothing(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task List_relationships_is_403_for_a_non_authoring_role(MembershipRole role)
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
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // DUPLICATE — the same directed edge of the same kind is a 409.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_409_for_a_duplicate_and_keeps_one_edge()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 1);
    }

    // =====================================================================
    // FAIL-CLOSED 400 — an endpoint entity in a DIFFERENT workspace never
    // resolves through the route workspace, so the edge is rejected.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_400_when_the_target_entity_is_in_a_sibling_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid sourceInXId = Guid.Empty;
        Guid targetInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            // The caller hosts workspace X and the source entity lives there.
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var typeX = await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-alpha");
            var sourceInX = await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "source-x");
            sourceInXId = sourceInX.Id;
            // The target entity lives in sibling workspace Y of the SAME org.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-alpha");
            var targetInY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "target-y");
            targetInYId = targetInY.Id;
        });

        // Address the create through workspace X with a target that lives in Y. The workspace-scoped
        // FindByIdAsync(org, X, target) returns nothing because the entity belongs to Y -> 400, nothing created.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceInXId.ToString(), targetInYId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceXId, 0);
    }

    [Fact]
    public async Task Create_relationship_is_400_when_the_source_entity_is_in_a_sibling_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid sourceInYId = Guid.Empty;
        Guid targetInXId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var typeX = await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-alpha");
            var targetInX = await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "target-x");
            targetInXId = targetInX.Id;
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-alpha");
            var sourceInY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "source-y");
            sourceInYId = sourceInY.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceInYId.ToString(), targetInXId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceXId, 0);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — real entities in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_404_for_a_foreign_tenant_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid sourceInBId = Guid.Empty;
        Guid targetInBId = Guid.Empty;
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
            var type = await db.AddEntityTypeAsync(orgB.Id, workspaceInB.Id, "type-alpha");
            var source = await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "source");
            sourceInBId = source.Id;
            var target = await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "target");
            targetInBId = target.Id;
        });

        // The caller is a Host of org B's workspace but addresses it with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceInBId.ToString(), targetInBId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipCountAsync(factory, workspaceInBId, 0);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's
    // workspace must not learn the workspace exists.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
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
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // ARCHIVED — an archived (read-only) workspace rejects the create (409).
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_409_for_an_archived_workspace_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // 400 — structural and input validation.
    // =====================================================================

    [Fact]
    public async Task Create_relationship_is_400_for_a_self_loop_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "self");
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, entityId.ToString(), entityId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_relationship_is_400_for_an_unknown_endpoint_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
        });

        // The target id addresses no entity in the workspace.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), Guid.CreateVersion7().ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Create_relationship_is_400_for_a_malformed_or_empty_endpoint_id(string sourceId)
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
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId, Guid.CreateVersion7().ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_relationship_is_400_for_a_malformed_kind_and_does_not_echo_it()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        const string malformedKind = "Not A Valid Kind!!do-not-echo-marker";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(_orgA, sourceId.ToString(), targetId.ToString(), malformedKind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("do-not-echo-marker", body, StringComparison.Ordinal);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_relationship_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var source = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "source");
            sourceId = source.Id;
            var target = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "target");
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships",
            new CreateEntityRelationshipRequest(OrganizationSlug: null, sourceId.ToString(), targetId.ToString(), _kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task List_relationships_is_400_without_the_organization_slug()
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
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/entity-relationships");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // LIST — by-entity filter and same-workspace isolation.
    // =====================================================================

    [Fact]
    public async Task List_relationships_by_entity_filter_returns_only_touching_edges()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid aId = Guid.Empty;
        Guid bId = Guid.Empty;
        Guid cId = Guid.Empty;
        Guid edgeAbId = Guid.Empty;
        Guid edgeBcId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var a = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "a");
            aId = a.Id;
            var b = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "b");
            bId = b.Id;
            var c = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "c");
            cId = c.Id;
            // a -> b and b -> c. The c -> a... no: only a->b and b->c, so b touches both, a touches one.
            var edgeAb = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, a.Id, b.Id, _kind);
            edgeAbId = edgeAb.Id;
            var edgeBc = await db.AddEntityRelationshipAsync(org.Id, workspace.Id, b.Id, c.Id, _kind);
            edgeBcId = edgeBc.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // The whole-workspace list returns both edges.
        var allResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}");
        var all = await ReadRelationshipsAsync(allResponse);
        Assert.Equal(2, all.Length);

        // Filtering by entity A returns only the a -> b edge (A is an endpoint of one edge).
        var byAResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}&entityId={aId}");
        var byA = await ReadRelationshipsAsync(byAResponse);
        var onlyEdge = Assert.Single(byA);
        Assert.Equal(edgeAbId, onlyEdge.Id);

        // Filtering by entity B returns BOTH edges (B is an endpoint of each).
        var byBResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}&entityId={bId}");
        var byB = await ReadRelationshipsAsync(byBResponse);
        Assert.Equal(2, byB.Length);
        Assert.Contains(byB, e => e.Id == edgeAbId);
        Assert.Contains(byB, e => e.Id == edgeBcId);

        // C touches only the b -> c edge.
        var byCResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}&entityId={cId}");
        var byC = await ReadRelationshipsAsync(byCResponse);
        Assert.Equal(edgeBcId, Assert.Single(byC).Id);
    }

    [Fact]
    public async Task List_relationships_returns_only_same_workspace_edges()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceXId = Guid.Empty;
        Guid edgeInXId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            // Workspace X: one edge, the caller is a Host.
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var typeX = await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-alpha");
            var sourceX = await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "source-x");
            var targetX = await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "target-x");
            var edgeInX = await db.AddEntityRelationshipAsync(org.Id, workspaceX.Id, sourceX.Id, targetX.Id, _kind);
            edgeInXId = edgeInX.Id;
            // Sibling workspace Y: a separate edge that must NEVER appear in workspace X's list.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-alpha");
            var sourceY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "source-y");
            var targetY = await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "target-y");
            await db.AddEntityRelationshipAsync(org.Id, workspaceY.Id, sourceY.Id, targetY.Id, _kind);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-relationships?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edges = await ReadRelationshipsAsync(response);
        Assert.Equal(edgeInXId, Assert.Single(edges).Id);
    }

    [Fact]
    public async Task List_relationships_is_404_for_a_foreign_tenant()
    {
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
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entity-relationships?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task List_relationships_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task List_relationships_is_400_for_a_malformed_entity_filter()
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
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={_orgA}&entityId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<RelationshipDto> ReadRelationshipAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<RelationshipDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<RelationshipDto[]> ReadRelationshipsAsync(HttpResponseMessage response)
    {
        var dtos = await response.Content.ReadFromJsonAsync<RelationshipDto[]>(_json);
        Assert.NotNull(dtos);
        return dtos;
    }

    private static async Task AssertRelationshipCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.EntityRelationships.AsNoTracking().CountAsync(r => r.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no edge/tenant existence or authorization rationale
    /// (threat T7): it carries only the generic title/detail used for every denial, with no id, slug, role or
    /// "why" wording.
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

    private sealed record RelationshipDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SourceEntityId,
        Guid TargetEntityId,
        string RelationshipKind,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
