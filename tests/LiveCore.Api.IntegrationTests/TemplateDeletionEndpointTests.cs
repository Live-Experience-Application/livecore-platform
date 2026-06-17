// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for template deletion (CORE-LIFE-008,
/// <c>DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}</c>, roles Owner/Admin).
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the
/// documented request flow (authentication -> tenant context resolver -> endpoint -> inline
/// authorization) is exercised end-to-end.
///
/// The story's heart is the global/organization template boundary under negative authorization and
/// tenant isolation (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an authorized admin (Owner/Admin) deletes an ORGANIZATION-scoped template (204), and the
///   row is gone;</item>
///   <item>a GLOBAL template cannot be deleted by an organization — addressed through the org path it
///   is a hidden 404 and is left intact (the headline acceptance criterion);</item>
///   <item>deletion does NOT affect already-instantiated entity types (they carry no foreign key back
///   to the template);</item>
///   <item>401 for missing auth; 403 for a tenant member that is not Owner/Admin; 404 (hidden) for a
///   cross-tenant/unknown template or a tenant the caller cannot see.</item>
/// </list>
/// All template keys/definitions are generic and neutral (the template boundary, docs/04; AGENTS.md).
/// </summary>
public sealed class TemplateDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Delete_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success for Owner/Admin, removes the org template --------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Delete_succeeds_for_owner_or_admin_and_removes_the_org_template(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);
            var template = await db.AddOrganizationTemplateAsync(org.Id);
            templateId = template.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TemplateExistsAsync(factory, templateId));
    }

    // ---- Deletion does not affect already-instantiated entity types ---------

    [Fact]
    public async Task Delete_does_not_affect_already_instantiated_entity_types()
    {
        // A template's load materializes NORMAL workspace EntityType rows that carry no foreign key back
        // to the template, so deleting the template must remove only the registry row and leave every
        // previously instantiated type intact (the story acceptance criterion).
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid templateId = Guid.Empty;
        Guid instantiatedTypeId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var template = await db.AddOrganizationTemplateAsync(org.Id);
            templateId = template.Id;

            // An entity type already instantiated into a workspace of the same tenant (as a load would
            // produce). It references no template, so it must survive the template's deletion.
            var workspace = await db.AddWorkspaceAsync(org.Id, "studio", "Studio");
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            instantiatedTypeId = entityType.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TemplateExistsAsync(factory, templateId));
        // The already-instantiated entity type is unaffected.
        Assert.True(await EntityTypeExistsAsync(factory, instantiatedTypeId));
    }

    // ---- 403: authenticated tenant member without Owner/Admin ----------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Delete_is_403_for_a_tenant_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var template = await db.AddOrganizationTemplateAsync(org.Id);
            templateId = template.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await TemplateExistsAsync(factory, templateId));
    }

    // ---- 404 hidden: a global template cannot be deleted by an org (T5) ------

    [Fact]
    public async Task Delete_is_404_for_a_global_template_and_leaves_it_intact()
    {
        // The headline boundary: a GLOBAL template (organization_id IS NULL) is available to every
        // tenant and must NOT be deletable by an organization. Addressed through the org path with its
        // exact id, the org-scoped lookup never returns it, so it is an indistinguishable hidden 404 and
        // the global template is untouched.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid globalTemplateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var global = await db.AddGlobalTemplateAsync();
            globalTemplateId = global.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{globalTemplateId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await TemplateExistsAsync(factory, globalTemplateId));
    }

    // ---- 404 hidden: cross-tenant / unknown (T1/T5) -------------------------

    [Fact]
    public async Task Delete_is_404_for_a_template_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the template belongs to org B. Addressing it through org A
        // is hidden as 404 (the template id belongs to a tenant the caller cannot see).
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid templateInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);
            var template = await db.AddOrganizationTemplateAsync(orgB.Id);
            templateInBId = template.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateInBId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await TemplateExistsAsync(factory, templateInBId));
    }

    [Fact]
    public async Task Delete_is_404_for_an_unknown_template_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_is_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        // The token claims org A, but the caller has NO membership in org A. Resolution denies (no
        // membership), hidden as 404 — the caller cannot even see the tenant, let alone manage it.
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var template = await db.AddOrganizationTemplateAsync(org.Id);
            templateId = template.Id;
        });

        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await TemplateExistsAsync(factory, templateId));
    }

    [Fact]
    public async Task Delete_is_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // T5: the caller is an Owner of org A and names org A in the path, but the token only asserts
        // org B, so resolution of org A denies and the request is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid templateId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var template = await db.AddOrganizationTemplateAsync(org.Id);
            templateId = template.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/templates/{templateId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await TemplateExistsAsync(factory, templateId));
    }

    private static async Task<bool> TemplateExistsAsync(WorkspaceApiFactory factory, Guid templateId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Templates.AsNoTracking().AnyAsync(template => template.Id == templateId);
    }

    private static async Task<bool> EntityTypeExistsAsync(WorkspaceApiFactory factory, Guid entityTypeId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.EntityTypes.AsNoTracking().AnyAsync(entityType => entityType.Id == entityTypeId);
    }
}
