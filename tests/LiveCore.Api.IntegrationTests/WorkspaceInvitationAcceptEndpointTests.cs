using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace invitation accept/redeem endpoint
/// (CORE-WS-006, <c>POST /api/v1/workspaces/{workspaceId}/invitations/accept</c>). They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the documented request flow
/// (authentication -> tenant context resolver -> endpoint -> atomic redeem) runs end-to-end.
///
/// The heart of the story is the scoped-token BEARER grant and fail-closed redemption (threats T5/T6/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>a pending token redeems to exactly ONE WorkspaceMember carrying the invited role for the
///   AUTHENTICATED caller's subject (not the invited email);</item>
///   <item>redemption is single-use (a second redeem is rejected), expiry- and revocation-aware, and
///   tenant/workspace-scoped (a foreign token is a hidden 404), all reported as an indistinguishable 404;</item>
///   <item>the redeemed member can then act per their granted role — create a scene — end-to-end over HTTP.</item>
/// </list>
/// </summary>
public sealed class WorkspaceInvitationAcceptEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _inviteEmail = "invitee@example.test";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static string AcceptRoute(Guid workspaceId)
        => $"/api/v1/workspaces/{workspaceId}/invitations/accept";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Accept_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            AcceptRoute(Guid.CreateVersion7()),
            new AcceptWorkspaceInvitationRequest(_orgA, "any-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 201: the bearer grant — the caller (not the email) becomes the member

    [Fact]
    public async Task Accept_redeems_a_pending_token_into_one_member_with_the_invited_role()
    {
        // The redeemer is a DIFFERENT identity than the invited email: the bearer grant gives membership to
        // whoever presents a valid token, so the created member carries the CALLER's subject and the invited
        // ROLE, and the invited email is never an identity check.
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        Guid redeemerProfileId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            redeemerProfileId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            // The redeemer is an ORG member (any role) so the tenant resolver succeeds, but NOT yet a
            // workspace member — exactly the "membership is unreachable" gap this story closes.
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId),
            new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var member = await ReadMemberAsync(response);
        Assert.NotEqual(Guid.Empty, member.Id);
        Assert.Equal(workspaceId, member.WorkspaceId);
        Assert.Equal(redeemerProfileId, member.UserProfileId);
        Assert.Equal("Host", member.Role);

        // Exactly one membership exists for the caller's subject in this workspace, and the invitation is now
        // Accepted (single-use consumed).
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var members = await context.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && m.UserProfileId == redeemerProfileId)
            .ToListAsync();
        Assert.Single(members);
        Assert.Equal(MembershipRole.Host, members[0].Role);

        var invitation = await context.WorkspaceInvitations.SingleAsync();
        Assert.Equal(WorkspaceInvitationStatus.Accepted, invitation.Status);

        // The redemption is recorded as a MemberJoined audit fact for the caller (threat T6 invite control).
        var audit = await context.AuditLogs
            .Where(a => a.Action == AuditAction.MemberJoined)
            .ToListAsync();
        Assert.Single(audit);
        Assert.Equal(redeemerProfileId, audit[0].ActorUserProfileId);
        Assert.Equal(workspaceId, audit[0].WorkspaceId);
        Assert.Equal(member.Id, audit[0].ResourceId);
        Assert.Equal("Host", audit[0].NewState);
    }

    // ---- 404 single-use: a second redemption of the same token is rejected ---

    [Fact]
    public async Task Accept_is_single_use_so_a_second_redemption_is_a_hidden_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);

        var first = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A second redemption of the now-Accepted token is rejected exactly like an unknown token (hidden 404).
        var second = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        // Still exactly one membership — the single-use token never granted twice.
        await AssertMemberCountAsync(factory, workspaceId, 1);
    }

    // ---- 404: expired / revoked / unknown tokens (fail-closed) --------------

    [Fact]
    public async Task Accept_is_404_for_an_expired_token()
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // Created far in the past with a short validity, so it is deterministically expired now.
            (_, token) = await db.AddWorkspaceInvitationAsync(
                org.Id,
                workspace.Id,
                MembershipRole.Host,
                _inviteEmail,
                createdAt: new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
                validity: TimeSpan.FromDays(7));
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMemberCountAsync(factory, workspaceId, 0);
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Pending);
    }

    [Fact]
    public async Task Accept_is_404_for_a_revoked_token()
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, _inviteEmail, revoked: true);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMemberCountAsync(factory, workspaceId, 0);
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Revoked);
    }

    [Fact]
    public async Task Accept_is_404_for_an_unknown_token()
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // No invitation seeded at all.
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId),
            new AcceptWorkspaceInvitationRequest(_orgA, "this-is-not-a-real-token"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMemberCountAsync(factory, workspaceId, 0);
    }

    // ---- 404 hidden: wrong workspace / tenant (T5) --------------------------

    [Fact]
    public async Task Accept_is_404_for_a_token_minted_for_another_workspace()
    {
        // A valid token for workspace A presented at workspace B's accept route resolves to nothing: the token
        // is bound to its workspace, so a wrong-workspace redemption is an indistinguishable hidden 404.
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid otherWorkspaceId = Guid.Empty;
        string tokenForWorkspaceA = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspaceA = await db.AddWorkspaceAsync(org.Id, "workspace-a", "Workspace A");
            var workspaceB = await db.AddWorkspaceAsync(org.Id, "workspace-b", "Workspace B");
            otherWorkspaceId = workspaceB.Id;
            (_, tokenForWorkspaceA) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspaceA.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(otherWorkspaceId),
            new AcceptWorkspaceInvitationRequest(_orgA, tokenForWorkspaceA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMemberCountAsync(factory, otherWorkspaceId, 0);
        // The invitation is untouched (still Pending) in its own workspace.
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Pending);
    }

    [Fact]
    public async Task Accept_is_404_for_a_token_minted_in_another_tenant()
    {
        // T5: the caller is a member of org A (and names org A); the workspace and invitation live in org B.
        // Resolution succeeds for org A, but the org-A-scoped token lookup finds nothing, so the foreign-tenant
        // token is a hidden 404 and grants nothing.
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceInBId = Guid.Empty;
        string tokenInB = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            (_, tokenInB) = await db.AddWorkspaceInvitationAsync(orgB.Id, workspaceInB.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceInBId),
            new AcceptWorkspaceInvitationRequest(_orgA, tokenInB));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertMemberCountAsync(factory, workspaceInBId, 0);
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Pending);
    }

    [Fact]
    public async Task Accept_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is a member of org A and names org A, but the token only asserts org B. Resolution
        // denies, so the invitation is hidden as 404 and never redeemed.
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(redeemer, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Pending);
    }

    // ---- 400: invalid body --------------------------------------------------

    [Theory]
    [InlineData(null, "some-token")] // missing organization
    [InlineData("", "some-token")] // blank organization
    [InlineData(_orgA, null)] // missing token
    [InlineData(_orgA, "")] // blank token
    [InlineData(_orgA, "   ")] // whitespace token
    public async Task Accept_is_400_on_an_invalid_body(string? organizationSlug, string? token)
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(organizationSlug, token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- 409: the caller is already a member; the token is not consumed -----

    [Fact]
    public async Task Accept_is_409_when_the_caller_is_already_a_member_and_does_not_consume_the_token()
    {
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // The caller already has a workspace membership.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.CoHost);
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The token is NOT consumed (still Pending) and the existing membership keeps its original role.
        await AssertInvitationStatusAsync(factory, WorkspaceInvitationStatus.Pending);
        await AssertMemberCountAsync(factory, workspaceId, 1);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var member = await context.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspaceId);
        Assert.Equal(MembershipRole.CoHost, member.Role);
    }

    // ---- end-to-end: the redeemed member can act per their role -------------

    [Fact]
    public async Task A_redeemed_member_can_create_a_scene_end_to_end()
    {
        // The crown jewel: redemption makes membership reachable over the public API. A caller with no
        // workspace membership redeems a Host invitation, then immediately creates a scene — an action the
        // authorization matrix grants Host — proving the bearer grant confers real, role-scoped standing.
        await using var factory = new WorkspaceApiFactory();
        const string redeemer = "redeemer-subject";
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, redeemer);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteEmail);
        });

        using var client = factory.CreateClientFor(redeemer, _issuer, _orgA);

        // Before redeeming, the caller cannot create a scene (not a workspace member): hidden 404.
        var beforeRedeem = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "Too Early"));
        Assert.Equal(HttpStatusCode.NotFound, beforeRedeem.StatusCode);

        // Redeem the invitation -> become a Host member.
        var accept = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId), new AcceptWorkspaceInvitationRequest(_orgA, token));
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);

        // Now the redeemed member can create a scene (Host is a scene-create role).
        var createScene = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes",
            new CreateSceneRequest(_orgA, "Opening"));
        Assert.Equal(HttpStatusCode.Created, createScene.StatusCode);
    }

    private static async Task<MemberDto> ReadMemberAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<MemberDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task AssertMemberCountAsync(WorkspaceApiFactory factory, Guid workspaceId, int expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var count = await context.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId);
        Assert.Equal(expected, count);
    }

    private static async Task AssertInvitationStatusAsync(WorkspaceApiFactory factory, WorkspaceInvitationStatus expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var invitation = await context.WorkspaceInvitations.SingleAsync();
        Assert.Equal(expected, invitation.Status);
    }

    private sealed record MemberDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid UserProfileId,
        string Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
