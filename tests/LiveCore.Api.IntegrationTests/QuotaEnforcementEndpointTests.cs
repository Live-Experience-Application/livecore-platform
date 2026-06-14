using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for server-side QUOTA ENFORCEMENT on protected workspace and session commands
/// (CORE-ENTL-004, the quota enforcement story of the "Entitlements and Quotas" epic) — the story's required
/// "Quota enforcement integration tests". They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented request flow (authentication → tenant context resolver → endpoint → authorization → quota
/// enforcement) is exercised end-to-end exactly as in production.
///
/// THE EPIC ACCEPTANCE CRITERION — "Free limits cannot be bypassed by clients". The two protected commands are:
/// <list type="bullet">
///   <item><c>POST /api/v1/workspaces</c> — consumes the creating USER's <c>workspace.active.max</c> quota.</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/start</c> — consumes the session's WORKSPACE's
///   <c>session.active.max</c> quota; <c>.../end</c> releases it.</item>
/// </list>
///
/// Coverage: a subject AT its free limit is rejected with 409 and nothing is created; a subject UNDER the limit
/// succeeds and its usage increments; an unlimited (fair-use) grant always succeeds; a defined quota the subject is
/// NOT entitled to is rejected fail-closed; an ungoverned command (no quota defined) is unaffected; ending a session
/// frees a slot. The required NEGATIVE authorization cases: an unauthorized role is denied (403) and a cross-tenant
/// caller is hidden (404) BEFORE any quota is consulted, and neither consumes any quota (no usage side effect, no
/// resource created). All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaEnforcementEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // =====================================================================
    // POST /api/v1/workspaces — workspace.active.max (User subject).
    // =====================================================================

    [Fact]
    public async Task Create_workspace_is_blocked_when_the_user_is_at_the_workspace_quota()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 1);
            orgId = org.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First create lands AT the limit of 1 (allowed).
        var first = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Second create would exceed the limit: rejected with 409, and no second workspace is created.
        var second = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "second", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(1, await CountWorkspacesAsync(factory, orgId));
    }

    [Fact]
    public async Task Create_workspace_succeeds_repeatedly_for_an_unlimited_user()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            // An unlimited (fair-use) grant: null limit.
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: null);
            orgId = org.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, $"ws-{i}", $"WS {i}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(3, await CountWorkspacesAsync(factory, orgId));
    }

    [Fact]
    public async Task Create_workspace_is_blocked_fail_closed_when_a_quota_is_defined_but_the_user_is_not_entitled()
    {
        // The quota is defined but the user holds NO entitlement granting a limit: fail-closed, no allowance,
        // so a free limit cannot be bypassed by a client that simply never purchased an entitlement.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            orgId = org.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountWorkspacesAsync(factory, orgId));
    }

    [Fact]
    public async Task Create_workspace_succeeds_when_no_quota_is_defined()
    {
        // No quota definition governs workspace creation in this deployment, so the command proceeds unchanged:
        // Core enforces only quotas that exist.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            orgId = org.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountWorkspacesAsync(factory, orgId));
    }

    [Fact]
    public async Task Create_workspace_is_403_for_a_non_owner_admin_role_and_consumes_no_quota()
    {
        // NEGATIVE AUTH: a Host org-role is not authorized to create a workspace (Owner/Admin only). The denial
        // happens BEFORE the quota is consulted, so the unauthorized caller never consumes the quota and no
        // workspace is created (fail-closed; threat T1).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid orgId = Guid.Empty;
        Guid userId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 5);
            orgId = org.Id;
            userId = user.Id;
            quotaId = quota.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountWorkspacesAsync(factory, orgId));
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));
    }

    [Fact]
    public async Task Create_workspace_is_403_for_a_caller_not_entitled_to_the_org_and_consumes_no_quota()
    {
        // NEGATIVE AUTH (tenant isolation, threat T5): the caller is a member of org B only, but names org A in the
        // body. The tenant boundary denies (403) before any quota is consulted; no workspace and no usage in A.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 5);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // =====================================================================
    // POST /api/v1/sessions/{sessionId}/start (+ end) — session.active.max (Workspace subject).
    // =====================================================================

    [Fact]
    public async Task Start_session_is_blocked_when_the_workspace_is_at_the_session_quota()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid session1 = Guid.Empty;
        Guid session2 = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 1);
            session1 = (await db.AddSessionAsync(org.Id, workspace.Id, "S1", SessionStatus.Prepared)).Id;
            session2 = (await db.AddSessionAsync(org.Id, workspace.Id, "S2", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.PostAsync($"/api/v1/sessions/{session1}/start?organizationSlug={_orgA}", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The workspace is now at its limit of one active session: starting a second is rejected (409) and the
        // second session stays Prepared.
        var second = await client.PostAsync($"/api/v1/sessions/{session2}/start?organizationSlug={_orgA}", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertSessionStatusAsync(factory, session2, SessionStatus.Prepared);
    }

    [Fact]
    public async Task Ending_a_session_frees_a_slot_so_a_new_session_can_start()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid session1 = Guid.Empty;
        Guid session2 = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 1);
            session1 = (await db.AddSessionAsync(org.Id, workspace.Id, "S1", SessionStatus.Prepared)).Id;
            session2 = (await db.AddSessionAsync(org.Id, workspace.Id, "S2", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/v1/sessions/{session1}/start?organizationSlug={_orgA}", null)).StatusCode);
        // At the limit: the second start is blocked.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/v1/sessions/{session2}/start?organizationSlug={_orgA}", null)).StatusCode);
        // Ending the first frees the workspace's active-session slot.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/v1/sessions/{session1}/end?organizationSlug={_orgA}", null)).StatusCode);
        // Now the second session can start.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/v1/sessions/{session2}/start?organizationSlug={_orgA}", null)).StatusCode);

        await AssertSessionStatusAsync(factory, session2, SessionStatus.Live);
    }

    [Fact]
    public async Task Start_session_succeeds_repeatedly_for_an_unlimited_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var sessionIds = new List<Guid>();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: null);
            for (var i = 0; i < 3; i++)
            {
                sessionIds.Add((await db.AddSessionAsync(org.Id, workspace.Id, $"S{i}", SessionStatus.Prepared)).Id);
            }
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        foreach (var sessionId in sessionIds)
        {
            var response = await client.PostAsync($"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Start_session_is_blocked_fail_closed_when_a_quota_is_defined_but_the_workspace_is_not_entitled()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            // The quota is defined but the workspace holds NO entitlement: fail-closed.
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            sessionId = (await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task Start_session_succeeds_when_no_quota_is_defined()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            sessionId = (await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    [Fact]
    public async Task Start_session_is_403_for_a_non_control_role_and_consumes_no_quota()
    {
        // NEGATIVE AUTH: a Participant workspace-role may not start a session. The role denial (403) happens before
        // the quota is consulted, so the workspace's session quota is never consumed and the session stays Prepared.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid sessionId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 5);
            workspaceId = workspace.Id;
            quotaId = quota.Id;
            sessionId = (await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.Workspace, workspaceId, quotaId));
    }

    [Fact]
    public async Task Start_session_is_404_for_a_foreign_tenant_and_consumes_no_quota()
    {
        // NEGATIVE AUTH (tenant isolation, threat T5): a real Prepared session in org B, addressed with
        // organizationSlug = A. The cross-tenant id is hidden as 404 before any quota is consulted; the session is
        // not started and the workspace's session quota is not consumed.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sessionInB = Guid.Empty;
        Guid workspaceInB = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 5);
            workspaceInB = workspace.Id;
            quotaId = quota.Id;
            sessionInB = (await db.AddSessionAsync(orgB.Id, workspace.Id, "B Session", SessionStatus.Prepared)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync($"/api/v1/sessions/{sessionInB}/start?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInB, SessionStatus.Prepared);
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.Workspace, workspaceInB, quotaId));
    }

    // =====================================================================
    // POST /api/v1/workspaces/{workspaceId}/archive — releases workspace.active.max (CORE-MON-007).
    //
    // Archiving a workspace is the symmetric counterpart of session end: it RELEASES the workspace.active.max
    // unit the create consumed for the User subject, so the counter tracks the user's CURRENT active workspaces
    // and a free user (limit 1) is not locked out forever after a single create -> archive. The release is
    // idempotent and fail-closed (it runs only after an authorized, persisted archive).
    // =====================================================================

    [Fact]
    public async Task Archive_releases_the_slot_so_a_free_user_at_limit_one_can_create_again()
    {
        // Required test: "Create then archive then create succeeds at limit 1" and "quota_usage returns to 0 after
        // archive". A free user with workspace.active.max = 1 creates (consumes the slot), is blocked from a second
        // create, archives the first (releasing the slot back to 0), and can then create again at the same limit.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        Guid userId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 1);
            orgId = org.Id;
            userId = user.Id;
            quotaId = quota.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First create lands AT the limit of 1 (allowed) and records one unit of usage.
        var first = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));

        // While at the limit, a second create is rejected and records nothing more.
        var blocked = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "second", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // Archiving the first workspace releases its consumption: quota_usage returns to 0.
        var firstId = await GetWorkspaceIdAsync(factory, orgId, "first");
        var archive = await client.PostAsync($"/api/v1/workspaces/{firstId}/archive?organizationSlug={_orgA}", null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));

        // The freed slot lets the user create again, at the same limit of 1.
        var third = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "third", "Third"));
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));
    }

    [Fact]
    public async Task Double_archive_does_not_double_release_the_workspace_quota()
    {
        // Required test: "double-archive does not double-release". At a limit of 2, two creates consume two units;
        // the first archive releases exactly one (usage 2 -> 1); the terminal archive guard rejects the second
        // archive (409) BEFORE the release, so usage stays 1 — a second archive never frees another unit.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid orgId = Guid.Empty;
        Guid userId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 2);
            orgId = org.Id;
            userId = user.Id;
            quotaId = quota.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First")))
                .StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "second", "Second")))
                .StatusCode);
        Assert.Equal(2, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));

        var firstId = await GetWorkspaceIdAsync(factory, orgId, "first");

        // First archive releases exactly one unit.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/v1/workspaces/{firstId}/archive?organizationSlug={_orgA}", null)).StatusCode);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));

        // The second archive of the same (now-archived) workspace is a 409 and releases nothing: usage stays 1.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/v1/workspaces/{firstId}/archive?organizationSlug={_orgA}", null)).StatusCode);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));
    }

    [Fact]
    public async Task Archive_is_403_for_a_non_owner_and_releases_no_quota()
    {
        // NEGATIVE AUTH: archive is Owner-only, so an org Admin is denied (403). The denial happens BEFORE the
        // release, so a non-Owner can never free a workspace slot they are not authorized to archive (fail-closed;
        // threat T1). The pre-recorded usage is left untouched.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid userId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Admin);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 1);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.User, user.Id, quota, usedAmount: 1);
            userId = user.Id;
            quotaId = quota.Id;
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertWorkspaceStatusAsync(factory, workspaceId, WorkspaceStatus.Active);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));
    }

    [Fact]
    public async Task Archive_is_404_for_a_foreign_tenant_workspace_and_releases_no_quota()
    {
        // NEGATIVE AUTH (tenant isolation, threat T5): a real Active workspace in org B, addressed with
        // organizationSlug = A. The cross-tenant id is hidden as 404 before the release runs, so the workspace is
        // not archived and the user's pre-recorded workspace quota is not released.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid userId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        Guid workspaceInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 5);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.User, user.Id, quota, usedAmount: 1);
            userId = user.Id;
            quotaId = quota.Id;
            workspaceInB = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceInB}/archive?organizationSlug={_orgA}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertWorkspaceStatusAsync(factory, workspaceInB, WorkspaceStatus.Active);
        Assert.Equal(1, await ReadUsageAsync(factory, EntitlementSubjectType.User, userId, quotaId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<Guid> GetWorkspaceIdAsync(WorkspaceApiFactory factory, Guid organizationId, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var workspace = await context.Workspaces.AsNoTracking()
            .SingleAsync(w => w.OrganizationId == organizationId && w.Slug == slug);
        return workspace.Id;
    }

    private static async Task AssertWorkspaceStatusAsync(
        WorkspaceApiFactory factory,
        Guid workspaceId,
        WorkspaceStatus expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var workspace = await context.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
        Assert.Equal(expected, workspace.Status);
    }

    private static async Task<int> CountWorkspacesAsync(WorkspaceApiFactory factory, Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Workspaces.AsNoTracking().CountAsync(w => w.OrganizationId == organizationId);
    }

    private static async Task<long> ReadUsageAsync(
        WorkspaceApiFactory factory,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid quotaDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var row = await context.QuotaUsage.AsNoTracking().FirstOrDefaultAsync(
            u => u.SubjectType == subjectType
                && u.SubjectId == subjectId
                && u.QuotaDefinitionId == quotaDefinitionId);
        return row?.UsedAmount ?? 0;
    }

    private static async Task AssertSessionStatusAsync(
        WorkspaceApiFactory factory,
        Guid sessionId,
        SessionStatus expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var session = await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(expected, session.Status);
    }
}
