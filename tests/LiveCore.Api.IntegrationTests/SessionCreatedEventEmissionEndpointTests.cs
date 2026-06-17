// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the session-create EVENT emission (CORE-EVT-004): creating a session must
/// append exactly one durable, HOST-ONLY <c>SessionCreated</c> session event to the new session's stream,
/// atomically with the session row, with an identifier-only payload (no content, threat T7), and the
/// recipient resolver / reconnect replay must deliver it to the session HOSTS only — never an observer or a
/// participant. They drive the REAL application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), with the REAL recipient resolver and
/// replay filter in the loop, so the documented "persist event -&gt; compute recipients -&gt; project payload
/// -&gt; send to recipient groups" flow runs end-to-end exactly as in production.
///
/// Coverage, per the story's required tests ("each newly-emitted event persists and reaches its documented
/// recipients with identifier-only payloads (no PII, T7)"):
/// <list type="bullet">
///   <item>EMISSION — a create persists exactly one host-only <c>SessionCreated</c> event (no visibility
///   subject, no selected participant), authored by the creating host, with the session id + Prepared status
///   in its payload (identifiers only).</item>
///   <item>DELIVERY + REPLAY — the host replays the <c>SessionCreated</c> event; an OBSERVER and an active
///   PARTICIPANT replay NOTHING (the host-only prep event never reaches the audience, live or on replay;
///   threats T2/T7).</item>
///   <item>NEGATIVE AUTHORIZATION / ISOLATION — a non-create workspace role is 403 and emits NO event; a
///   cross-tenant create is hidden as 404 and emits nothing — so a denied/rejected create can never leak a
///   prep event (fail-closed; threats T1/T3/T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionCreatedEventEmissionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Create_persists_exactly_one_host_only_SessionCreated_event()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Opening Session"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionResponse>(_json);
        Assert.NotNull(created);

        // Exactly one durable event for the new session: SessionCreated, audience-wide routing fields unset
        // (no selected participant), subjectless, authored by the creating host. The host-only ROUTING is the
        // event TYPE's policy (asserted via replay below); the stored row carries no target/subject.
        var sessionEvent = Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, created.Id));
        Assert.Equal(SessionEventTypes.SessionCreated, sessionEvent.EventType);
        Assert.True(SessionEventTypes.IsHostOnly(sessionEvent.EventType));
        Assert.Equal(created.Id, sessionEvent.SessionId);
        Assert.Equal(seed.WorkspaceId, sessionEvent.WorkspaceId);
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.HasVisibilitySubject);
        Assert.Equal(seed.HostProfileId, sessionEvent.CreatedBy);

        // Identifier-only payload: the session id + Prepared status name, never the title (threat T7).
        Assert.Contains(created.Id.ToString(), sessionEvent.Payload, StringComparison.Ordinal);
        Assert.Contains(nameof(SessionStatus.Prepared), sessionEvent.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening Session", sessionEvent.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionCreated_reaches_hosts_only_never_observers_or_participants_via_replay()
    {
        await using var factory = new WorkspaceApiFactory();
        const string hostSubject = "host-a";
        const string observerSubject = "observer-a";
        const string participantSubject = "participant-a";
        Guid workspaceId = Guid.Empty;
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, hostSubject);
            var observerUser = await db.AddUserAsync(_issuer, observerSubject);
            var participantUser = await db.AddUserAsync(_issuer, participantSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, observerUser.Id, MembershipRole.Observer);
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, observerUser.Id, MembershipRole.Observer);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, displayName: "P");
            workspaceId = workspace.Id;
            participantId = participant.Id;
        });

        using var host = factory.CreateClientFor(hostSubject, _issuer, _orgA);
        var response = await host.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Opening Session"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionResponse>(_json);
        Assert.NotNull(created);

        // The host replays the event (the session-hosts group always receives it).
        var hostReplay = await ReplayAsync(host, created.Id);
        Assert.Equal(SessionEventTypes.SessionCreated, Assert.Single(hostReplay.Events).EventType);

        // An observer replays NOTHING: a host-only prep event never reaches the observers group.
        using var observer = factory.CreateClientFor(observerSubject, _issuer, _orgA);
        var observerReplay = await ReplayAsync(observer, created.Id);
        Assert.Empty(observerReplay.Events);

        // An active participant replays NOTHING: the host-only prep event never fans out to a participant,
        // even though they may join and replay the stream later (threats T2/T7).
        using var participant = factory.CreateClientFor(participantSubject, _issuer, _orgA);
        var participantReplay = await ReplayAsync(participant, created.Id, participantId);
        Assert.Empty(participantReplay.Events);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Create_by_a_non_create_role_is_403_and_emits_no_event(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{seed.WorkspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Should Not Exist"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Nothing was created, so no session event exists for the workspace at all.
        Assert.Empty(await WorkspaceSessionEventsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Create_for_a_workspace_in_another_tenant_is_404_and_emits_no_event()
    {
        // T5: a real workspace in org B addressed with organizationSlug = A (the caller's own org). Hidden as
        // 404, and NO event is written for the org-B workspace.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        Guid orgBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            workspaceInBId = workspaceInB.Id;
            orgBId = orgB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceInBId}/sessions",
            new CreateSessionRequest(_orgA, "Cross Tenant"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await WorkspaceSessionEventsAsync(factory, orgBId, workspaceInBId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<Seed> SeedWorkspaceAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            seed = new Seed(org.Id, workspace.Id, user.Id);
        });
        return seed;
    }

    private static async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<SessionEvent>> WorkspaceSessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    private static async Task<ReplayDto> ReplayAsync(HttpClient client, Guid sessionId, Guid? participantId = null)
    {
        var url = $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}";
        if (participantId is { } participant)
        {
            url += $"&participantId={participant}";
        }

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid HostProfileId);

    private sealed record ReplayDto(
        Guid SessionId,
        IReadOnlyList<ReplayItemDto> Events,
        DateTimeOffset GeneratedAt);

    private sealed record ReplayItemDto(
        Guid EventId,
        string EventType,
        Guid SessionId,
        string Payload,
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid? TargetParticipantId);
}
