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
/// HTTP integration tests for first-login tenant provisioning (CORE-ID-007,
/// <c>GET /api/v1/me</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, with a test authentication scheme and an EF
/// Core SQLite database, so the documented first-login onboarding path is
/// exercised end-to-end.
///
/// The story's acceptance criteria and threat model drive the cases:
/// <list type="bullet">
///   <item>a principal whose ONLY tenant context is a verified org claim naming an
///   organization that does NOT yet exist has that organization and a founding
///   Owner membership provisioned on the first <c>GET /api/v1/me</c>, so its
///   org-scoped reads resolve without an out-of-band
///   <c>POST /api/v1/organizations</c>;</item>
///   <item>a second call is idempotent — no duplicate tenant or membership;</item>
///   <item>NEGATIVE (threats T5/T1): a claim naming an ALREADY-EXISTING organization
///   the caller is not a member of provisions NOTHING — the caller is not
///   auto-enrolled and its org-scoped reads stay hidden (404), so an existing
///   tenant can never be joined or hijacked from a claim;</item>
///   <item>NEGATIVE: a principal with no claim provisions nothing.</item>
/// </list>
/// </summary>
public sealed class MeFirstLoginTenantProvisioningEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- positive: a claim for a not-yet-existing tenant founds it ----------

    [Fact]
    public async Task First_me_with_a_claim_for_a_new_org_provisions_the_caller_as_owner()
    {
        // A brand-new user whose only tenant context is the verified org claim for an
        // organization that does not yet exist. The first /me provisions the tenant
        // and the caller's founding Owner membership, so the principal context lists
        // exactly that membership with role Owner.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "founder";

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ReadMeAsync(response);

        var membership = Assert.Single(me.Memberships);
        Assert.Equal(_orgA, membership.OrganizationSlug);
        Assert.Equal("Owner", membership.Role);

        // The tenant root, with the claim as its canonical slug, and the founding
        // Owner membership keyed to the caller's own profile were actually persisted.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var profile = await db.UserProfiles.SingleAsync(u => u.Issuer == _issuer && u.SubjectId == subject);
        var organization = await db.Organizations.SingleAsync(o => o.Slug == _orgA);
        Assert.Equal(organization.Id, membership.OrganizationId);
        var member = await db.OrganizationMembers.SingleAsync(m => m.OrganizationId == organization.Id);
        Assert.Equal(profile.Id, member.UserProfileId);
        Assert.Equal(MembershipRole.Owner, member.Role);
    }

    [Fact]
    public async Task Provisioned_tenant_resolves_org_scoped_reads_without_an_out_of_band_create()
    {
        // After the first /me provisions the tenant, an org-scoped read that goes
        // through the FULL TenantContextResolver (token claim AND persisted
        // membership) resolves — without any POST /api/v1/organizations. The
        // workspace list is the simplest such read (allowed to any membership role,
        // hidden 404 for a non-member/foreign tenant).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "founder-reads";

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First login provisions the tenant.
        var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // The org-scoped read now resolves (200), proving the founder's membership
        // resolves the just-provisioned tenant.
        var workspaces = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
    }

    // ---- idempotency --------------------------------------------------------

    [Fact]
    public async Task Second_me_is_idempotent_and_provisions_no_duplicate()
    {
        // Calling /me twice provisions the tenant exactly once: the second call sees
        // the tenant already exists and the membership already held, so it adds
        // nothing and still reports the single Owner membership.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "founder-twice";

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await ReadMeAsync(await client.GetAsync("/api/v1/me"));
        var second = await ReadMeAsync(await client.GetAsync("/api/v1/me"));

        var firstMembership = Assert.Single(first.Memberships);
        var secondMembership = Assert.Single(second.Memberships);
        Assert.Equal(firstMembership.OrganizationId, secondMembership.OrganizationId);
        Assert.Equal("Owner", secondMembership.Role);

        // Exactly one tenant and one membership exist (no second provision).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Equal(1, await db.Organizations.CountAsync(o => o.Slug == _orgA));
        var organization = await db.Organizations.SingleAsync(o => o.Slug == _orgA);
        Assert.Equal(1, await db.OrganizationMembers.CountAsync(m => m.OrganizationId == organization.Id));
    }

    // ---- NEGATIVE: an existing tenant is never joined from a claim (T5/T1) ---

    [Fact]
    public async Task A_claim_naming_an_existing_org_does_not_auto_enrol_and_org_scoped_reads_stay_hidden()
    {
        // The organization already exists with another founding Owner. A different
        // caller whose token claims that same slug — but who is NOT a member — must
        // NOT be auto-enrolled from the claim: /me lists no membership, no membership
        // row is created, and an org-scoped read stays an indistinguishable hidden
        // 404. An existing tenant can never be joined or hijacked from a claim.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "existing-owner";
        const string intruderSubject = "claim-only-intruder";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var a = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(a.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(intruderSubject, _issuer, _orgA);

        var me = await ReadMeAsync(await client.GetAsync("/api/v1/me"));
        Assert.Empty(me.Memberships);

        // The org-scoped read is hidden (404): the intruder resolves no membership.
        var workspaces = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, workspaces.StatusCode);

        // No membership was created for the intruder, and the existing tenant still
        // has exactly its one (original Owner) member.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var organization = await db.Organizations.SingleAsync(o => o.Slug == _orgA);
        Assert.Equal(1, await db.OrganizationMembers.CountAsync(m => m.OrganizationId == organization.Id));
        var intruder = await db.UserProfiles.SingleAsync(u => u.SubjectId == intruderSubject);
        Assert.False(await db.OrganizationMembers.AnyAsync(m => m.UserProfileId == intruder.Id));
    }

    // ---- NEGATIVE: no claim provisions nothing ------------------------------

    [Fact]
    public async Task A_principal_with_no_org_claim_provisions_no_tenant()
    {
        // With no organization claim there is nothing to provision a tenant from: the
        // profile is provisioned (first sight) but no organization or membership is.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "no-claim";

        using var client = factory.CreateClientFor(subject, _issuer);
        var me = await ReadMeAsync(await client.GetAsync("/api/v1/me"));

        Assert.Empty(me.Memberships);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await db.Organizations.AnyAsync());
        Assert.False(await db.OrganizationMembers.AnyAsync());
    }

    private static async Task<MeDto> ReadMeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MeDto>(_json);
        Assert.NotNull(dto);
        Assert.NotNull(dto.User);
        Assert.NotNull(dto.Memberships);
        return dto;
    }

    private sealed record MeDto(MeUserDto User, MeMembershipDto[] Memberships);

    private sealed record MeUserDto(Guid Id, string Issuer, string Subject, string? DisplayName, string? Email);

    private sealed record MeMembershipDto(
        Guid OrganizationId,
        string OrganizationSlug,
        string OrganizationName,
        string Role);
}
