// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the per-request authorization-lookup cache (CORE-PERF-003). They drive the real
/// application over real HTTP through <see cref="AuthorizationCacheApiFactory"/>, so the documented request flow
/// (authentication -> tenant context resolver -> endpoint) exercises the production cache and its decorators
/// end-to-end.
///
/// The story's required behaviours are asserted here:
/// <list type="bullet">
///   <item>repeated requests by the same principal do NOT re-issue the organization/profile/membership SELECTs
///   within the TTL (asserted with a command-counting interceptor on the SQLite test provider);</item>
///   <item>a membership change INVALIDATES the cache — the removed member loses tenant access on the next request,
///   fail-closed, exactly as the un-cached resolver did;</item>
///   <item>authorization decisions are UNCHANGED: a non-member and a foreign-tenant caller are denied on every
///   request (a denial is never cached, so it is always re-checked, fail-closed).</item>
/// </list>
/// </summary>
public sealed class AuthorizationCacheIntegrationTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    [Fact]
    public async Task Repeated_request_by_the_same_principal_does_not_reissue_the_authz_lookups()
    {
        // The command-counting interceptor is wired only on the SQLite test provider; the behavioural tests below
        // cover both providers. SQLite is the default CI integration path, where this optimisation is observable.
        if (PostgresTestDatabase.IsConfigured)
        {
            return;
        }

        await using var factory = new AuthorizationCacheApiFactory();
        const string subject = "user-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Cold request: the resolver issues the organization / profile / membership lookups.
        var cold = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
        Assert.True(factory.Counter.Organizations >= 1, "cold request should read the organizations table");
        Assert.True(factory.Counter.Users >= 1, "cold request should read the users table");
        Assert.True(factory.Counter.OrganizationMembers >= 1, "cold request should read the organization_members table");

        // Warm request by the SAME principal for the SAME tenant: all three authorization lookups are served from
        // the cache, so none of them is re-issued within the TTL.
        factory.Counter.Reset();
        var warm = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Equal(0, factory.Counter.Organizations);
        Assert.Equal(0, factory.Counter.Users);
        Assert.Equal(0, factory.Counter.OrganizationMembers);
    }

    [Fact]
    public async Task A_membership_removal_invalidates_the_cache_and_revokes_tenant_access()
    {
        await using var factory = new AuthorizationCacheApiFactory();
        const string adminSubject = "admin-a";
        const string targetSubject = "target-a";
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);

            var target = await db.AddUserAsync(_issuer, targetSubject);
            var membership = await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var target = factory.CreateClientFor(targetSubject, _issuer, _orgA);

        // Warm the target's authorization cache with a successful tenant-scoped read.
        var before = await target.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // The Owner removes the target's membership.
        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var removal = await admin.DeleteAsync($"/api/v1/organizations/{_orgA}/members/{targetMembershipId}");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        // Despite the warmed cache, the removed member loses tenant access on the very next request: the removal
        // invalidated the cache, so resolution re-queries and now denies (hidden 404), fail-closed.
        var after = await target.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task A_non_member_is_denied_fail_closed_on_every_request()
    {
        await using var factory = new AuthorizationCacheApiFactory();
        const string subject = "outsider";
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA); // the organization exists, but this subject has NO membership
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // No membership: resolution denies and the read is hidden as 404. A denial is never cached, so a repeated
        // request is re-checked and stays denied — fail-closed.
        var first = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        var second = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Caching_one_tenant_never_grants_a_foreign_tenant()
    {
        await using var factory = new AuthorizationCacheApiFactory();
        const string subject = "member-of-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);

            // Organization B exists, but the subject is NOT a member of it.
            await db.AddOrganizationAsync(_orgB);
        });

        // The token asserts both tenants; only the membership in A is real.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        // Warm the cache for tenant A (a real membership).
        var inA = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, inA.StatusCode);

        // The cached tenant-A context never leaks to tenant B: the foreign tenant is denied on every request
        // because B has no membership for this subject (threat T5), fail-closed.
        var inB = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.NotFound, inB.StatusCode);

        var inBAgain = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.NotFound, inBAgain.StatusCode);
    }
}
