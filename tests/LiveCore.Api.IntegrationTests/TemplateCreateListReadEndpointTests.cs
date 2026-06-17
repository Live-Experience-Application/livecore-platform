// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the template create/list/read APIs (CORE-TMPL-001, the "Vertical Authoring and
/// Read API Completeness" epic): the three routes
/// <c>GET /api/v1/organizations/{organizationSlug}/templates</c>,
/// <c>POST /api/v1/organizations/{organizationSlug}/templates</c> and
/// <c>GET /api/v1/organizations/{organizationSlug}/templates/{templateId}</c>. They drive the real application
/// over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON), so the documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end exactly as in production.
///
/// A template is reusable vertical scaffolding stored as DATA (its template key plus the entity types it
/// defines) through which a vertical maps its domain onto Core (the template boundary, docs/04). It is an
/// ORGANIZATION-level registry/authoring artifact, NOT audience content, so all three routes are org-scoped
/// (the tenant's slug is in the path) and authorized to Owner/Admin only with a single response shape — no
/// audience role ever receives a template, and there is no host-vs-participant projection.
///
/// Coverage, per the story's required tests (plus the negative scope):
/// <list type="bullet">
///   <item>HAPPY PATH: an Owner/Admin creates a template (201), reads it back by id (200) and lists it (200),
///   and the creation is audited (a TemplateCreated fact for the created template).</item>
///   <item>THE GLOBAL/ORGANIZATION BOUNDARY: a global template is never listed nor readable through the
///   org-scoped route, and creating an org template with the same key+version as a global one succeeds without
///   mutating the global template (the headline acceptance criterion).</item>
///   <item>A foreign-tenant template is hidden-404 (read by id and list, addressed with the wrong org).</item>
///   <item>A non-privileged role is 403 on create AND on list/read (templates are Owner/Admin-only).</item>
///   <item>Negatives: 401 unauthenticated; non-member hidden-404; duplicate per-scope key+version 409;
///   malformed inputs 400; unknown/malformed id 404.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations, never an ordering
/// comparison; every denial asserts the SPECIFIC status code, asserts no state change where a side effect was
/// possible, and asserts the Problem Details body leaks no existence/rationale (threats T1/T5/T7). All template
/// keys/definitions are generic and neutral (the template boundary, docs/04; AGENTS.md).
/// </summary>
public sealed class TemplateCreateListReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A generic, well-formed, NEUTRAL template definition (the template boundary, docs/04).
    private const string _definition = """
        {"templateKey":"sample.template","entityTypes":[{"key":"type-alpha","displayName":"Type Alpha","attributeSchema":{"fields":[]}}]}
        """;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The template authoring roles (csv/api_routes.csv "Owner,Admin").</summary>
    public static TheoryData<MembershipRole> PrivilegedRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
    ];

    /// <summary>The organization-member roles that may NOT create, list or read a template.</summary>
    public static TheoryData<MembershipRole> NonPrivilegedRoles =>
    [
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on EACH route.
    // =====================================================================

    [Fact]
    public async Task List_templates_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_template_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/organizations/{_orgA}/templates/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_template_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, _definition));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — create a template, read it back, list it, and it is audited.
    // =====================================================================

    [Theory]
    [MemberData(nameof(PrivilegedRoles))]
    public async Task Create_template_is_201_then_read_back_and_listed_and_is_audited(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"admin-{role}";
        Guid orgId = Guid.Empty;
        Guid userId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            userId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Create.
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, _definition));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadTemplateAsync(createResponse);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.Equal("sample.template", created.TemplateKey);
        Assert.Equal(1, created.Version);
        Assert.Equal(
            $"/api/v1/organizations/{_orgA}/templates/{created.Id}",
            createResponse.Headers.Location?.ToString());

        // Read it back by id.
        var readResponse = await client.GetAsync(
            $"/api/v1/organizations/{_orgA}/templates/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var read = await ReadTemplateAsync(readResponse);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal("sample.template", read.TemplateKey);

        // List it.
        var listResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = await ReadTemplatesAsync(listResponse);
        var single = Assert.Single(listed);
        Assert.Equal(created.Id, single.Id);

        // The creation is audited as a single, ORGANIZATION-level TemplateCreated fact for the created template.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.TemplateCreated)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Null(entry.WorkspaceId);
        Assert.Equal(created.Id, entry.ResourceId);
        Assert.Equal(nameof(Template), entry.ResourceType);
        Assert.Equal(userId, entry.ActorUserProfileId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Create_template_with_omitted_version_defaults_to_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", Version: null, _definition));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadTemplateAsync(response);
        Assert.Equal(1, created.Version);
    }

    [Fact]
    public async Task Create_template_canonicalizes_the_key()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("Sample.Template", 1, _definition));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadTemplateAsync(response);
        Assert.Equal("sample.template", created.TemplateKey);
    }

    [Fact]
    public async Task Read_template_returns_the_full_shape()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            templateId = (await db.AddOrganizationTemplateAsync(org.Id)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var properties = ObjectPropertyNames(body);
        Assert.Equal(
            new[] { "id", "organizationId", "templateKey", "version", "definition", "createdAt", "updatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task List_templates_returns_only_the_organizations_templates_in_key_version_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid alphaV1 = Guid.Empty;
        Guid alphaV2 = Guid.Empty;
        Guid betaV1 = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            // Seed out of order; the list must come back sorted by key then version.
            betaV1 = (await db.AddOrganizationTemplateAsync(org.Id, "beta.template", 1)).Id;
            alphaV2 = (await db.AddOrganizationTemplateAsync(org.Id, "alpha.template", 2)).Id;
            alphaV1 = (await db.AddOrganizationTemplateAsync(org.Id, "alpha.template", 1)).Id;

            // A template in ANOTHER tenant and a GLOBAL template, neither of which must appear in org A's list.
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationTemplateAsync(orgB.Id, "alpha.template", 1);
            await db.AddGlobalTemplateAsync("alpha.template", 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var templates = await ReadTemplatesAsync(response);
        var ids = templates.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { alphaV1, alphaV2, betaV1 }, ids); // alpha v1, alpha v2, beta v1 (key then version).
    }

    // =====================================================================
    // PRIVILEGED / NON-PRIVILEGED ROLE SWEEP.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonPrivilegedRoles))]
    public async Task Create_template_is_403_for_a_non_privileged_role_and_persists_nothing(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, _definition));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertTemplateCountAsync(factory, 0);
        await AssertNoTemplateCreatedAuditAsync(factory);
    }

    [Theory]
    [MemberData(nameof(NonPrivilegedRoles))]
    public async Task List_and_read_templates_are_403_for_a_non_privileged_member(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            templateId = (await db.AddOrganizationTemplateAsync(org.Id)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var listResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        await AssertNoRationaleLeakAsync(listResponse);

        var readResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates/{templateId}");
        Assert.Equal(HttpStatusCode.Forbidden, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);
    }

    // =====================================================================
    // THE GLOBAL / ORGANIZATION BOUNDARY — a global template is never mutated/returned via the org route.
    // =====================================================================

    [Fact]
    public async Task Global_template_is_not_listed_or_readable_through_the_org_route()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid globalTemplateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            globalTemplateId = (await db.AddGlobalTemplateAsync("sample.template", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // The org list never includes the global template.
        var listResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Empty(await ReadTemplatesAsync(listResponse));

        // The global template addressed by its exact id through the org route is a hidden 404.
        var readResponse = await client.GetAsync(
            $"/api/v1/organizations/{_orgA}/templates/{globalTemplateId}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);

        // And the global template is untouched.
        Assert.True(await TemplateExistsAsync(factory, globalTemplateId));
    }

    [Fact]
    public async Task Create_org_template_with_a_globals_key_and_version_succeeds_and_does_not_mutate_the_global()
    {
        // The "an org-scoped lookup never returns a global template for mutation" criterion: a global template
        // and an org template may share a key+version because they are different scopes. Creating the org
        // template succeeds (201) and leaves the global template intact — the org route never reaches the
        // global pool.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid orgId = Guid.Empty;
        Guid globalTemplateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            globalTemplateId = (await db.AddGlobalTemplateAsync("sample.template", 1)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, _definition));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadTemplateAsync(response);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.NotEqual(globalTemplateId, created.Id);

        // Both coexist: the global (organization_id IS NULL) is unchanged, and a NEW org-scoped row exists.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var stored = await context.Templates.AsNoTracking()
            .Where(t => t.TemplateKey == "sample.template" && t.Version == 1)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Single(stored, t => t.Id == globalTemplateId && t.OrganizationId == null);
        Assert.Single(stored, t => t.Id == created.Id && t.OrganizationId == orgId);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real template in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Get_and_list_templates_are_404_for_a_template_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid templateInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            // The caller is an Owner of A only; the token below asserts only A.
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Owner);
            templateInBId = (await db.AddOrganizationTemplateAsync(orgB.Id)).Id;
        });

        // The template is real and in org B, but addressed through org A (the only org the token asserts).
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var readResponse = await client.GetAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateInBId}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        await AssertNoRationaleLeakAsync(readResponse);

        // org A's list does not include org B's template.
        var listResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Empty(await ReadTemplatesAsync(listResponse));
    }

    // =====================================================================
    // NON-MEMBER / FOREIGN-TENANT 404 on every route.
    // =====================================================================

    [Fact]
    public async Task Routes_are_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        // The token claims org A, but the caller has NO membership in org A. Resolution denies, hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "stranger";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            templateId = (await db.AddOrganizationTemplateAsync(org.Id)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var listResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates");
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);

        var readResponse = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates/{templateId}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("other.template", 1, _definition));
        Assert.Equal(HttpStatusCode.NotFound, createResponse.StatusCode);
        await AssertTemplateCountAsync(factory, 1); // only the seeded template; the create wrote nothing.
        await AssertNoTemplateCreatedAuditAsync(factory);
    }

    [Fact]
    public async Task Routes_are_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // The caller is an Owner of org A and names org A in the path, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            templateId = (await db.AddOrganizationTemplateAsync(org.Id)).Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // CREATE — duplicate per-scope key+version is a 409.
    // =====================================================================

    [Fact]
    public async Task Create_template_is_409_for_a_duplicate_key_and_version_and_persists_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationTemplateAsync(org.Id, "sample.template", 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // A re-cased key canonicalizes to the existing key, so it collides on the per-scope natural key.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("SAMPLE.TEMPLATE", 1, _definition));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertTemplateCountAsync(factory, 1);
        // Only the seeded template exists; the conflicting create wrote no audit fact.
        await AssertNoTemplateCreatedAuditAsync(factory);
    }

    // =====================================================================
    // CREATE — input validation (400).
    // =====================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a")] // too short (min length 2).
    [InlineData("Has Spaces")] // not a slug shape.
    [InlineData(".leading")] // leading dot.
    public async Task Create_template_is_400_for_an_invalid_template_key(string? templateKey)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest(templateKey, 1, _definition));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertTemplateCountAsync(factory, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_template_is_400_for_a_non_positive_version(int version)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", version, _definition));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertTemplateCountAsync(factory, 0);
    }

    [Fact]
    public async Task Create_template_is_400_for_a_malformed_definition_and_does_not_leak_the_body()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        const string malformed = "{ do-not-echo-marker not-valid-json ";
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, malformed));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("do-not-echo-marker", body, StringComparison.Ordinal);
        await AssertTemplateCountAsync(factory, 0);
    }

    [Fact]
    public async Task Create_template_is_400_for_a_structurally_invalid_definition()
    {
        // Well-formed JSON but missing the required entityTypes array — structurally invalid (a template that
        // defines no entity types loads nothing).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{_orgA}/templates",
            new CreateTemplateRequest("sample.template", 1, "{\"templateKey\":\"sample.template\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertTemplateCountAsync(factory, 0);
    }

    // =====================================================================
    // GET BY ID — safe 404s.
    // =====================================================================

    [Fact]
    public async Task Get_template_by_id_is_404_for_an_unknown_template_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/organizations/{_orgA}/templates/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_template_by_id_is_404_for_a_malformed_or_empty_template_id(string templateId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<bool> TemplateExistsAsync(WorkspaceApiFactory factory, Guid templateId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Templates.AsNoTracking().AnyAsync(t => t.Id == templateId);
    }

    private static async Task AssertTemplateCountAsync(WorkspaceApiFactory factory, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.Templates.AsNoTracking().CountAsync();
        Assert.Equal(expected, count);
    }

    private static async Task AssertNoTemplateCreatedAuditAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.TemplateCreated));
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no template/tenant existence or authorization
    /// rationale (threat T7): it carries only the generic title/detail used for every denial.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("global", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TemplateDto[]> ReadTemplatesAsync(HttpResponseMessage response)
    {
        var dtos = await response.Content.ReadFromJsonAsync<TemplateDto[]>(_json);
        Assert.NotNull(dtos);
        return dtos;
    }

    private static async Task<TemplateDto> ReadTemplateAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<TemplateDto>(_json);
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

    private sealed record TemplateDto(
        Guid Id,
        Guid OrganizationId,
        string TemplateKey,
        int Version,
        string Definition,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
