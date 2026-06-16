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
/// HTTP integration tests for the entity create/list/read APIs (CORE-ENT-006, the "Vertical Authoring and
/// Read API Completeness" epic): the three routes
/// <c>GET /api/v1/workspaces/{workspaceId}/entities</c>,
/// <c>POST /api/v1/workspaces/{workspaceId}/entities</c> and
/// <c>GET /api/v1/workspaces/{workspaceId}/entities/{entityId}</c>. They drive the real application over real
/// HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys
/// ON), so the documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: an authoring role creates an entity (201) and reads it back by id (200, full host
///   shape with the attribute values).</item>
///   <item>List returns only SAME-WORKSPACE entities (a sibling workspace's entity is never included).</item>
///   <item>A foreign-tenant entity is hidden-404 (read by id and list, addressed with the wrong org).</item>
///   <item>A participant (and observer, and auditor) gets the PARTICIPANT-SAFE projection — exactly
///   {id, name}, never the attribute-values content or any internal field (T2).</item>
///   <item>A non-authoring role creating is 403, and nothing is persisted.</item>
///   <item>The created entity is audited (an EntityCreated fact for the created entity).</item>
///   <item>Negatives: 401 unauthenticated; non-member hidden-404; unknown/foreign entity-type 400; archived
///   workspace 409; malformed/missing inputs 400; missing organizationSlug 400.</item>
/// </list>
///
/// An entity IS content, so the host-vs-participant DTO split is the "View host-only content" row of
/// docs/06 (Owner/Admin/Host/CoHost get the full shape; Participant/Observer/Auditor get the stripped shape)
/// — the deliberate distinction from the scene projection, which gives Auditor the host metadata shape.
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations, never an
/// ordering comparison; every denial asserts the SPECIFIC status code, asserts no state change where a side
/// effect was possible, and asserts the Problem Details body leaks no existence/rationale (threats T1/T5/T7).
/// </summary>
public sealed class EntityCreateListReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive marker embedded in the entity's attribute-values content, used to prove the stripped
    // participant projection never echoes the content (T2).
    private const string _attributeMarker = "do-not-leak-attr-marker";
    private static readonly string _attributeValues = $"{{\"trait\":\"{_attributeMarker}\"}}";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The entity-create roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> WriteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT create an entity (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonWriteRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    /// <summary>
    /// The workspace roles that receive the FULL host entity shape: docs/06 "View host-only content" = yes
    /// (Owner, Admin, Host, CoHost). An entity is content, so — unlike the scene metadata projection —
    /// Auditor is NOT here.
    /// </summary>
    public static TheoryData<MembershipRole> HostShapeRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace roles that receive the STRIPPED, audience-safe entity shape: the audience roles
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
    public async Task List_entities_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_entity_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entities/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_entity_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entities",
            new CreateEntityRequest(_orgA, Guid.CreateVersion7().ToString(), "Lantern", _attributeValues));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — create an entity, read it back, and the create is audited.
    // =====================================================================

    [Fact]
    public async Task Create_entity_is_201_then_read_back_by_id_returns_the_host_shape_and_is_audited()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
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
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Create.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "Lantern", _attributeValues));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadEntityAsync(createResponse);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(workspaceId, created.WorkspaceId);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.Equal(entityTypeId, created.EntityTypeId);
        Assert.Equal("Lantern", created.Name);
        Assert.Equal(_attributeValues, created.AttributeValues);
        Assert.Equal($"/api/v1/workspaces/{workspaceId}/entities/{created.Id}", createResponse.Headers.Location?.ToString());

        // Read it back by id — same full host shape, same data.
        var readResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{created.Id}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var read = await ReadEntityAsync(readResponse);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal("Lantern", read.Name);
        Assert.Equal(_attributeValues, read.AttributeValues);
        Assert.Equal(entityTypeId, read.EntityTypeId);

        // The entity is persisted in the workspace.
        await AssertEntityCountAsync(factory, workspaceId, 1);

        // The creation is audited as a single EntityCreated fact for the created entity.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.EntityCreated)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(created.Id, entry.ResourceId);
        Assert.Equal(nameof(Entity), entry.ResourceType);
        Assert.Equal(userId, entry.ActorUserProfileId);
        // No content leaks into the audit record (threat T7): only identifiers and the generic kind name.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Create_entity_with_omitted_attribute_values_defaults_to_an_empty_object()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "Lantern", AttributeValues: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadEntityAsync(response);
        Assert.Equal("{}", created.AttributeValues);
    }

    // =====================================================================
    // WRITE-ROLE SWEEP — create.
    // =====================================================================

    [Theory]
    [MemberData(nameof(WriteRoles))]
    public async Task Create_entity_is_201_for_a_write_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 1);
    }

    [Theory]
    [MemberData(nameof(NonWriteRoles))]
    public async Task Create_entity_is_403_for_a_non_write_workspace_role_and_persists_nothing(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityCountAsync(factory, workspaceId, 0);
        // A denied create writes no audit fact.
        await AssertNoEntityCreatedAuditAsync(factory);
    }

    // =====================================================================
    // LIST — same-workspace only.
    // =====================================================================

    [Fact]
    public async Task List_entities_returns_only_the_routes_workspace_entities()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceXId = Guid.Empty;
        Guid entityX1Id = Guid.Empty;
        Guid entityX2Id = Guid.Empty;
        Guid entityYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);

            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var typeX = await db.AddEntityTypeAsync(org.Id, workspaceX.Id, "type-x");
            entityX1Id = (await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "X1")).Id;
            entityX2Id = (await db.AddEntityAsync(org.Id, workspaceX.Id, typeX.Id, "X2")).Id;

            // A sibling workspace Y in the SAME org with its own entity, which must NOT appear in X's list.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceY.Id, user.Id, MembershipRole.Host);
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-y");
            entityYId = (await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "Y1")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceXId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadHostEntitiesAsync(response);
        var ids = entities.Select(e => e.Id).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Contains(entityX1Id, ids);
        Assert.Contains(entityX2Id, ids);
        Assert.DoesNotContain(entityYId, ids);
    }

    [Fact]
    public async Task List_entities_for_an_empty_workspace_is_200_and_empty()
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
            $"/api/v1/workspaces/{workspaceId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadHostEntitiesAsync(response);
        Assert.Empty(entities);
    }

    // =====================================================================
    // LIST/READ — host-vs-participant DTO PROJECTION by workspace role.
    // An entity IS content: host-content roles get the full shape; the audience
    // roles AND the audit role get the stripped, audience-safe shape.
    // =====================================================================

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public async Task List_entities_returns_the_host_shape_to_a_host_content_role(MembershipRole role)
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
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Lantern");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var properties = FirstElementPropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "workspaceId", "entityTypeId", "name", "attributeValues", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));

        var entities = Deserialize<EntityDto[]>(body);
        var entity = Assert.Single(entities);
        Assert.Equal("Lantern", entity.Name);
        Assert.Equal(workspaceId, entity.WorkspaceId);
        Assert.Equal("{}", entity.AttributeValues);
    }

    [Theory]
    [MemberData(nameof(StrippedShapeRoles))]
    public async Task List_entities_returns_the_stripped_participant_shape_to_an_audience_or_audit_role(MembershipRole role)
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
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            // Seed with the distinctive attribute marker so the no-leak assertion is meaningful.
            var entity = Entity.Create(org.Id, workspace.Id, type.Id, "Lantern", _attributeValues, TestData.SeedTime);
            db.Entities.Add(entity);
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The EXACT top-level property set of the participant shape is {id, name} — and NOTHING else. This
        // FAILS if any host-only field (the attribute-values content, the tenant/workspace/type ids, the host
        // timestamps) or any authorization rationale is ever added to the participant DTO (T2/T7).
        var properties = FirstElementPropertyNames(body);
        Assert.Equal(
            new[] { "id", "name" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("attributeValues", properties);
        Assert.DoesNotContain("organizationId", properties);
        Assert.DoesNotContain("workspaceId", properties);
        Assert.DoesNotContain("entityTypeId", properties);
        Assert.DoesNotContain("createdAt", properties);
        Assert.DoesNotContain("updatedAt", properties);

        // The attribute-values CONTENT never appears anywhere in the body (a direct T2 content-leak guard).
        Assert.DoesNotContain(_attributeMarker, body, StringComparison.Ordinal);

        // The participant still receives the entity (the SET is unchanged; only the SHAPE is stripped).
        var entities = Deserialize<ParticipantEntityDto[]>(body);
        var entity = Assert.Single(entities);
        Assert.Equal("Lantern", entity.Name);
        Assert.NotEqual(Guid.Empty, entity.Id);
    }

    [Fact]
    public async Task Get_entity_by_id_returns_the_stripped_participant_shape_to_a_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var entity = Entity.Create(org.Id, workspace.Id, type.Id, "Lantern", _attributeValues, TestData.SeedTime);
            db.Entities.Add(entity);
            await db.SaveChangesAsync();
            entityId = entity.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var properties = ObjectPropertyNames(body);
        Assert.Equal(
            new[] { "id", "name" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(_attributeMarker, body, StringComparison.Ordinal);

        var entity = Deserialize<ParticipantEntityDto>(body);
        Assert.Equal(entityId, entity.Id);
        Assert.Equal("Lantern", entity.Name);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real entity in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Get_entity_by_id_is_404_for_an_entity_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid entityInBId = Guid.Empty;
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
            entityInBId = (await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "B Entity")).Id;
        });

        // The entity and workspace are real and in org B, but addressed with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var readResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entities/{entityInBId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);

        // The list of the org-B workspace addressed with org A is also hidden as 404.
        var listResponse = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entities?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);
        await AssertNoRationaleLeakAsync(listResponse);
    }

    [Fact]
    public async Task Get_entity_by_id_is_404_when_addressed_through_a_sibling_workspace()
    {
        // T1/T5 cross-workspace WITHIN one tenant: an entity in sibling workspace Y addressed through
        // workspace X (which the caller hosts) is hidden as 404 — the lookup is scoped to the route's
        // workspace, so a control role in X never reaches Y's entity.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid workspaceXId = Guid.Empty;
        Guid entityInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            workspaceXId = workspaceX.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var typeY = await db.AddEntityTypeAsync(org.Id, workspaceY.Id, "type-y");
            entityInYId = (await db.AddEntityAsync(org.Id, workspaceY.Id, typeY.Id, "Y Entity")).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceXId}/entities/{entityInYId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the route's workspace.
    // =====================================================================

    [Fact]
    public async Task List_entities_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
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
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Lantern");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Create_entity_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertEntityCountAsync(factory, workspaceId, 0);
    }

    // =====================================================================
    // CREATE — unknown/foreign entity type is a 400 (the same-workspace coupling).
    // =====================================================================

    [Fact]
    public async Task Create_entity_is_400_for_an_unknown_entity_type_and_persists_nothing()
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
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, Guid.CreateVersion7().ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 0);
        await AssertNoEntityCreatedAuditAsync(factory);
    }

    [Fact]
    public async Task Create_entity_is_400_for_a_type_in_a_sibling_workspace_and_persists_nothing()
    {
        // The referenced type is real but lives in sibling workspace Y; resolving it within the route's
        // workspace X returns null (the same-workspace coupling the FK cannot enforce), so the create is a
        // 400 indistinguishable from an unknown type.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
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
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceXId}/entities",
            new CreateEntityRequest(_orgA, typeInYId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceXId, 0);
    }

    // =====================================================================
    // CREATE — archived workspace is read-only (409).
    // =====================================================================

    [Fact]
    public async Task Create_entity_is_409_for_an_archived_workspace_and_persists_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 0);
        await AssertNoEntityCreatedAuditAsync(factory);
    }

    // =====================================================================
    // CREATE — input validation (400) and missing organizationSlug (400).
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_entity_is_400_for_an_invalid_name(string? name)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), name, _attributeValues));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 0);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Create_entity_is_400_for_a_malformed_or_missing_entity_type_id(string? entityTypeId)
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
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId, "E", _attributeValues));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_entity_is_400_for_a_malformed_json_attribute_values_and_does_not_leak_the_body()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        const string malformed = "{ do-not-echo-marker not-valid-json ";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(_orgA, entityTypeId.ToString(), "E", malformed));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("do-not-echo-marker", body, StringComparison.Ordinal);
        await AssertEntityCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task Create_entity_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid entityTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            entityTypeId = type.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/entities",
            new CreateEntityRequest(OrganizationSlug: null, entityTypeId.ToString(), "E", _attributeValues));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEntityCountAsync(factory, workspaceId, 0);
    }

    [Fact]
    public async Task List_entities_is_400_without_the_organization_slug()
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
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/entities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // GET BY ID — safe 404s.
    // =====================================================================

    [Fact]
    public async Task Get_entity_by_id_is_404_for_an_unknown_entity_in_the_callers_workspace()
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
            $"/api/v1/workspaces/{workspaceId}/entities/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_entity_by_id_is_404_for_a_malformed_or_empty_entity_id(string entityId)
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
            $"/api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertEntityCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.Entities.AsNoTracking().CountAsync(e => e.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    private static async Task AssertNoEntityCreatedAuditAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.EntityCreated));
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no entity/tenant existence or authorization
    /// rationale (threat T7): it carries only the generic title/detail used for every denial.
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

    private static async Task<EntityDto[]> ReadHostEntitiesAsync(HttpResponseMessage response)
    {
        var dtos = await response.Content.ReadFromJsonAsync<EntityDto[]>(_json);
        Assert.NotNull(dtos);
        return dtos;
    }

    private static async Task<EntityDto> ReadEntityAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<EntityDto>(_json);
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
    /// Returns the EXACT set of top-level JSON property names on the FIRST element of a JSON-array response
    /// body. The shape-leak guard that fails if a host-only field is ever added to the participant projection.
    /// </summary>
    private static string[] FirstElementPropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var first = document.RootElement[0];
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

    private sealed record EntityDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid EntityTypeId,
        string Name,
        string AttributeValues,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ParticipantEntityDto(
        Guid Id,
        string Name);
}
