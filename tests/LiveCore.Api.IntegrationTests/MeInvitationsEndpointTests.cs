// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the user-scoped pending-invitation self-discovery endpoint
/// (CORE-INV-002, <c>GET /api/v1/me/invitations</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> principal mapping ->
/// endpoint -> projection) is exercised end-to-end.
///
/// The story is the onboarding discovery half of the invite flow, under the load-bearing rule that matching is
/// ONLY ever on the caller's trustworthy VERIFIED email (CORE-INV-001) and is scoped to the caller's claimed
/// tenants (threats T1/T5/T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>a caller whose verified email matches a pending invitation sees it with the organization slug and
///   workspace id (the fields that drive the existing accept) and never the token hash nor the invited email;</item>
///   <item>a caller with an unverified or non-matching email sees an empty list (never an error);</item>
///   <item>an accepted/revoked/expired invitation, another person's invitation and a foreign-tenant invitation
///   are never returned;</item>
///   <item>the returned fields let the caller drive the existing acceptInvitation.</item>
/// </list>
/// </summary>
public sealed class MeInvitationsEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _route = "/api/v1/me/invitations";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task List_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 403: a service account (a /me read is a user concept) ---------------

    [Fact]
    public async Task List_for_a_service_account_is_403()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientForWithEmail(
            "svc-a", _issuer, "svc@example.test", emailVerified: true, _orgA);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 200: success - matched on the verified email, with slug + workspace id, no token hash ----

    [Fact]
    public async Task List_returns_the_caller_pending_invitation_matched_on_the_verified_email()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        string plaintextToken = null!;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            // The caller is an org member (any role) so the tenant is one they can resolve and accept against.
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            var (invitation, token) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, callerEmail);
            invitationId = invitation.Id;
            plaintextToken = token;
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(invitationId, item.Id);
        Assert.Equal(orgId, item.OrganizationId);
        Assert.Equal(_orgA, item.OrganizationSlug);
        Assert.Equal(workspaceId, item.WorkspaceId);
        Assert.Equal(nameof(MembershipRole.Host), item.Role);
        Assert.Equal(nameof(WorkspaceInvitationStatus.Pending), item.Status);
        Assert.True(item.ExpiresAt > item.CreatedAt);

        // T6/T7: the body must never contain the stored token hash, any plaintext token, a tokenHash field, or
        // the invited email (the projection carries no personal data, not even the caller's own email).
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(WorkspaceInvitationToken.Hash(plaintextToken), body);
        Assert.DoesNotContain(plaintextToken, body);
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(callerEmail, body);
        Assert.DoesNotContain("invitedEmail", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 200: the discovered fields drive the existing acceptInvitation ------

    [Fact]
    public async Task The_discovered_fields_drive_the_existing_accept_invitation()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        string plaintextToken = null!;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");

            var (_, token) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, callerEmail);
            plaintextToken = token;
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);

        // 1) Discover the invitation: the caller learns the organization slug + workspace id WITHOUT the host
        //    handing over a workspace id out of band.
        var discovery = await client.GetAsync(_route);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var page = await discovery.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);

        // 2) Drive the existing accept with the discovered slug + workspace id and the token from the invite link.
        var acceptRoute = $"/api/v1/workspaces/{item.WorkspaceId}/invitations/accept";
        var accept = await client.PostAsJsonAsync(
            acceptRoute,
            new AcceptWorkspaceInvitationRequest(item.OrganizationSlug, plaintextToken));

        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);

        // 3) The invitation is now redeemed, so a follow-up discovery returns nothing (already-accepted excluded).
        var afterAccept = await client.GetAsync(_route);
        var afterPage = await afterAccept.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(afterPage);
        Assert.Empty(afterPage.Items);
    }

    // ---- 200 empty: unverified email fails closed (never an error) -----------

    [Fact]
    public async Task List_is_empty_for_an_unverified_email()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, callerEmail);
        });

        // Same matching email, but the provider did NOT verify it: keying on a bare unverified email would be an
        // enumeration/hijack vector, so the read fails closed to an empty list (never an error).
        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: false, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    // ---- 200 empty: a verified but non-matching email returns nothing --------

    [Fact]
    public async Task List_is_empty_for_a_verified_but_non_matching_email()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The invitation is addressed to someone ELSE.
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, "bob@example.test");
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    // ---- 200: accepted / revoked / expired are excluded; another person's never returned ----

    [Fact]
    public async Task List_excludes_accepted_revoked_expired_and_other_peoples_invitations()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        Guid pendingId = Guid.Empty;
        string otherPersonToken = null!;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");

            // The ONE invitation that must be listed: a pending, unexpired invitation addressed to the caller.
            var (pending, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, callerEmail);
            pendingId = pending.Id;

            // A revoked invitation addressed to the caller must NOT be listed.
            await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, callerEmail, revoked: true);

            // An EXPIRED (still-Pending) invitation addressed to the caller must NOT be listed: created in the
            // past with a one-day validity, so its token can no longer be redeemed.
            await db.AddWorkspaceInvitationAsync(
                org.Id,
                workspace.Id,
                MembershipRole.Host,
                callerEmail,
                createdAt: TestData.SeedTime.AddYears(-1),
                validity: TimeSpan.FromDays(1));

            // An ACCEPTED invitation addressed to the caller must NOT be listed: seed it and drive the real redeem.
            var accepted = WorkspaceInvitation.Create(
                org.Id, workspace.Id, callerEmail, MembershipRole.Host, TestData.SeedTime, out _);
            accepted.Redeem(TestData.SeedTime.AddMinutes(1));
            db.WorkspaceInvitations.Add(accepted);

            // Another person's pending invitation must NEVER be returned to the caller.
            var (_, otherToken) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Host, "bob@example.test");
            otherPersonToken = otherToken;

            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);

        // Exactly the one pending, unexpired, caller-addressed invitation - never the revoked/expired/accepted
        // ones and never another person's.
        var only = Assert.Single(page.Items);
        Assert.Equal(pendingId, only.Id);

        // The other person's token secret never appears anywhere in the response.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(otherPersonToken, body);
        Assert.DoesNotContain(WorkspaceInvitationToken.Hash(otherPersonToken), body);
        Assert.DoesNotContain("bob@example.test", body);
    }

    // ---- T5: fail-closed tenant scoping -------------------------------------

    [Fact]
    public async Task List_excludes_an_invitation_in_a_tenant_the_caller_does_not_belong_to()
    {
        // The caller is a member of org A (and the token claims A); a pending invitation addressed to the caller's
        // verified email exists in org B, which the caller neither claims nor belongs to. It must NEVER be
        // returned (threat T5): an invitation is only discoverable in a tenant the caller can resolve/accept.
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Participant);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceInvitationAsync(orgB.Id, workspaceInB.Id, MembershipRole.Host, callerEmail);
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task List_returns_an_invitation_in_a_claimed_tenant_without_a_membership()
    {
        // CORE-INV-003: the token claims BOTH A and B; the caller is a MEMBER of A only, and a pending invitation
        // addressed to their verified email exists in B. Scoping is now the TOKEN CLAIM ALONE (no pre-existing
        // membership required), the same claim-only scope the accept route resolves against — so the invitation in
        // B (a tenant the caller can resolve and accept against) IS surfaced. This is the cross-org onboarding the
        // earlier membership-AND-claim intersection made impossible (the dead-persona gap ARC-GAP-117). Matching is
        // still ONLY the verified email, so no other person's invitation is ever returned.
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        Guid workspaceInBId = Guid.Empty;
        Guid invitationInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Participant);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            var (invitationInB, _) = await db.AddWorkspaceInvitationAsync(
                orgB.Id, workspaceInB.Id, MembershipRole.Host, callerEmail);
            invitationInBId = invitationInB.Id;
        });

        // Token claims A and B; membership only in A. The invitation in B is now discoverable.
        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA, _orgB);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(invitationInBId, item.Id);
        Assert.Equal(_orgB, item.OrganizationSlug);
        Assert.Equal(workspaceInBId, item.WorkspaceId);
    }

    [Fact]
    public async Task List_excludes_an_invitation_in_a_member_tenant_the_token_does_not_claim()
    {
        // The mirror: the caller is a MEMBER of A and the invitation is in A, but the token does NOT claim A. A
        // membership without a matching token claim must not surface the invitation (threat T5).
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject, email: callerEmail);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, callerEmail);
        });

        // Token claims only B; the caller is a member of A.
        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgB);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    // ---- 200: a brand-new caller with no membership and no addressed invitation gets an empty list ----

    [Fact]
    public async Task List_is_empty_for_a_caller_with_no_membership_and_no_addressed_invitation()
    {
        // CORE-INV-003: a brand-new caller (no membership anywhere) who claims a real tenant gets an empty list
        // when no invitation is addressed to THEIR verified email — even though an invitation to someone else
        // exists in that tenant. The claim-only scope surfaces the tenant, but the verified-email match still
        // gates which invitations are returned, so a no-membership caller never sees another person's invite.
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "first-timer";
        const string callerEmail = "first-timer@example.test";
        await factory.SeedAsync(async db =>
        {
            // The tenant exists and the caller claims it, but the caller holds NO membership and is NOT the invitee.
            var org = await db.AddOrganizationAsync(_orgA);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Host, "someone-else@example.test");
        });

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync(_route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<MyPendingWorkspaceInvitationResponse>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    // ---- 400: a malformed paging parameter is rejected ----------------------

    [Fact]
    public async Task List_is_400_for_a_malformed_limit()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "alice";
        const string callerEmail = "alice@example.test";

        using var client = factory.CreateClientForWithEmail(callerSubject, _issuer, callerEmail, emailVerified: true, _orgA);
        var response = await client.GetAsync($"{_route}?limit=not-a-number");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
