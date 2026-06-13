using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the session start/end lifecycle commands
/// (CORE-SES-004, <c>POST /api/v1/sessions/{sessionId}/start</c> and
/// <c>.../end</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -> tenant
/// context resolver -> endpoint -> inline authorization) is exercised end-to-end
/// exactly as in production.
///
/// Two families of tests, per the story's required coverage ("Lifecycle tests and
/// authorization tests"):
/// <list type="bullet">
///   <item>LIFECYCLE: a Prepared session starts (200, Live, StartedAt set); a Live
///   session ends (200, Ended, EndedAt set); every out-of-state command is a 409
///   Conflict (start Live, start Ended, end Prepared, end Ended) and leaves the
///   persisted status UNCHANGED. This file pins the persisted status machine and the
///   authorization model; the durable SessionStarted/SessionEnded events and audit
///   records these commands now emit (CORE-EVT-001) are asserted in
///   <see cref="SessionLifecycleEventEmissionEndpointTests"/>.</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the full seven-role
///   WORKSPACE-membership sweep on BOTH routes (allowed {Owner, Admin, Host,
///   CoHost} vs denied {Participant, Observer, Auditor} -> 403, every denial
///   asserting NO state change); and the 404-hide cases — a session in another
///   tenant, a caller who is an org member but NOT a member of the session's
///   workspace, a malformed/empty session id, and a missing organizationSlug (400)
///   — each asserting no state change.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit
/// enumerations of the allowed/denied sets, never an ordering comparison. Every
/// assertion checks the SPECIFIC status code and, on a denial, the SPECIFIC
/// persisted status, so a wrong-status pass that also mutated state is caught.
/// </summary>
public sealed class SessionStartEndEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The session-control roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> ControlRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace-member roles that may NOT start/end a session (the audience
    /// and audit roles). These are the 403 cases.
    /// </summary>
    public static TheoryData<MembershipRole> NonControlRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated on BOTH routes. RequireAuthorization() challenges
    // before any handler runs (docs/08: missing/invalid auth).
    // =====================================================================

    [Fact]
    public async Task Start_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task End_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // LIFECYCLE — the happy-path transitions and the persisted result.
    // =====================================================================

    [Fact]
    public async Task Start_a_prepared_session_is_200_and_becomes_live_with_started_at_set()
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadSessionAsync(response);
        Assert.Equal(sessionId, body.Id);
        Assert.Equal(nameof(SessionStatus.Live), body.Status);
        Assert.NotNull(body.StartedAt);
        Assert.Null(body.EndedAt);
        // The DTO carries the generic fields + timestamps and no hidden fields.
        Assert.NotEqual(Guid.Empty, body.OrganizationId);
        Assert.NotEqual(Guid.Empty, body.WorkspaceId);
        Assert.Equal("Opening Night", body.Title);

        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    [Fact]
    public async Task End_a_live_session_is_200_and_becomes_ended_with_ended_at_set()
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadSessionAsync(response);
        Assert.Equal(nameof(SessionStatus.Ended), body.Status);
        Assert.NotNull(body.StartedAt);
        Assert.NotNull(body.EndedAt);

        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Ended);
    }

    // ---- 409: out-of-state transitions, with status UNCHANGED ---------------

    [Fact]
    public async Task Start_a_live_session_is_409_and_does_not_change_status()
    {
        await AssertConflictLeavesStatusUnchangedAsync(
            seedStatus: SessionStatus.Live,
            command: "start",
            expectedStatusAfter: SessionStatus.Live);
    }

    [Fact]
    public async Task Start_an_ended_session_is_409_and_does_not_change_status()
    {
        await AssertConflictLeavesStatusUnchangedAsync(
            seedStatus: SessionStatus.Ended,
            command: "start",
            expectedStatusAfter: SessionStatus.Ended);
    }

    [Fact]
    public async Task End_a_prepared_session_is_409_and_does_not_change_status()
    {
        await AssertConflictLeavesStatusUnchangedAsync(
            seedStatus: SessionStatus.Prepared,
            command: "end",
            expectedStatusAfter: SessionStatus.Prepared);
    }

    [Fact]
    public async Task End_an_ended_session_is_409_and_does_not_change_status()
    {
        await AssertConflictLeavesStatusUnchangedAsync(
            seedStatus: SessionStatus.Ended,
            command: "end",
            expectedStatusAfter: SessionStatus.Ended);
    }

    // =====================================================================
    // AUTHORIZATION — the full seven-role WORKSPACE-membership sweep on BOTH
    // routes. Allowed {Owner, Admin, Host, CoHost} -> the appropriate 200;
    // denied {Participant, Observer, Auditor} -> 403 with NO state change.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ControlRoles))]
    public async Task Start_is_200_for_a_session_control_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    [Theory]
    [MemberData(nameof(ControlRoles))]
    public async Task End_is_200_for_a_session_control_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Ended);
    }

    [Theory]
    [MemberData(nameof(NonControlRoles))]
    public async Task Start_is_403_for_a_non_control_workspace_role_and_does_not_change_status(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The denial must not have started the session.
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
    }

    [Theory]
    [MemberData(nameof(NonControlRoles))]
    public async Task End_is_403_for_a_non_control_workspace_role_and_does_not_change_status(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The denial must not have ended the session.
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    // =====================================================================
    // TENANT / OBJECT ISOLATION (404-hide) and 400, each asserting no state
    // change. The 403-vs-404 distinction is the heart of the story.
    // =====================================================================

    [Fact]
    public async Task Start_is_404_for_a_session_in_another_tenant_and_does_not_change_status()
    {
        // T5: a real, Prepared session in org B, owned by a caller who is a Host of
        // its workspace, but addressed with organizationSlug = A (the caller's own
        // org). The cross-tenant id is hidden as 404, never 403, and the session is
        // not started.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid sessionInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B Session", SessionStatus.Prepared);
            sessionInBId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInBId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInBId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task Start_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        // T1 object-level authorization: the caller is an organization member (even
        // an Owner) in org A and the session is in org A, but the caller is NOT a
        // member of the session's workspace. A non-member must not learn the session
        // exists, so this is a hidden 404, NOT 403, and the session is not started.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            // The caller is an Owner of the ORG but not a member of the workspace.
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // Only the insider is a member of the workspace.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task End_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    [Fact]
    public async Task Start_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // T1/T5 cross-workspace WITHIN one tenant: the caller is a Host of workspace
        // X in org A, but the session lives in a sibling workspace Y of the SAME org
        // that the caller is not a member of. Workspace membership is checked against
        // the SESSION'S own workspace, so a control role held in X never confers
        // standing in Y: the session is hidden as 404 (not 403) and is not started.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid sessionInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            // A sibling workspace in the same org; the caller is NOT a member of it.
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var session = await db.AddSessionAsync(org.Id, workspaceY.Id, "S", SessionStatus.Prepared);
            sessionInYId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInYId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInYId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task End_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // The End twin of the cross-workspace-within-one-tenant case: a Host of
        // workspace X cannot end a Live session that lives in sibling workspace Y of
        // the same org. Hidden as 404, and the session stays Live.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-of-x";
        Guid sessionInYId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceX = await db.AddWorkspaceAsync(org.Id, "workspace-x", "Workspace X");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceX.Id, user.Id, MembershipRole.Host);
            var workspaceY = await db.AddWorkspaceAsync(org.Id, "workspace-y", "Workspace Y");
            var session = await db.AddSessionAsync(org.Id, workspaceY.Id, "S", SessionStatus.Live);
            sessionInYId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInYId}/end?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInYId, SessionStatus.Live);
    }

    [Fact]
    public async Task Start_is_404_when_caller_is_not_entitled_to_the_target_org()
    {
        // T5: the caller is a member of org B only; addressing a session under org A
        // is hidden as 404 at the org boundary, before any session lookup.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller IS a Host in org A but the token only asserts org B. The
        // claim mismatch denies before any membership is consulted; the session is
        // hidden as 404 and is not started.
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        // Token asserts only org B, not the targeted org A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task Start_is_404_for_an_unknown_session_in_the_callers_org()
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
        var response = await client.PostAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Start_is_404_for_a_malformed_or_empty_session_id(string sessionId)
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
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_is_400_without_the_organization_query_parameter()
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A 400 on the query parameter must not have started the session.
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
    }

    [Fact]
    public async Task End_is_400_without_the_organization_query_parameter()
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end",
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Live);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Seeds a session in <paramref name="seedStatus"/>, sends the
    /// <paramref name="command"/> ("start"/"end") as a Host of the session's
    /// workspace, asserts 409 Conflict and asserts the persisted status is
    /// <paramref name="expectedStatusAfter"/> (unchanged).
    /// </summary>
    private static async Task AssertConflictLeavesStatusUnchangedAsync(
        SessionStatus seedStatus,
        string command,
        SessionStatus expectedStatusAfter)
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", seedStatus);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/{command}?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, expectedStatusAfter);
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
