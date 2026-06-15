using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the pending-invitations read endpoint
/// (CORE-WS-008, <c>GET /api/v1/workspaces/{workspaceId}/invitations</c>,
/// csv/api_routes.csv roles Owner,Admin). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization) is exercised end-to-end.
///
/// The story is the manage-members read of a workspace's outstanding invites, under negative authorization,
/// tenant isolation and the token-at-rest model (threats T1/T5/T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an Owner/Admin lists exactly the workspace's PENDING invitations (accepted/revoked excluded)
///   through a PII-safe projection whose only personal datum is the invited email;</item>
///   <item>the response NEVER contains the token hash (nor any plaintext token; threats T6/T7);</item>
///   <item>only Owner/Admin may list (403 for any other org role); a cross-tenant/unknown workspace is an
///   indistinguishable hidden 404 (threats T1/T5).</item>
/// </list>
/// </summary>
public sealed class WorkspacePendingInvitationListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static string ListRoute(Guid workspaceId, string organizationSlug)
        => $"/api/v1/workspaces/{workspaceId}/invitations?organizationSlug={organizationSlug}";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task List_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(ListRoute(Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: success for Owner/Admin, only pending, PII-safe (no token hash) ----

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task List_returns_only_pending_invitations_and_never_the_token_hash(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "admin-a";
        const string pendingEmailOne = "pending-one@example.test";
        const string pendingEmailTwo = "pending-two@example.test";
        Guid workspaceId = Guid.Empty;
        Guid pendingOneId = Guid.Empty;
        Guid pendingTwoId = Guid.Empty;
        string pendingTokenOne = null!;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            var (pendingOne, tokenOne) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, pendingEmailOne);
            pendingOneId = pendingOne.Id;
            pendingTokenOne = tokenOne;

            var (pendingTwo, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Admin, pendingEmailTwo);
            pendingTwoId = pendingTwo.Id;

            // A revoked invitation must NOT appear in the pending list.
            await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, "revoked@example.test", revoked: true);

            // An accepted invitation must NOT appear either: seed one and drive the real redeem transition.
            var accepted = WorkspaceInvitation.Create(
                org.Id, workspace.Id, "accepted@example.test", MembershipRole.Host, TestData.SeedTime, out _);
            accepted.Redeem(TestData.SeedTime.AddMinutes(1));
            db.WorkspaceInvitations.Add(accepted);
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var invitations = await response.Content.ReadFromJsonAsync<PendingWorkspaceInvitationResponse[]>();
        Assert.NotNull(invitations);

        // Exactly the two PENDING invitations, never the accepted or revoked ones.
        Assert.Equal(new[] { pendingOneId, pendingTwoId }.OrderBy(id => id), invitations.Select(i => i.Id).OrderBy(id => id));
        Assert.All(invitations, i => Assert.Equal(nameof(WorkspaceInvitationStatus.Pending), i.Status));

        // The projection carries the invited email (the only personal datum), the role and the expiry.
        var one = Assert.Single(invitations, i => i.Id == pendingOneId);
        Assert.Equal(pendingEmailOne, one.InvitedEmail);
        Assert.Equal(nameof(MembershipRole.Host), one.Role);
        Assert.Equal(workspaceId, one.WorkspaceId);
        Assert.True(one.ExpiresAt > one.CreatedAt);

        // T6/T7: the response body must never contain the stored token hash nor any plaintext token.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(WorkspaceInvitationToken.Hash(pendingTokenOne), body);
        Assert.DoesNotContain(pendingTokenOne, body);
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
        // But it does carry the invited email (the one allowed personal datum).
        Assert.Contains(pendingEmailOne, body);
    }

    [Fact]
    public async Task List_is_an_empty_array_when_a_workspace_has_no_pending_invitations()
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
            // Only a revoked invitation exists, so nothing is pending.
            await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, "revoked@example.test", revoked: true);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var invitations = await response.Content.ReadFromJsonAsync<PendingWorkspaceInvitationResponse[]>();
        Assert.NotNull(invitations);
        Assert.Empty(invitations);
    }

    // ---- 403: authenticated org member without Owner/Admin -------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task List_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 404 hidden: cross-tenant / unknown / wrong-org-claim (T1/T5) --------

    [Fact]
    public async Task List_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace exists in org B. Hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, owner.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceInvitationAsync(orgB.Id, workspaceInB.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceInBId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_for_an_unknown_workspace_in_an_owned_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is an Owner of org A and names org A, but the token only asserts org B.
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
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host);
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgB);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task List_is_400_when_the_organization_is_missing()
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
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/invitations");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
