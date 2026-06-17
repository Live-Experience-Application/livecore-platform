// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the session create and list API (CORE-API-003,
/// <c>POST</c> and <c>GET /api/v1/workspaces/{workspaceId}/sessions</c>). They drive
/// the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), so the documented
/// request flow (authentication → tenant context resolver → endpoint → authorization
/// → quota enforcement) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests ("Lifecycle + authorization negative
/// tests; quota-exceeded test"):
/// <list type="bullet">
///   <item>LIFECYCLE: a host creates a session (201, Prepared, no live timeline); the
///   created session then appears in the workspace's list; and the SAME created
///   session can be started and ended through the existing lifecycle commands — the
///   acceptance criterion "start/end then operate on a real created session".</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the create role sweep
///   (allowed {Owner, Admin, Host, CoHost} → 201 vs denied {Participant, Observer,
///   Auditor} → 403 with NOTHING created); the list allowed to any workspace member;
///   and the 404-hide cases — a workspace in another tenant, an org member who is not
///   a member of the workspace, a malformed/empty workspace id — plus the 400 cases
///   (missing organizationSlug, invalid title), each asserting nothing was created.</item>
///   <item>QUOTA: a workspace AT its <c>session.active.max</c> ceiling is refused the
///   create with 409 and nothing is created; a create UNDER the ceiling succeeds and —
///   crucially — does NOT consume the active-session quota (that stays owned by
///   start/end), so a created Prepared session never double-counts.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit
/// enumerations of the allowed/denied sets, never an ordering comparison.
/// </summary>
public sealed class SessionCreateListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The session-create roles (csv/api_routes.csv "Owner,Admin,Host,CoHost").</summary>
    public static TheoryData<MembershipRole> CreateRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT create a session (403 cases).</summary>
    public static TheoryData<MembershipRole> NonCreateRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    /// <summary>Every workspace-member role: all may LIST the workspace's sessions.</summary>
    public static TheoryData<MembershipRole> AllMemberRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on BOTH routes.
    // =====================================================================

    [Fact]
    public async Task Create_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/sessions?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // LIFECYCLE — create, then list, then start/end operate on the created session.
    // =====================================================================

    [Fact]
    public async Task Create_a_session_is_201_and_persists_a_prepared_session()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Opening Night"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadSessionAsync(response);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.NotEqual(Guid.Empty, body.OrganizationId);
        Assert.Equal("Opening Night", body.Title);
        Assert.Equal(nameof(SessionStatus.Prepared), body.Status);
        Assert.Null(body.StartedAt);
        Assert.Null(body.EndedAt);
        Assert.Equal($"/api/v1/sessions/{body.Id}", response.Headers.Location?.ToString());

        await AssertSessionStatusAsync(factory, body.Id, SessionStatus.Prepared);
        Assert.Equal(1, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task A_created_session_can_then_be_started_and_ended()
    {
        // The acceptance criterion: start/end operate on a REAL created session. The
        // session is created over HTTP and then driven through its lifecycle over HTTP.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Run"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sessionId = (await ReadSessionAsync(created)).Id;

        var started = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(nameof(SessionStatus.Live), (await ReadSessionAsync(started)).Status);

        var ended = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={_orgA}", null);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal(nameof(SessionStatus.Ended), (await ReadSessionAsync(ended)).Status);

        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Ended);
    }

    [Fact]
    public async Task List_returns_only_the_workspaces_sessions()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        var expected = new List<Guid>();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
            expected.Add((await db.AddSessionAsync(org.Id, workspace.Id, "S1", SessionStatus.Prepared)).Id);
            expected.Add((await db.AddSessionAsync(org.Id, workspace.Id, "S2", SessionStatus.Live)).Id);
            expected.Add((await db.AddSessionAsync(org.Id, workspace.Id, "S3", SessionStatus.Ended)).Id);
            // A sibling workspace's session must NOT appear in this workspace's list.
            var other = await db.AddWorkspaceAsync(org.Id, "winter-show", "Winter Show");
            await db.AddSessionAsync(org.Id, other.Id, "Other", SessionStatus.Prepared);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await ReadSessionListAsync(response);
        Assert.Equal(expected.OrderBy(id => id), sessions.Select(s => s.Id).OrderBy(id => id));
        Assert.All(sessions, s => Assert.Equal(workspaceId, s.WorkspaceId));
    }

    [Theory]
    [MemberData(nameof(AllMemberRoles))]
    public async Task List_is_200_for_any_workspace_member_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await ReadSessionListAsync(response));
    }

    // =====================================================================
    // AUTHORIZATION — create role sweep, each denial asserting NOTHING created.
    // =====================================================================

    [Theory]
    [MemberData(nameof(CreateRoles))]
    public async Task Create_is_201_for_a_session_create_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountSessionsAsync(factory, workspaceId));
    }

    [Theory]
    [MemberData(nameof(NonCreateRoles))]
    public async Task Create_is_403_for_a_non_create_workspace_role_and_creates_nothing(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    // =====================================================================
    // TENANT / OBJECT ISOLATION (404-hide) and 400, each asserting no creation.
    // =====================================================================

    [Fact]
    public async Task Create_is_404_for_a_workspace_in_another_tenant_and_creates_nothing()
    {
        // T5: a real workspace in org B, of which the caller is a Host, addressed with
        // organizationSlug = A (the caller's own org). Hidden as 404, nothing created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceInB = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInB}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceInB));
    }

    [Fact]
    public async Task Create_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        // T1 object-level authorization: the caller is an org Owner in org A and the
        // workspace is in org A, but the caller is NOT a member of the workspace. A
        // non-member must not learn the workspace exists, so 404 (not 403); nothing
        // created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task List_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: a real workspace in org B addressed with organizationSlug = A. The
        // cross-tenant id is hidden as 404 — the caller never learns the workspace or
        // its sessions exist.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspace.Id, user.Id, MembershipRole.Host);
            await db.AddSessionAsync(orgB.Id, workspace.Id, "B Session", SessionStatus.Prepared);
            workspaceInB = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInB}/sessions?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Create_is_404_for_a_malformed_or_empty_workspace_id(string workspaceId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(OrganizationSlug: null, Title: "S"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_is_400_for_a_blank_title_and_creates_nothing(string title)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, title));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task List_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/sessions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // QUOTA — session.active.max enforced on create (quota-exceeded test).
    // =====================================================================

    [Fact]
    public async Task Create_is_blocked_when_the_workspace_is_at_the_session_quota()
    {
        // The workspace is granted a session.active.max of 1 and already has one live
        // session recorded against it (usage = 1). Creating another session would
        // exceed the active-session ceiling, so the create is refused with 409 and no
        // session is created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 1);
            // The workspace is already running its one allowed active session.
            await db.AddQuotaUsageAsync(EntitlementSubjectType.Workspace, workspace.Id, quota, usedAmount: 1);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Create_under_the_quota_succeeds_and_does_not_consume_the_active_session_quota()
    {
        // A create UNDER the ceiling succeeds. Crucially it does NOT record consumption:
        // session.active.max counts LIVE sessions (consumed at start, released at end), so
        // a created Prepared session must not increment the active-session usage — that
        // would double-count and make the session un-startable.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid quotaId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 5);
            workspaceId = workspace.Id;
            quotaId = quota.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountSessionsAsync(factory, workspaceId));
        // The active-session quota is unchanged by the create.
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.Workspace, workspaceId, quotaId));
    }

    [Fact]
    public async Task Create_is_403_for_an_unauthorized_role_before_any_quota_is_consulted()
    {
        // NEGATIVE AUTH: a Participant may not create a session. The role denial (403)
        // happens before the quota is consulted, so the workspace's session quota is
        // never touched and nothing is created (fail-closed; threat T1).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
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
            await db.AddSubjectQuotaEntitlementAsync(
                EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 5);
            workspaceId = workspace.Id;
            quotaId = quota.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "S"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
        Assert.Equal(0, await ReadUsageAsync(factory, EntitlementSubjectType.Workspace, workspaceId, quotaId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<int> CountSessionsAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Sessions.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId);
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

    private static async Task<SessionDto> ReadSessionAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<IReadOnlyList<SessionDto>> ReadSessionListAsync(HttpResponseMessage response)
    {
        // The list is a bounded page envelope (CORE-DX-003): read the page and return its items.
        var page = await response.Content.ReadFromJsonAsync<PageDto<SessionDto>>(_json);
        Assert.NotNull(page);
        return page.Items;
    }

    private sealed record SessionDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        string Title,
        string Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
