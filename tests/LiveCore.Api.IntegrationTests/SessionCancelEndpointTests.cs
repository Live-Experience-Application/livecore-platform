// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the session cancel command (CORE-LIFE-010,
/// <c>POST /api/v1/sessions/{sessionId}/cancel</c>, csv/api_routes.csv roles
/// "Host,CoHost,Owner,Admin"). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> audit) is exercised end-to-end.
///
/// The story's decision is a SOFT, audited, terminal cancel (a new <c>Cancelled</c>
/// value on the <see cref="Session"/> aggregate, not a hard delete). The acceptance
/// criteria proven here ("cancel/delete a session, append-only history preserved,
/// negative role/tenant tests"):
/// <list type="bullet">
///   <item>a host cancels a not-yet-started (<see cref="SessionStatus.Prepared"/>)
///   session; it becomes <see cref="SessionStatus.Cancelled"/> with its live-timeline
///   timestamps still null, and the cancel is audited exactly once with the
///   Prepared -&gt; Cancelled transition;</item>
///   <item>the append-only <c>session_events</c> and <c>audit_logs</c> the session
///   anchors are PRESERVED — the soft cancel never deletes the session row, so neither
///   its event stream nor any prior audit fact is cascade-erased;</item>
///   <item>negative role/tenant: 401 for missing auth; the full seven-role
///   WORKSPACE-membership sweep (allowed {Owner, Admin, Host, CoHost} vs denied
///   {Participant, Observer, Auditor} -&gt; 403); 404 (hidden) for a cross-tenant /
///   unknown session, a non-member, a host of a different workspace, and a token/org
///   mismatch; 400 for a missing organization; 409 for any non-Prepared session.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit
/// enumerations, never an ordering comparison. Every negative case asserts the
/// SPECIFIC status code AND the unchanged persisted status AND that nothing was
/// audited, so a wrong pass that also mutated state or wrote a stray audit fact is
/// caught.
/// </summary>
public sealed class SessionCancelEndpointTests
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

    /// <summary>The workspace-member roles that may NOT cancel a session (403 cases).</summary>
    public static TheoryData<MembershipRole> NonControlRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    /// <summary>The non-Prepared statuses, from which a cancel is a 409 conflict.</summary>
    public static TheoryData<SessionStatus> NonCancellableStatuses =>
    [
        SessionStatus.Live,
        SessionStatus.Ended,
        SessionStatus.Cancelled,
    ];

    // =====================================================================
    // 401 — unauthenticated. RequireAuthorization() challenges first.
    // =====================================================================

    [Fact]
    public async Task Cancel_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsync(
            $"/api/v1/sessions/{Guid.CreateVersion7()}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — cancel a Prepared session; Cancelled, audited, history kept.
    // =====================================================================

    [Fact]
    public async Task Cancel_a_prepared_session_is_200_becomes_cancelled_and_audits_the_transition()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid hostProfileId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            hostProfileId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadSessionAsync(response);
        Assert.Equal(sessionId, body.Id);
        Assert.Equal(nameof(SessionStatus.Cancelled), body.Status);
        // A cancelled session never opened a live timeline.
        Assert.Null(body.StartedAt);
        Assert.Null(body.EndedAt);
        Assert.Equal("Opening Night", body.Title);

        // The persisted session is Cancelled (and the row still EXISTS — soft cancel, not a delete).
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Cancelled);

        // The cancel is audited exactly once, capturing the actor, the session and the Prepared -> Cancelled
        // status transition.
        var audit = Assert.Single(await ListSessionCancelledAsync(factory));
        Assert.Equal(AuditAction.SessionCancelled, audit.Action);
        Assert.Equal(hostProfileId, audit.ActorUserProfileId);
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(nameof(Session), audit.ResourceType);
        Assert.Equal(sessionId, audit.ResourceId);
        Assert.Equal(nameof(SessionStatus.Prepared), audit.PreviousState);
        Assert.Equal(nameof(SessionStatus.Cancelled), audit.NewState);
        Assert.Null(audit.TargetParticipantId);
    }

    [Fact]
    public async Task Cancel_preserves_the_append_only_session_events_and_prior_audit_logs()
    {
        // The crown-jewel of the story: a cancel is a SOFT status transition, so the session row survives and
        // neither its append-only session_events nor any prior audit_logs are cascade-erased. We seed one of
        // each up front, cancel, and assert both survive while the cancel APPENDS its own audit fact.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid seededEventId = Guid.Empty;
        Guid priorAuditId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);
            sessionId = session.Id;

            // An append-only session event anchored to the session (audience-wide, no subject).
            var sessionEvent = SessionEvent.Create(
                org.Id, workspace.Id, session.Id, "ContentRevealed", user.Id,
                targetParticipantId: null, payload: "{}", schemaVersion: 1, createdAt: TestData.SeedTime);
            // Stamp the per-session sequence the append path would assign before persisting, so the seeded
            // row carries a real position (at least 1) exactly as production does and satisfies the
            // ck_session_events_sequence CHECK constraint (CORE-CONC-009).
            sessionEvent.AssignSequence(1);
            seededEventId = sessionEvent.Id;
            db.SessionEvents.Add(sessionEvent);

            // A prior, unrelated audit fact in the same tenant that must survive the cancel.
            var priorAudit = AuditLogEntry.ForVisibilityRuleChange(
                org.Id, workspace.Id, user.Id, "Scene", Guid.CreateVersion7(),
                targetParticipantId: null, previousState: null, newState: "Visible", createdAt: TestData.SeedTime);
            priorAuditId = priorAudit.Id;
            db.AuditLogs.Add(priorAudit);
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The session row survives (it was soft-cancelled, not deleted).
        Assert.True(await context.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId));

        // The append-only session event the session anchors is PRESERVED, never cascade-deleted.
        Assert.True(await context.SessionEvents.AsNoTracking().AnyAsync(e => e.Id == seededEventId));

        // The prior audit fact survives, and the cancel APPENDED its own SessionCancelled fact (append-only:
        // nothing is updated or deleted) — so the tenant now has exactly the two facts.
        Assert.True(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Id == priorAuditId));
        Assert.True(await context.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditAction.SessionCancelled && a.ResourceId == sessionId));
        Assert.Equal(2, await context.AuditLogs.AsNoTracking().CountAsync());
    }

    // =====================================================================
    // AUTHORIZATION — the full seven-role WORKSPACE-membership sweep.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ControlRoles))]
    public async Task Cancel_is_200_for_a_session_control_workspace_role(MembershipRole role)
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
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Cancelled);
        Assert.Single(await ListSessionCancelledAsync(factory));
    }

    [Theory]
    [MemberData(nameof(NonControlRoles))]
    public async Task Cancel_is_403_for_a_non_control_workspace_role_and_changes_nothing(MembershipRole role)
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
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The denial must not have cancelled the session or written an audit fact.
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    // =====================================================================
    // 409 — a non-Prepared session cannot be cancelled, and nothing changes.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonCancellableStatuses))]
    public async Task Cancel_is_409_for_a_non_prepared_session_and_changes_nothing(SessionStatus seedStatus)
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
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The session keeps its seeded status, and a rejected cancel writes no audit fact.
        await AssertSessionStatusAsync(factory, sessionId, seedStatus);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    // =====================================================================
    // TENANT / OBJECT ISOLATION (404-hide) and 400, each asserting no change.
    // =====================================================================

    [Fact]
    public async Task Cancel_is_404_for_a_session_in_another_tenant_and_changes_nothing()
    {
        // T5: a real, Prepared session in org B, owned by a caller who is a Host of its workspace, but
        // addressed with organizationSlug = A. The cross-tenant id is hidden as 404, never 403, and the
        // session is not cancelled.
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
            $"/api/v1/sessions/{sessionInBId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInBId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    [Fact]
    public async Task Cancel_is_404_for_an_org_member_who_is_not_a_member_of_the_sessions_workspace()
    {
        // T1 object-level authorization: the caller is an org Owner in org A and the session is in org A, but
        // the caller is NOT a member of the session's workspace. A non-member must not learn the session
        // exists, so this is a hidden 404 (not 403), and the session is not cancelled.
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
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    [Fact]
    public async Task Cancel_is_404_for_a_host_of_a_different_workspace_in_the_same_org()
    {
        // T1/T5 cross-workspace WITHIN one tenant: the caller is a Host of workspace X in org A, but the
        // session lives in a sibling workspace Y of the SAME org that the caller is not a member of. Workspace
        // membership is checked against the SESSION'S own workspace, so a control role in X never confers
        // standing in Y: the session is hidden as 404 (not 403) and is not cancelled.
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
            var session = await db.AddSessionAsync(org.Id, workspaceY.Id, "S", SessionStatus.Prepared);
            sessionInYId = session.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionInYId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionInYId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    [Fact]
    public async Task Cancel_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller IS a Host in org A but the token only asserts org B. The claim mismatch denies before
        // any membership is consulted; the session is hidden as 404 and is not cancelled.
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

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    [Fact]
    public async Task Cancel_is_404_for_an_unknown_session_in_the_callers_org()
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
            $"/api/v1/sessions/{Guid.CreateVersion7()}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Cancel_is_404_for_a_malformed_or_empty_session_id(string sessionId)
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
            $"/api/v1/sessions/{sessionId}/cancel?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_is_400_without_the_organization_query_parameter()
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
        var response = await client.PostAsync($"/api/v1/sessions/{sessionId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A 400 on the query parameter must not have cancelled the session.
        await AssertSessionStatusAsync(factory, sessionId, SessionStatus.Prepared);
        Assert.Empty(await ListSessionCancelledAsync(factory));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

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

    private static async Task<IReadOnlyList<AuditLogEntry>> ListSessionCancelledAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditAction.SessionCancelled)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
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
