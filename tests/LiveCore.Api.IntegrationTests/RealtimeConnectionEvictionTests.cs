using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for re-authorizing realtime connections on role change and participant removal
/// (CORE-RTC-002) — the story's required "Integration: a participant removed mid-session receives no further
/// events on the open socket; a demoted host loses host-group deliveries". They open a REAL SignalR hub
/// connection against the in-memory test server (<see cref="WorkspaceApiFactory"/>: test auth + EF Core
/// SQLite), so the connection runs the production <c>SessionHub.OnConnectedAsync</c> — it is admitted by the
/// real <see cref="RealtimeConnectionResolver"/> and recorded in the real
/// <see cref="RealtimeConnectionRegistry"/> — and is then torn down by the production eviction path when the
/// caller's standing changes mid-session. "Stops receiving events on the open socket" is observed directly:
/// the abort closes the live connection, raising its <c>Closed</c> event.
///
/// Each scenario also asserts PRECISION — a bystander connection that did NOT lose standing stays connected —
/// so the eviction only ever removes the affected connection and never widens or narrows anyone else's
/// audience (threat T3 in docs/07_SECURITY_THREAT_MODEL.md). The eviction triggers are the real production
/// seams: the participant removal goes through <see cref="SessionParticipantLeaveService"/> (the
/// <c>Participant.Remove</c> path), and the member demotion through the public
/// <see cref="IRealtimeConnectionEvictor"/> the same way a future workspace role-change command will raise it.
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionEvictionTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_participant_removed_mid_session_is_evicted_from_its_open_socket()
    {
        await using var factory = new WorkspaceApiFactory();
        var seed = await SeedAsync(factory);

        // The participant connects (owns an active participant) and a host connects alongside as the bystander.
        await using var participant = await ConnectAsync(factory, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
        await using var host = await ConnectAsync(factory, seed.HostSubject, seed.SessionId, participantId: null);
        await WaitForRegisteredAsync(factory, expected: 2);

        // The participant is removed mid-session through the real leave/remove flow.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var leaveService = scope.ServiceProvider.GetRequiredService<SessionParticipantLeaveService>();
            var result = await leaveService.LeaveAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, seed.ParticipantId, CancellationToken.None);
            Assert.Equal(ParticipantLeaveOutcome.Left, result.Outcome);
        }

        // Their open socket is torn down — it receives no further events — not only on reconnect.
        await AssertEvictedAsync(participant);

        // Precision: the host's connection (it lost no standing) is untouched.
        Assert.Equal(HubConnectionState.Connected, host.Connection.State);
        Assert.False(host.Closed.IsCompleted);
    }

    [Fact]
    public async Task A_demoted_host_is_evicted_and_loses_host_group_deliveries()
    {
        await using var factory = new WorkspaceApiFactory();
        var seed = await SeedAsync(factory);

        // The host connects (joins the host groups) and a participant connects alongside as the bystander.
        await using var host = await ConnectAsync(factory, seed.HostSubject, seed.SessionId, participantId: null);
        await using var participant = await ConnectAsync(factory, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
        await WaitForRegisteredAsync(factory, expected: 2);

        // The host is demoted mid-session: the role-change command raises the eviction seam for the subject in
        // the workspace (a workspace role-change endpoint is a later story; this is the exact signal it raises).
        var evictor = factory.Services.GetRequiredService<IRealtimeConnectionEvictor>();
        await evictor.EvictWorkspaceMemberAsync(
            seed.OrganizationId, seed.WorkspaceId, seed.HostProfileId, CancellationToken.None);

        // The host's open socket is torn down, so it loses the host-group deliveries it was receiving — not
        // only on reconnect (it would re-join only its new role's groups, never the host groups, on reconnect).
        await AssertEvictedAsync(host);

        // Precision: the participant's connection (its participant standing is unchanged by a membership role
        // change) is untouched.
        Assert.Equal(HubConnectionState.Connected, participant.Connection.State);
        Assert.False(participant.Closed.IsCompleted);
    }

    // =====================================================================
    // Connection + assertion helpers.
    // =====================================================================

    /// <summary>
    /// Opens a real hub connection to <c>/hubs/session</c> as the given caller, over the in-memory test server
    /// (long polling, header authentication via <see cref="TestAuthenticationHandler"/>). A non-null
    /// <paramref name="participantId"/> opens a participant connection; null opens a host/observer member
    /// connection. The <c>Closed</c> event is captured before the connection starts so an eviction abort is
    /// never missed.
    /// </summary>
    private static async Task<HubClient> ConnectAsync(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid? participantId)
    {
        var query = $"organizationSlug={_orgA}&sessionId={sessionId}";
        if (participantId is { } pid)
        {
            query += $"&participantId={pid}";
        }

        var url = new Uri(factory.Server.BaseAddress, $"hubs/session?{query}");
        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthenticationHandler.SubjectHeader, subject);
                options.Headers.Add(TestAuthenticationHandler.IssuerHeader, _issuer);
                options.Headers.Add(TestAuthenticationHandler.OrganizationHeader, _orgA);
            })
            .Build();

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Any close (the server-initiated eviction abort) completes the signal; the close exception, if any, is
        // expected and not surfaced.
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        using var startCancellation = new CancellationTokenSource(_timeout);
        await connection.StartAsync(startCancellation.Token);
        Assert.Equal(HubConnectionState.Connected, connection.State);
        return new HubClient(connection, closed.Task);
    }

    /// <summary>
    /// Waits until the hub has finished admitting <paramref name="expected"/> connections (recorded in the
    /// registry), so a test triggers an eviction only after the connection it targets is actually registered —
    /// the deterministic barrier that makes the over-the-wire test reliable.
    /// </summary>
    private static async Task WaitForRegisteredAsync(WorkspaceApiFactory factory, int expected)
    {
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        var deadline = DateTime.UtcNow.Add(_timeout);
        while (registry.Count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(
            registry.Count >= expected,
            $"Expected at least {expected} registered realtime connections but saw {registry.Count}.");
    }

    /// <summary>Asserts the connection was torn down by the eviction: its <c>Closed</c> event fired and it left the Connected state.</summary>
    private static async Task AssertEvictedAsync(HubClient client)
    {
        var completed = await Task.WhenAny(client.Closed, Task.Delay(_timeout));
        Assert.True(completed == client.Closed, "The connection was not evicted (its socket stayed open).");
        Assert.NotEqual(HubConnectionState.Connected, client.Connection.State);
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    private static async Task<Seed> SeedAsync(WorkspaceApiFactory factory)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            // A host: org + workspace Host role, so the realtime resolver admits a host-group connection.
            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            // A participant: an org member (so the tenant resolves) who owns an active participant in the
            // session's workspace, so the resolver admits a participant connection.
            var participantUser = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, "Stage Left");

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);

            seed = new Seed(
                org.Id,
                workspace.Id,
                session.Id,
                "host-a",
                hostUser.Id,
                "participant-a",
                participant.Id);
        });

        return seed;
    }

    private readonly record struct Seed(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        string HostSubject,
        Guid HostProfileId,
        string ParticipantSubject,
        Guid ParticipantId);

    private sealed record HubClient(HubConnection Connection, Task Closed) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
