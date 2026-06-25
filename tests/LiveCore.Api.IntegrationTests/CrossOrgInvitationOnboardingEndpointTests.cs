// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the cross-org invitation onboarding story (CORE-INV-003) — a genuinely new or
/// cross-org invitee, with NO pre-existing <see cref="OrganizationMember"/> in the inviting org, discovering and
/// accepting a workspace invitation and gaining BOTH an organization membership and a workspace membership in one
/// step. They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the whole
/// discover -> accept -> read journey runs end-to-end.
///
/// This is the story that flips the documented <see cref="MyPendingWorkspaceInvitationResponse"/> persona from a
/// dead end for a new invitee (gap ARC-GAP-117) into a working flow. The load-bearing security rules
/// (docs/06_AUTHORIZATION_MATRIX.md; threats T5/T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>discovery matches the caller's VERIFIED email only and is scoped to the token org claim, so a
///   no-membership caller still discovers an invitation addressed to them but never another person's;</item>
///   <item>accept authorizes on the token org claim AND a valid invitation token (by hash) — never a pre-existing
///   membership and never an email match — and provisions the invitee's org membership at the minimal Participant
///   role atomically with the workspace membership;</item>
///   <item>a non-addressed/unknown token, and a caller whose token is not scoped to the inviting org, are an
///   indistinguishable hidden 404 that provisions NO membership (fail-closed).</item>
/// </list>
/// </summary>
public sealed class CrossOrgInvitationOnboardingEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _inviteeSubject = "brand-new-invitee";
    private const string _inviteeEmail = "newcomer@example.test";
    private const string _meInvitationsRoute = "/api/v1/me/invitations";

    private static string AcceptRoute(Guid workspaceId)
        => $"/api/v1/workspaces/{workspaceId}/invitations/accept";

    // ---- the crown jewel: a brand-new invitee onboards in one discover -> accept -> read flow ----

    [Fact]
    public async Task A_brand_new_invitee_discovers_accepts_and_gains_org_and_workspace_membership()
    {
        // The invitee is a GENUINELY NEW principal: no user profile and NO organization membership are seeded for
        // them. Only the inviting org, its workspace and the invitation (addressed to the invitee's email) exist.
        await using var factory = new WorkspaceApiFactory();
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteeEmail);
        });

        using var client = factory.CreateClientForWithEmail(
            _inviteeSubject, _issuer, _inviteeEmail, emailVerified: true, _orgA);

        // 1) DISCOVER: the invitation is present even though the invitee has no membership yet.
        var discovery = await client.GetAsync(_meInvitationsRoute);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var page = await discovery.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(_orgA, item.OrganizationSlug);
        Assert.Equal(workspaceId, item.WorkspaceId);
        Assert.Equal(nameof(MembershipRole.Host), item.Role);

        // 2) ACCEPT: the discovered slug + workspace id + token drive the accept; the invitee onboards in one step.
        var accept = await client.PostAsJsonAsync(
            AcceptRoute(item.WorkspaceId),
            new AcceptWorkspaceInvitationRequest(item.OrganizationSlug, token));
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);

        // 3) The invitee now has BOTH an OrganizationMember (the minimal Participant role — never an
        //    org-administration role) and a WorkspaceMember (the invitation's Host role), both for the same
        //    server-provisioned profile, and the invitation is consumed.
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

            var orgMember = await context.OrganizationMembers.SingleAsync(m => m.OrganizationId == orgId);
            Assert.Equal(MembershipRole.Participant, orgMember.Role);

            var workspaceMember = await context.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspaceId);
            Assert.Equal(MembershipRole.Host, workspaceMember.Role);

            // Both memberships reference the SAME provisioned user-profile subject.
            Assert.Equal(orgMember.UserProfileId, workspaceMember.UserProfileId);

            var invitation = await context.WorkspaceInvitations.SingleAsync();
            Assert.Equal(WorkspaceInvitationStatus.Accepted, invitation.Status);
        }

        // 4) The invitee can now READ the workspace (the org membership lets the tenant resolver succeed, and the
        //    workspace membership grants the object-level read).
        var read = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    // ---- 200 empty: a new caller with no addressed invitation sees an empty list (never an error) ----

    [Fact]
    public async Task A_new_caller_with_no_addressed_invitation_sees_an_empty_list()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            // The tenant the invitee claims exists and even has an invitation — but it is addressed to SOMEONE
            // ELSE, so the new caller's verified-email match returns nothing.
            var org = await db.AddOrganizationAsync(_orgA);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, "other@example.test");
        });

        using var client = factory.CreateClientForWithEmail(
            _inviteeSubject, _issuer, _inviteeEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_meInvitationsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    // ---- 404 fail-closed: an unknown / non-addressed token provisions NO membership ----

    [Fact]
    public async Task Accept_for_a_new_invitee_with_an_unknown_token_is_404_and_provisions_no_membership()
    {
        await using var factory = new WorkspaceApiFactory();
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // A real invitation exists for the invitee, but they present a token that matches NO invitation.
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteeEmail);
        });

        using var client = factory.CreateClientForWithEmail(
            _inviteeSubject, _issuer, _inviteeEmail, emailVerified: true, _orgA);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId),
            new AcceptWorkspaceInvitationRequest(_orgA, "this-is-not-the-real-token"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Fail-closed: a rejected accept provisions NEITHER an org membership nor a workspace membership for the
        // new caller — a failed redeem can never leak standing.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Equal(0, await context.OrganizationMembers.CountAsync(m => m.OrganizationId == orgId));
        Assert.Equal(0, await context.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId));
    }

    // ---- 404 fail-closed (T5): a token not scoped to the inviting org cannot accept ----

    [Fact]
    public async Task Accept_for_a_new_invitee_whose_token_is_not_scoped_to_the_inviting_org_is_404_and_provisions_no_membership()
    {
        await using var factory = new WorkspaceApiFactory();
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        string token = null!;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            (_, token) = await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, _inviteeEmail);
        });

        // The token claims only org B; the accept body names org A (the inviting org). The token is not scoped to
        // the inviting org, so the claim-only resolution denies and the invitation is an indistinguishable 404.
        using var client = factory.CreateClientForWithEmail(
            _inviteeSubject, _issuer, _inviteeEmail, emailVerified: true, _orgB);
        var response = await client.PostAsJsonAsync(
            AcceptRoute(workspaceId),
            new AcceptWorkspaceInvitationRequest(_orgA, token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Equal(0, await context.OrganizationMembers.CountAsync(m => m.OrganizationId == orgId));
        Assert.Equal(0, await context.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId));
        // The invitation is untouched (still redeemable).
        var invitation = await context.WorkspaceInvitations.SingleAsync();
        Assert.Equal(WorkspaceInvitationStatus.Pending, invitation.Status);
    }
}
