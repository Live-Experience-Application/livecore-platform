using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace invitation revoke endpoint
/// (CORE-WS-007, <c>DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}</c>,
/// csv/api_routes.csv roles Owner,Admin). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> audit) is exercised end-to-end.
///
/// The heart of the story is the threat T6 REVOCATION control made reachable, under negative authorization
/// and tenant isolation (threats T1/T5/T6 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an Owner/Admin revokes a pending invitation (Pending -> Revoked) and a revoked token then no
///   longer redeems (paired with CORE-WS-006);</item>
///   <item>only Owner/Admin may revoke (403 for any other org role); revoking an already-accepted or
///   already-revoked invitation is rejected (409); a cross-tenant/unknown/foreign-workspace invitation is an
///   indistinguishable hidden 404;</item>
///   <item>every successful revoke appends exactly one append-only <c>MemberInvitationRevoked</c> audit record.</item>
/// </list>
/// </summary>
public sealed class WorkspaceInvitationRevokeEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _inviteEmail = "invitee@example.test";

    private static string RevokeRoute(Guid workspaceId, Guid invitationId, string organizationSlug)
        => $"/api/v1/workspaces/{workspaceId}/invitations/{invitationId}?organizationSlug={organizationSlug}";

    private static string AcceptRoute(Guid workspaceId)
        => $"/api/v1/workspaces/{workspaceId}/invitations/accept";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Revoke_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            RevokeRoute(Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success for Owner/Admin, transitions Pending -> Revoked, audits ----

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Revoke_succeeds_for_org_owner_or_admin_revokes_the_token_and_audits(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid adminProfileId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The invitation row survives the revoke (soft transition) and is now Revoked.
        Assert.Equal(WorkspaceInvitationStatus.Revoked, await InvitationStatusAsync(factory, invitationId));

        // The revoke is audited exactly once, capturing the actor, the revoked invitation and the
        // Pending -> Revoked status transition.
        var audit = Assert.Single(await ListRevokedAsync(factory));
        Assert.Equal(AuditAction.MemberInvitationRevoked, audit.Action);
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(nameof(WorkspaceInvitation), audit.ResourceType);
        Assert.Equal(invitationId, audit.ResourceId);
        Assert.Equal(nameof(WorkspaceInvitationStatus.Pending), audit.PreviousState);
        Assert.Equal(nameof(WorkspaceInvitationStatus.Revoked), audit.NewState);
        Assert.Null(audit.TargetParticipantId);
    }

    // ---- crown jewel: a revoked invitation no longer redeems (with CORE-WS-006) ----

    [Fact]
    public async Task A_revoked_invitation_no_longer_redeems()
    {
        // An Owner revokes a pending invitation; the bearer who still holds the plaintext token then cannot
        // redeem it — the revoke retired the token, so the redeem is the same indistinguishable hidden 404 as
        // any other non-redeemable token, and no membership is created (threat T6 revocation control).
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        const string redeemerSubject = "redeemer-a";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var redeemer = await db.AddUserAsync(_issuer, redeemerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            // The redeemer is an org member (so the tenant resolves) but not yet a workspace member.
            await db.AddOrganizationMemberAsync(org.Id, redeemer.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, plaintext) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
            token = plaintext;
        });

        // The Owner revokes the pending invitation.
        using (var owner = factory.CreateClientFor(ownerSubject, _issuer, _orgA))
        {
            var revoke = await owner.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        // The bearer's still-held token is now dead: the redeem is a hidden 404 and grants no membership.
        using (var redeemer = factory.CreateClientFor(redeemerSubject, _issuer, _orgA))
        {
            var accept = await redeemer.PostAsJsonAsync(
                AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));
            Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        }

        await AssertMemberCountAsync(factory, workspaceId, 0);
        Assert.Equal(WorkspaceInvitationStatus.Revoked, await InvitationStatusAsync(factory, invitationId));
    }

    // ---- 403: authenticated org member without Owner/Admin -------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Revoke_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The invitation is untouched (still Pending) and nothing is audited.
        Assert.Equal(WorkspaceInvitationStatus.Pending, await InvitationStatusAsync(factory, invitationId));
        Assert.Empty(await ListRevokedAsync(factory));
    }

    // ---- 409: revoking an accepted invitation is rejected --------------------

    [Fact]
    public async Task Revoke_is_409_for_an_accepted_invitation_and_changes_nothing()
    {
        // Pair with CORE-WS-006: a redeemer accepts the invitation (Pending -> Accepted), then an Owner tries to
        // revoke it. Revoking an accepted invitation must not silently undo the granted membership, so it is a
        // 409 that changes nothing and writes no audit fact.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        const string redeemerSubject = "redeemer-a";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var redeemer = await db.AddUserAsync(_issuer, redeemerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(org.Id, redeemer.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, plaintext) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
            token = plaintext;
        });

        // Redeem the invitation -> Accepted.
        using (var redeemer = factory.CreateClientFor(redeemerSubject, _issuer, _orgA))
        {
            var accept = await redeemer.PostAsJsonAsync(
                AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));
            Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        }

        // Revoking the now-Accepted invitation is rejected with 409.
        using var owner = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await owner.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The invitation stays Accepted and the membership is preserved; no revoke is audited.
        Assert.Equal(WorkspaceInvitationStatus.Accepted, await InvitationStatusAsync(factory, invitationId));
        await AssertMemberCountAsync(factory, workspaceId, 1);
        Assert.Empty(await ListRevokedAsync(factory));
    }

    // ---- 409: revoking an already-revoked invitation is rejected -------------

    [Fact]
    public async Task Revoke_is_409_for_an_already_revoked_invitation()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // Seed an already-revoked invitation.
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail, revoked: true);
            invitationId = invitation.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WorkspaceInvitationStatus.Revoked, await InvitationStatusAsync(factory, invitationId));
        // No NEW revoke audit fact is written for the no-op.
        Assert.Empty(await ListRevokedAsync(factory));
    }

    // ---- 404 hidden: cross-tenant / unknown / foreign-workspace (T1/T5) ------

    [Fact]
    public async Task Revoke_is_404_for_an_invitation_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace and invitation exist in org B. Hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        Guid invitationInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, owner.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                orgB.Id, workspaceInB.Id, MembershipRole.Host, _inviteEmail);
            invitationInBId = invitation.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(RevokeRoute(workspaceInBId, invitationInBId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The invitation in org B is untouched.
        Assert.Equal(WorkspaceInvitationStatus.Pending, await InvitationStatusAsync(factory, invitationInBId));
        Assert.Empty(await ListRevokedAsync(factory));
    }

    [Fact]
    public async Task Revoke_is_404_for_an_unknown_invitation_in_an_owned_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(RevokeRoute(workspaceId, Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_is_404_for_an_invitation_in_another_workspace_of_the_same_tenant()
    {
        // T1/T5: the invitation id exists, but in a DIFFERENT workspace of the same tenant. Addressing it
        // through the wrong workspace must never resolve it.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid otherWorkspaceId = Guid.Empty;
        Guid invitationInWorkspaceOneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspaceOne = await db.AddWorkspaceAsync(org.Id, "show-one", "Show One");
            var workspaceTwo = await db.AddWorkspaceAsync(org.Id, "show-two", "Show Two");
            otherWorkspaceId = workspaceTwo.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspaceOne.Id, MembershipRole.Host, _inviteEmail);
            invitationInWorkspaceOneId = invitation.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        // Address workspace one's invitation through workspace two.
        var response = await client.DeleteAsync(
            RevokeRoute(otherWorkspaceId, invitationInWorkspaceOneId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            WorkspaceInvitationStatus.Pending,
            await InvitationStatusAsync(factory, invitationInWorkspaceOneId));
        Assert.Empty(await ListRevokedAsync(factory));
    }

    [Fact]
    public async Task Revoke_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is an Owner of org A and names org A, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(RevokeRoute(workspaceId, invitationId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(WorkspaceInvitationStatus.Pending, await InvitationStatusAsync(factory, invitationId));
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task Revoke_is_400_when_the_organization_is_missing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
            invitationId = invitation.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/invitations/{invitationId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkspaceInvitationStatus.Pending, await InvitationStatusAsync(factory, invitationId));
    }

    private static async Task<WorkspaceInvitationStatus> InvitationStatusAsync(
        WorkspaceApiFactory factory, Guid invitationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var invitation = await context.WorkspaceInvitations.AsNoTracking()
            .SingleAsync(i => i.Id == invitationId);
        return invitation.Status;
    }

    private static async Task AssertMemberCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListRevokedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditAction.MemberInvitationRevoked)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }
}
