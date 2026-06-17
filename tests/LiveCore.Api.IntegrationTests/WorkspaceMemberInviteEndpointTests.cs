// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace member invite placeholder
/// (CORE-WS-004, <c>POST /api/v1/workspaces/{workspaceId}/members</c>,
/// csv/api_routes.csv "Invite/add member", roles Owner,Admin). They drive the
/// real application over real HTTP through <see cref="WorkspaceApiFactory"/>, so
/// the documented request flow (authentication -> tenant context resolver ->
/// endpoint -> inline authorization) is exercised end-to-end.
///
/// The heart of the story is NEGATIVE authorization, tenant isolation and the
/// scoped-token security model (threats T5/T6/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>401 for missing auth; 403 for an org member that is not Owner/Admin;
///   404 (hidden) for cross-tenant/unknown workspace; 400 for invalid body or
///   undefined role;</item>
///   <item>the plaintext token is returned exactly once in the creation
///   response, is a usable unguessable secret, and is NOT stored in plaintext —
///   only its SHA-256 hash is persisted.</item>
/// </list>
/// </summary>
public sealed class WorkspaceMemberInviteEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _inviteEmail = "invitee@example.test";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Invite_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 201: success for Owner/Admin, with a one-time token ----------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Invite_succeeds_for_org_owner_or_admin_and_returns_the_token_once(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadInvitationAsync(response);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal("Participant", body.Role);
        Assert.Equal("Pending", body.Status);
        Assert.True(body.ExpiresAt > body.CreatedAt);
        // The one-time plaintext token is returned, non-empty, and unguessable.
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.True(body.Token.Length >= 40);
    }

    [Fact]
    public async Task The_token_is_returned_in_plaintext_only_in_the_response_and_stored_only_as_a_hash()
    {
        // T6/T7: the response carries the plaintext token, but the database row
        // stores ONLY its SHA-256 hash. The plaintext must never appear at rest.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Host"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadInvitationAsync(response);
        var plaintext = body.Token;

        // Inspect the stored row directly.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var stored = await context.WorkspaceInvitations.SingleAsync(i => i.Id == body.Id);

        // The stored value is the hash of the plaintext, never the plaintext.
        Assert.NotEqual(plaintext, stored.TokenHash);
        Assert.Equal(WorkspaceInvitationToken.Hash(plaintext), stored.TokenHash);
        // The plaintext does match the stored invitation when presented.
        Assert.True(stored.MatchesToken(plaintext));
        // No invited email is leaked back in the response (data minimization).
        Assert.Equal(_inviteEmail, stored.InvitedEmail);
    }

    // ---- 403: authenticated org member without Owner/Admin -------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Invite_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"user-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 404 hidden: cross-tenant and unknown workspace (T1/T5) -------------

    [Fact]
    public async Task Invite_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace exists in org B.
        // Inviting into B's workspace through tenant A is hidden as 404, never
        // 403, and no invitation is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Fact]
    public async Task Invite_is_404_for_an_unknown_workspace_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invite_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is an Owner of org A and names org A, but the token only
        // asserts org B. Resolution denies; the workspace is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    // ---- 400: invalid body / undefined role ---------------------------------

    [Theory]
    [InlineData(null, "Participant")] // missing email
    [InlineData("", "Participant")] // blank email
    [InlineData("not-an-email", "Participant")] // malformed email
    [InlineData("invitee@example.test", null)] // missing role
    [InlineData("invitee@example.test", "")] // blank role
    [InlineData("invitee@example.test", "Superuser")] // unknown role
    [InlineData("invitee@example.test", "999")] // numeric / undefined role
    public async Task Invite_is_400_on_an_invalid_body(string? email, string? role)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(_orgA, email, role));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoInvitationsAsync(factory);
    }

    [Fact]
    public async Task Invite_is_400_when_the_organization_is_missing_from_the_body()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/members",
            new InviteWorkspaceMemberRequest(null, _inviteEmail, "Participant"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<InvitationDto> ReadInvitationAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<InvitationDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task AssertNoInvitationsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.WorkspaceInvitations.AnyAsync());
    }

    private sealed record InvitationDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        string Role,
        string Status,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt,
        string Token);
}
