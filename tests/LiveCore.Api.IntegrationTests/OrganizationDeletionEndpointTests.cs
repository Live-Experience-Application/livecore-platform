// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for authorized tenant organization deletion (CORE-PRIV-002, tenant offboarding / data
/// deletion, <c>DELETE /api/v1/organizations/{organizationSlug}</c>, csv/api_routes.csv roles Owner). They drive
/// the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (SQLite with foreign keys
/// enforced), so the documented request flow (authentication -> tenant context resolver -> endpoint -> Owner-only
/// authorization -> deletion command -> platform-level audit) AND the schema's existing <c>ON DELETE CASCADE</c>
/// foreign keys are exercised end-to-end.
///
/// The heart of the story is that an authorized Owner deletes a tenant and the existing cascade removes ALL its
/// child data, while the offboarding is recorded at the platform level so the security record survives, under
/// Owner-only authorization and tenant isolation (threats T1/T5/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>deleting an organization removes all its child data (workspaces, sessions, participants, memberships
///   and its own audit log) via the cascade;</item>
///   <item>only an authorized Owner may delete (403 for any other tenant role, Admin included);</item>
///   <item>a foreign-tenant or unknown org is hidden as 404;</item>
///   <item>data of other tenants is untouched.</item>
/// </list>
/// </summary>
public sealed class OrganizationDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static string OrganizationRoute(string organizationSlug) =>
        $"/api/v1/organizations/{organizationSlug}";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Delete_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success — cascade removes all child data, audited at platform ----

    [Fact]
    public async Task Delete_succeeds_removes_all_child_data_via_cascade_and_audits_at_platform_level()
    {
        await using var factory = new WorkspaceApiFactory();

        const string ownerSubject = "owner-a";

        Guid orgAId = Guid.Empty;
        Guid ownerProfileId = Guid.Empty;
        Guid otherMemberOrgMembershipId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid workspaceMembershipId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid participantId = Guid.Empty;

        // A second tenant whose data must be left completely untouched.
        Guid orgBId = Guid.Empty;
        Guid orgBWorkspaceId = Guid.Empty;
        Guid orgBMembershipId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            ownerProfileId = owner.Id;
            var orgA = await db.AddOrganizationAsync(_orgA);
            orgAId = orgA.Id;
            await db.AddOrganizationMemberAsync(orgA.Id, owner.Id, MembershipRole.Owner);

            // Child data across the tenant that the cascade must remove.
            var otherMember = await db.AddUserAsync(_issuer, "member-a");
            var otherMembership = await db.AddOrganizationMemberAsync(orgA.Id, otherMember.Id, MembershipRole.Participant);
            otherMemberOrgMembershipId = otherMembership.Id;

            var workspace = await db.AddWorkspaceAsync(orgA.Id, "alpha", "Alpha");
            workspaceId = workspace.Id;
            var workspaceMembership = await db.AddWorkspaceMemberAsync(
                orgA.Id, workspace.Id, otherMember.Id, MembershipRole.Participant);
            workspaceMembershipId = workspaceMembership.Id;

            var session = await db.AddSessionAsync(orgA.Id, workspace.Id, "Session A", SessionStatus.Prepared);
            sessionId = session.Id;
            var participant = await db.AddParticipantAsync(orgA.Id, workspace.Id, otherMember.Id, "Member A");
            participantId = participant.Id;

            // Tenant audit history (sealed into the tenant's hash chain) — intentionally part of the teardown.
            await db.AddAuditLogEntryAsync(orgA.Id, AuditAction.SessionStarted);
            await db.AddAuditLogEntryAsync(orgA.Id, AuditAction.SessionEnded);

            // A completely separate tenant + data that must survive the deletion of tenant A.
            var ownerB = await db.AddUserAsync(_issuer, "owner-b");
            var orgB = await db.AddOrganizationAsync(_orgB);
            orgBId = orgB.Id;
            var orgBMembership = await db.AddOrganizationMemberAsync(orgB.Id, ownerB.Id, MembershipRole.Owner);
            orgBMembershipId = orgBMembership.Id;
            var orgBWorkspace = await db.AddWorkspaceAsync(orgB.Id, "beta", "Beta");
            orgBWorkspaceId = orgBWorkspace.Id;
            await db.AddAuditLogEntryAsync(orgB.Id, AuditAction.SessionStarted);
        });

        using var owner = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await owner.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The tenant root is GONE.
        Assert.False(await context.Organizations.AsNoTracking().AnyAsync(o => o.Id == orgAId));

        // All of tenant A's child data was removed by the cascade.
        Assert.False(await context.OrganizationMembers.AsNoTracking().AnyAsync(m => m.OrganizationId == orgAId));
        Assert.False(await context.OrganizationMembers.AsNoTracking().AnyAsync(m => m.Id == otherMemberOrgMembershipId));
        Assert.False(await context.Workspaces.AsNoTracking().AnyAsync(w => w.Id == workspaceId));
        Assert.False(await context.WorkspaceMembers.AsNoTracking().AnyAsync(m => m.Id == workspaceMembershipId));
        Assert.False(await context.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId));
        Assert.False(await context.Participants.AsNoTracking().AnyAsync(p => p.Id == participantId));
        // The tenant's OWN audit log is intentionally part of the teardown.
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(e => e.OrganizationId == orgAId));

        // The offboarding is recorded ONCE at the PLATFORM level (null organization) so it survives the cascade:
        // it captures the actor (the owner) and the deleted organization by id, with no workspace/state.
        var audit = Assert.Single(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.OrganizationDeleted)
            .ToListAsync());
        Assert.Null(audit.OrganizationId);
        Assert.Equal(ownerProfileId, audit.ActorUserProfileId);
        Assert.Equal(orgAId, audit.ResourceId);
        Assert.Equal(nameof(Organization), audit.ResourceType);
        Assert.Null(audit.WorkspaceId);
        Assert.Null(audit.PreviousState);
        Assert.Null(audit.NewState);
        Assert.Null(audit.TargetParticipantId);

        // Tenant B is completely untouched: its org, membership, workspace and audit log all survive.
        Assert.True(await context.Organizations.AsNoTracking().AnyAsync(o => o.Id == orgBId));
        Assert.True(await context.OrganizationMembers.AsNoTracking().AnyAsync(m => m.Id == orgBMembershipId));
        Assert.True(await context.Workspaces.AsNoTracking().AnyAsync(w => w.Id == orgBWorkspaceId));
        Assert.True(await context.AuditLogs.AsNoTracking().AnyAsync(e => e.OrganizationId == orgBId));
    }

    // ---- 403: authenticated tenant member that is not the Owner ---------------

    [Theory]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Delete_is_403_for_a_tenant_member_that_is_not_owner(MembershipRole callerRole)
    {
        // Deletion is OWNER-ONLY: every other tenant role — Admin included, unlike member management/erasure —
        // is denied 403 and the tenant is left intact.
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            // A real Owner so the tenant is not ownerless (keeps the failure about the caller's role).
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOrganizationIntactAsync(factory, orgAId);
    }

    // ---- 404 hidden: foreign-tenant / unknown (T1/T5) ------------------------

    [Fact]
    public async Task Delete_is_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        // The stranger's token claims org A but they hold no membership in it.
        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOrganizationIntactAsync(factory, orgAId);
    }

    [Fact]
    public async Task Delete_is_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // T5: the caller is an Owner of org A and names org A in the path, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOrganizationIntactAsync(factory, orgAId);
    }

    [Fact]
    public async Task Delete_is_404_for_an_owner_in_another_tenant()
    {
        // T5: the caller is an Owner of org B and the path names org A (a tenant the caller cannot see). Their
        // token claims org A (so it is not the claim-mismatch case), but they hold no membership in org A.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-b";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var ownerB = await db.AddUserAsync(_issuer, ownerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            orgAId = orgA.Id;
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, ownerB.Id, MembershipRole.Owner);
            // org A has its own Owner; the caller is not a member of org A.
            var ownerA = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(orgA.Id, ownerA.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(OrganizationRoute(_orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOrganizationIntactAsync(factory, orgAId);
    }

    [Fact]
    public async Task Delete_is_404_for_an_unknown_org()
    {
        // The caller's token claims a slug for which no organization exists; the resolution fails closed (hidden
        // 404), never revealing whether the tenant exists.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "wanderer";
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, subject);
        });

        using var client = factory.CreateClientFor(subject, _issuer, "ghost-tenant");
        var response = await client.DeleteAsync(OrganizationRoute("ghost-tenant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task AssertOrganizationIntactAsync(WorkspaceApiFactory factory, Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        // The tenant survives and no offboarding was audited.
        Assert.True(await context.Organizations.AsNoTracking().AnyAsync(o => o.Id == organizationId));
        Assert.Empty(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.OrganizationDeleted)
            .ToListAsync());
    }
}
