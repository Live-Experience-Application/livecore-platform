// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the entity-TYPE create/list/read APIs (CORE-ENT-007, the "Vertical Authoring and
/// Read API Completeness" epic): the three routes
/// <c>GET /api/v1/workspaces/{workspaceId}/entity-types</c>,
/// <c>POST /api/v1/workspaces/{workspaceId}/entity-types</c> and
/// <c>GET /api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}</c>. They drive the real application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON), so the documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end exactly as in production.
///
/// An entity type is the DATA-DRIVEN definition of a kind of entity (its template key plus field/type metadata)
/// through which a vertical maps its domain onto Core (the template boundary, docs/04). Unlike an entity, it is
/// an AUTHORING/schema artifact, NOT audience content, so all three routes are authorized to the authoring
/// roles only (Owner/Admin/Host/CoHost) with a single response shape — no audience role ever receives an entity
/// type, and there is no host-vs-participant projection.
///
/// Coverage, per the story's required tests (plus the negative scope):
/// <list type="bullet">
///   <item>HAPPY PATH: an authoring role defines an entity type (201), reads it back by id (200) and lists it
///   (200), and the definition is audited (an EntityTypeCreated fact for the created type).</item>
///   <item>List returns only SAME-WORKSPACE types (a sibling workspace's type is never included).</item>
///   <item>A foreign-tenant type is hidden-404 (read by id and list, addressed with the wrong org).</item>
///   <item>A non-authoring role is 403 on create AND on list/read (entity types are authoring-only).</item>
///   <item>Negatives: 401 unauthenticated; non-member hidden-404; duplicate per-workspace key 409; archived
///   workspace 409; malformed/missing inputs 400; missing organizationSlug 400; unknown/malformed id 404.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations, never an ordering
/// comparison; every denial asserts the SPECIFIC status code, asserts no state change where a side effect was
/// possible, and asserts the Problem Details body leaks no existence/rationale (threats T1/T5/T7).
/// </summary>
public sealed class EntityTypeCreateListReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The entity-type authoring roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> AuthoringRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT define, list or read an entity type.</summary>
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
    public async Task List_entity_types_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-types?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_entity_type_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-types/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_entity_type_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — define a type, read it back, list it, and it is audited.
    // =====================================================================

    [Fact]
    public async Task Create_entity_type_is_201_then_read_back_and_listed_and_is_audited()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid orgId = Guid.Empty;
        Guid userId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            userId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Define.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{\"fields\":[]}"));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadEntityTypeAsync(createResponse);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(workspaceId, created.WorkspaceId);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.Equal("type-alpha", created.TypeKey);
        Assert.Equal("Type Alpha", created.DisplayName);
        Assert.Equal("{\"fields\":[]}", created.AttributeSchema);
        Assert.Equal(
            $"/api/v1/workspaces/{workspaceId}/entity-types/{created.Id}",
            createResponse.Headers.Location?.ToString());

        // Read it back by id.
        var readResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types/{created.Id}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var read = await ReadEntityTypeAsync(readResponse);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal("type-alpha", read.TypeKey);
        Assert.Equal("{\"fields\":[]}", read.AttributeSchema);

        // List it.
        var listResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = await ReadEntityTypesAsync(listResponse);
        var single = Assert.Single(listed);
        Assert.Equal(created.Id, single.Id);

        // The definition is audited as a single EntityTypeCreated fact for the created type.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.EntityTypeCreated)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(created.Id, entry.ResourceId);
        Assert.Equal(nameof(EntityType), entry.ResourceType);
        Assert.Equal(userId, entry.ActorUserProfileId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Create_entity_type_with_omitted_attribute_schema_defaults_to_an_empty_object()
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", AttributeSchema: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadEntityTypeAsync(response);
        Assert.Equal("{}", created.AttributeSchema);
    }

    [Fact]
    public async Task Create_entity_type_canonicalizes_the_key()
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "Type-Alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadEntityTypeAsync(response);
        Assert.Equal("type-alpha", created.TypeKey);
    }

    // =====================================================================
    // AUTHORING-ROLE SWEEP — create, list and read.
    // =====================================================================

    [Theory]
    [MemberData(nameof(AuthoringRoles))]
    public async Task Create_entity_type_is_201_for_an_authoring_role(MembershipRole role)
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 1);
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task Create_entity_type_is_403_for_a_non_authoring_role_and_persists_nothing(MembershipRole role)
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
        await AssertNoEntityTypeCreatedAuditAsync(factory);
    }

    [Theory]
    [MemberData(nameof(NonAuthoringRoles))]
    public async Task List_and_read_entity_types_are_403_for_a_non_authoring_member(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid typeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            typeId = (await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var listResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        await AssertNoRationaleLeakAsync(listResponse);

        var readResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types/{typeId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);
    }

    // =====================================================================
    // LIST — same-workspace only.
    // =====================================================================

    [Fact]
    public async Task List_entity_types_returns_only_the_routes_workspace_types_in_key_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceXId = Guid.Empty;
        Guid typeX1Id = Guid.Empty;
        Guid typeX2Id = Guid.Empty;
        Guid typeYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);

            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            // Seed out of key order; the list must come back sorted by the canonical key.
            typeX2Id = (await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-beta")).Id;
            typeX1Id = (await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-alpha")).Id;

            // A sibling workspace Y in the SAME org with its own type, which must NOT appear in X's list.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceY.Id, user.Id, MembershipRole.Host);
            typeYId = (await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-gamma")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-types?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var types = await ReadEntityTypesAsync(response);
        var ids = types.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { typeX1Id, typeX2Id }, ids); // type-alpha then type-beta (key order).
        Assert.DoesNotContain(typeYId, ids);
    }

    [Fact]
    public async Task List_entity_types_for_an_empty_workspace_is_200_and_empty()
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
            $"/api/v1/workspaces/{workspaceId}/entity-types?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var types = await ReadEntityTypesAsync(response);
        Assert.Empty(types);
    }

    [Fact]
    public async Task Read_entity_type_returns_the_full_shape_to_an_authoring_role()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid typeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            typeId = (await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types/{typeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var properties = ObjectPropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "typeKey", "displayName", "attributeSchema", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real type in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Get_and_list_entity_types_are_404_for_a_type_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid typeInBId = Guid.Empty;
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
            typeInBId = (await db.AddEntityTypeAsync(orgB.Id, workspaceInB.Id, "type-alpha")).Id;
        });

        // The type and workspace are real and in org B, but addressed with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var readResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entity-types/{typeInBId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);

        var listResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entity-types?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);
        await AssertNoRationaleLeakAsync(listResponse);
    }

    [Fact]
    public async Task Get_entity_type_by_id_is_404_when_addressed_through_a_sibling_workspace()
    {
        // T1/T5 cross-workspace WITHIN one tenant: a type in sibling workspace Y addressed through workspace X
        // (which the caller hosts) is hidden as 404 — the lookup is scoped to the route's workspace.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid typeInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            typeInYId = (await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-y")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceXId}/entity-types/{typeInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's workspace.
    // =====================================================================

    [Fact]
    public async Task List_entity_types_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
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
            await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Create_entity_type_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // CREATE — duplicate per-workspace key is a 409.
    // =====================================================================

    [Fact]
    public async Task Create_entity_type_is_409_for_a_duplicate_key_in_the_workspace_and_persists_one()
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
            await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // A re-cased key canonicalizes to the existing key, so it collides on the per-workspace natural key.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "TYPE-ALPHA", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 1);
        // Only the seeded type exists; the conflicting create wrote no audit fact.
        await AssertNoEntityTypeCreatedAuditAsync(factory);
    }

    // =====================================================================
    // CREATE — archived workspace is read-only (409).
    // =====================================================================

    [Fact]
    public async Task Create_entity_type_is_409_for_an_archived_workspace_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
        await AssertNoEntityTypeCreatedAuditAsync(factory);
    }

    // =====================================================================
    // CREATE — input validation (400) and missing organizationSlug (400).
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a")] // too short (min length 2).
    [InlineData("Has Spaces")] // not a slug shape.
    [InlineData("-leading")] // leading dash.
    public async Task Create_entity_type_is_400_for_an_invalid_type_key(string? typeKey)
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, typeKey, "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_entity_type_is_400_for_an_invalid_display_name(string? displayName)
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", displayName, "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_entity_type_is_400_for_a_malformed_json_attribute_schema_and_does_not_leak_the_body()
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

        const string malformed = "{ do-not-echo-marker not-valid-json ";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(_orgA, "type-alpha", "Type Alpha", malformed));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("do-not-echo-marker", body, StringComparison.Ordinal);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_entity_type_is_400_without_the_organization_slug()
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
            $"/api/v1/workspaces/{workspaceId}/entity-types",
            new CreateEntityTypeRequest(OrganizationSlug: null, "type-alpha", "Type Alpha", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityTypeCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task List_entity_types_is_400_without_the_organization_slug()
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
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/entity-types");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // GET BY ID — safe 404s.
    // =====================================================================

    [Fact]
    public async Task Get_entity_type_by_id_is_404_for_an_unknown_type_in_the_callers_workspace()
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
            $"/api/v1/workspaces/{workspaceId}/entity-types/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_entity_type_by_id_is_404_for_a_malformed_or_empty_type_id(string entityTypeId)
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
            $"/api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertEntityTypeCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.EntityTypes.AsNoTracking().CountAsync(t => t.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    private static async Task AssertNoEntityTypeCreatedAuditAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.EntityTypeCreated));
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no type/tenant existence or authorization rationale
    /// (threat T7): it carries only the generic title/detail used for every denial.
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

    private static async Task<EntityTypeDto[]> ReadEntityTypesAsync(HttpResponseMessage response)
    {
        var dtos = await response.Content.ReadFromJsonAsync<EntityTypeDto[]>(_json);
        Assert.NotNull(dtos);
        return dtos;
    }

    private static async Task<EntityTypeDto> ReadEntityTypeAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<EntityTypeDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    /// <summary>Returns the EXACT set of top-level JSON property names on a single JSON-object response body.</summary>
    private static string[] ObjectPropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private sealed record EntityTypeDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        string TypeKey,
        string DisplayName,
        string AttributeSchema,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
