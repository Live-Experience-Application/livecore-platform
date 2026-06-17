// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the organization create/read API (CORE-API-001).
/// They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, with a test authentication scheme and an
/// EF Core SQLite database, so the documented request flow (authentication ->
/// principal mapping -> endpoint -> inline authorization) is exercised
/// end-to-end for <c>GET</c>/<c>POST /api/v1/organizations</c>.
///
/// The heart of the story is fail-closed authorization and tenant isolation:
/// <list type="bullet">
///   <item>401 for missing auth on both routes;</item>
///   <item>403 for a service-account principal on both routes (the
///   unauthorized-role denial — only a human user can hold an organization
///   membership);</item>
///   <item>404 (hidden) when creating a tenant the token does not claim (the
///   foreign-tenant denial; T5);</item>
///   <item>409 on a taken slug, granting NO membership (no privilege
///   escalation into a pre-existing tenant; T5/T1);</item>
///   <item>a create makes the caller the founding Owner, and the list returns
///   only the organizations the caller is a member of AND the token claims.</item>
/// </list>
/// </summary>
public sealed class OrganizationEndpointsTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _orgC = "globex-inc";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401: missing auth on both routes -----------------------------------

    [Fact]
    public async Task List_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Northwind Labs"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 403: a service account (unauthorized role) on both routes ----------

    [Fact]
    public async Task List_for_a_service_account_is_403()
    {
        // Organizations-the-user-belongs-to is a user concept: a service account
        // (issuer-asserted client_id marker) holds no user profile or membership.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer, _orgA);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_for_a_service_account_is_403_and_creates_nothing()
    {
        // Only a human user can own a tenant (the founding membership references a
        // user profile), so a service account is denied fail-closed and no tenant
        // or membership is persisted.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer, _orgA);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Northwind Labs"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await OrganizationExistsAsync(factory, _orgA));
    }

    // ---- POST /organizations: create ----------------------------------------

    [Fact]
    public async Task Create_succeeds_and_makes_the_caller_the_founding_owner()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "founder-a";

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Northwind Labs"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadOrganizationAsync(response);
        Assert.Equal(_orgA, body.Slug);
        Assert.Equal("Northwind Labs", body.Name);
        Assert.NotEqual(Guid.Empty, body.Id);

        // The caller is the founding Owner of the new tenant.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var profile = await db.UserProfiles.SingleAsync(u => u.Issuer == _issuer && u.SubjectId == subject);
        var member = await db.OrganizationMembers.SingleAsync(
            m => m.OrganizationId == body.Id && m.UserProfileId == profile.Id);
        Assert.Equal(MembershipRole.Owner, member.Role);
    }

    [Fact]
    public async Task Create_canonicalizes_the_slug()
    {
        // The token claims the canonical slug; the body supplies a mixed-case,
        // padded variant. The create canonicalizes it to the stored form.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("founder-a", _issuer, _orgA);

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest("  NorthWind-Labs ", "Northwind Labs"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadOrganizationAsync(response);
        Assert.Equal(_orgA, body.Slug);
    }

    [Fact]
    public async Task Create_is_404_when_the_token_does_not_claim_the_slug()
    {
        // Foreign tenant (T5): the caller is authenticated and the token asserts
        // org B, but the create targets org A. A tenant the token does not claim
        // is hidden as 404 (the create never reveals whether it exists), and
        // nothing is persisted.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("user-b", _issuer, _orgB);

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Northwind Labs"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await OrganizationExistsAsync(factory, _orgA));
    }

    [Fact]
    public async Task Create_is_409_on_a_taken_slug_and_grants_no_membership()
    {
        // A tenant already owns the slug. A different caller whose token DOES claim
        // it cannot create it (409) and — critically — is NOT added as a member of
        // the existing tenant: a create can never escalate into ownership of a
        // pre-existing organization (T5/T1).
        await using var factory = new WorkspaceApiFactory();
        const string intruder = "intruder";
        Guid existingOrgId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var founder = await db.AddUserAsync(_issuer, "real-founder");
            var existing = await db.AddOrganizationAsync(_orgA);
            existingOrgId = existing.Id;
            await db.AddOrganizationMemberAsync(existing.Id, founder.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(intruder, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Hijacked"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        // The existing tenant is untouched (its name is unchanged).
        var loaded = await db.Organizations.SingleAsync(o => o.Id == existingOrgId);
        Assert.Equal(_orgA, loaded.Name); // seeded name equals the slug, never "Hijacked"
        // The intruder holds no membership in it (the would-be owner row never committed).
        var intruderProfile = await db.UserProfiles
            .SingleOrDefaultAsync(u => u.Issuer == _issuer && u.SubjectId == intruder);
        if (intruderProfile is not null)
        {
            Assert.False(await db.OrganizationMembers.AnyAsync(
                m => m.OrganizationId == existingOrgId && m.UserProfileId == intruderProfile.Id));
        }
    }

    [Theory]
    [InlineData("northwind-labs", "")] // blank name
    [InlineData("northwind-labs", null)] // missing name
    [InlineData("", "Northwind Labs")] // blank slug
    [InlineData("Bad Slug!", "Northwind Labs")] // invalid slug shape
    public async Task Create_is_400_on_an_invalid_body(string? slug, string? name)
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("founder-a", _issuer, _orgA);

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(slug, name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- GET /organizations: list -------------------------------------------

    [Fact]
    public async Task List_returns_only_orgs_the_caller_belongs_to_and_the_token_claims()
    {
        // The caller is a member of A and B (persisted), and the token claims A
        // and C. The listing must return ONLY A: B is dropped because the token
        // does not claim it (a foreign tenant from the token's view), and C is
        // dropped because the caller has no membership there. Neither leaks (T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-abc";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "other");
            var a = await db.AddOrganizationAsync(_orgA);
            var b = await db.AddOrganizationAsync(_orgB);
            var c = await db.AddOrganizationAsync(_orgC);
            orgAId = a.Id;
            await db.AddOrganizationMemberAsync(a.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(b.Id, user.Id, MembershipRole.Participant);
            // The caller is NOT a member of C; another user is.
            await db.AddOrganizationMemberAsync(c.Id, other.Id, MembershipRole.Owner);
        });

        // Token asserts A and C, but not B.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgC);
        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var organizations = await response.Content.ReadFromJsonAsync<OrganizationDto[]>(_json);
        Assert.NotNull(organizations);
        var only = Assert.Single(organizations);
        Assert.Equal(orgAId, only.Id);
        Assert.Equal(_orgA, only.Slug);
    }

    [Fact]
    public async Task List_is_empty_for_a_user_with_no_matching_membership()
    {
        // The caller is a member of B only, and the token claims A only. There is
        // no organization the caller both belongs to and the token claims, so the
        // listing is empty — a membership the token does not claim never leaks.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b-only";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var b = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(b.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var organizations = await response.Content.ReadFromJsonAsync<OrganizationDto[]>(_json);
        Assert.NotNull(organizations);
        Assert.Empty(organizations);
    }

    [Fact]
    public async Task Create_then_list_returns_the_new_organization()
    {
        // End-to-end "created and read over HTTP": create a tenant, then it
        // appears in the caller's own organization listing.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("founder-a", _issuer, _orgA);

        var created = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new CreateOrganizationRequest(_orgA, "Northwind Labs"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/organizations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var organizations = await listResponse.Content.ReadFromJsonAsync<OrganizationDto[]>(_json);
        Assert.NotNull(organizations);
        var only = Assert.Single(organizations);
        Assert.Equal(_orgA, only.Slug);
        Assert.Equal("Northwind Labs", only.Name);
    }

    private static async Task<bool> OrganizationExistsAsync(WorkspaceApiFactory factory, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await db.Organizations.AnyAsync(o => o.Slug == slug);
    }

    private static async Task<OrganizationDto> ReadOrganizationAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<OrganizationDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private sealed record OrganizationDto(
        Guid Id,
        string Slug,
        string Name,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
