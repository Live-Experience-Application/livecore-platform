// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// CROSS-INSTANCE realtime-connection eviction over the REAL Valkey/Redis backplane (CORE-RES-008, the
/// "Multi-Instance Runtime Correctness" epic). It boots TWO <see cref="WorkspaceApiFactory"/> instances that model
/// two API replicas of a multi-instance deployment: both read/write ONE shared PostgreSQL system of record and run
/// the UNCHANGED production realtime wiring (<see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>),
/// which — with a <c>Realtime:Backplane:ConnectionString</c> configured — wires the
/// <c>RedisRealtimeConnectionEvictionBackplane</c> + listener (CORE-RES-008) alongside the Redis SignalR backplane.
/// A demotion/removal performed on one instance therefore publishes its eviction descriptor over Redis pub/sub, and
/// the test observes the target socket — held by the OTHER instance — being aborted, while an unrelated connection
/// stays open.
///
/// This is the story's required test: a demotion on instance A aborts the target socket on instance B; an unrelated
/// connection is untouched. It mirrors <see cref="RedisBackplanePropagationTests"/> (the same two-instance + real
/// backplane harness) and the single-instance <c>RealtimeConnectionEvictionTests</c> (the same over-the-wire abort
/// observation).
///
/// It is SKIPPED unless BOTH a real PostgreSQL provider (<see cref="PostgresTestDatabase.IsConfigured"/>, the shared
/// system of record) and a Valkey/Redis backplane (<see cref="RedisBackplaneTestServer.IsConfigured"/>) are
/// configured out of band — the CI <c>integration-postgres</c> job's service containers. A default <c>dotnet test</c>
/// needs neither and skips it; the deterministic cross-instance contract is proven by the in-memory unit tests.
/// Eviction only ever REMOVES a connection, so propagating it never widens an audience (threat T3). All fixtures are
/// generic (AGENTS.md).
/// </summary>
public sealed class RedisRealtimeConnectionEvictionPropagationTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    // The eviction propagation test needs BOTH a shared PostgreSQL system of record (so both modeled replicas see the
    // same seeded data) and a Redis/Valkey backplane (so an eviction crosses instances). With either absent it is
    // skipped, exactly as the PostgreSQL-only coverage is skipped on a default local run.
    private static bool ConfiguredForCrossInstance =>
        PostgresTestDatabase.IsConfigured && RedisBackplaneTestServer.IsConfigured;

    [Fact]
    public async Task A_demotion_on_one_instance_aborts_the_member_socket_on_another_instance()
    {
        if (!ConfiguredForCrossInstance)
        {
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        var sharedDatabase = PostgresTestDatabase.CreateMigratedDatabase();
        try
        {
            // Both replicas share the SAME channel prefix, so an eviction published on one is delivered on the very
            // channel the other subscribes to.
            var channelPrefix = NewChannelPrefix("evict");

            await using var publisher = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);
            await using var receiver = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);

            var seed = await SeedAsync(publisher);

            // The demoted host's MEMBER socket is held ONLY by the receiver instance, and a participant connects
            // alongside it as the precision bystander (its participant standing is unchanged by a role change).
            await using var host = await ConnectAsync(receiver, seed.HostSubject, seed.SessionId, participantId: null);
            await using var participant = await ConnectAsync(
                receiver, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
            await WaitForRegisteredAsync(receiver, expected: 2);

            // The demotion is handled on the PUBLISHER instance, which holds no matching socket; it broadcasts the
            // eviction over the backplane (the exact signal a future workspace role-change command raises). Re-raise
            // in the poll loop so the assertion never races the receiver's subscription becoming active (an early
            // publish would be dropped — pub/sub has no retention — and re-eviction is idempotent).
            var evictor = publisher.Services.GetRequiredService<IRealtimeConnectionEvictor>();
            var deadline = DateTime.UtcNow.Add(_timeout);
            while (!host.Closed.IsCompleted && DateTime.UtcNow < deadline)
            {
                await evictor.EvictWorkspaceMemberAsync(
                    seed.OrganizationId, seed.WorkspaceId, seed.HostProfileId, CancellationToken.None);
                await Task.WhenAny(host.Closed, Task.Delay(100));
            }

            // The eviction crossed the real backplane: the demoted host's socket on the OTHER instance was aborted.
            await AssertEvictedAsync(host);

            // Precision: the participant's connection on the same instance (its participant standing is unchanged) is
            // untouched — the eviction only ever removed the affected connection (threat T3).
            Assert.Equal(HubConnectionState.Connected, participant.Connection.State);
            Assert.False(participant.Closed.IsCompleted);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(sharedDatabase);
        }
    }

    [Fact]
    public async Task A_participant_removal_on_one_instance_aborts_the_participant_socket_on_another_instance()
    {
        if (!ConfiguredForCrossInstance)
        {
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        var sharedDatabase = PostgresTestDatabase.CreateMigratedDatabase();
        try
        {
            var channelPrefix = NewChannelPrefix("evict");

            await using var publisher = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);
            await using var receiver = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);

            var seed = await SeedAsync(publisher);

            // The participant's socket is held ONLY by the receiver instance, with a host connection alongside it as
            // the precision bystander.
            await using var participant = await ConnectAsync(
                receiver, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
            await using var host = await ConnectAsync(receiver, seed.HostSubject, seed.SessionId, participantId: null);
            await WaitForRegisteredAsync(receiver, expected: 2);

            var evictor = publisher.Services.GetRequiredService<IRealtimeConnectionEvictor>();
            var deadline = DateTime.UtcNow.Add(_timeout);
            while (!participant.Closed.IsCompleted && DateTime.UtcNow < deadline)
            {
                await evictor.EvictParticipantAsync(
                    seed.OrganizationId, seed.WorkspaceId, seed.SessionId, seed.ParticipantId, CancellationToken.None);
                await Task.WhenAny(participant.Closed, Task.Delay(100));
            }

            await AssertEvictedAsync(participant);

            // Precision: the host's connection (it lost no standing) is untouched.
            Assert.Equal(HubConnectionState.Connected, host.Connection.State);
            Assert.False(host.Closed.IsCompleted);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(sharedDatabase);
        }
    }

    // =====================================================================
    // Connection + assertion helpers (a focused subset of RealtimeConnectionEvictionTests, scoped to this suite).
    // =====================================================================

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
        // Any close (the cross-instance eviction abort) completes the signal; the close exception, if any, is expected.
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
    /// Waits until the given instance has admitted <paramref name="expected"/> connections (recorded in ITS
    /// registry), so the eviction is published only after the connections it must reach across the backplane have
    /// actually joined — the deterministic barrier registration is the last step of a successful
    /// <c>OnConnectedAsync</c>.
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
        Assert.True(completed == client.Closed, "The connection was not evicted across the backplane (its socket stayed open).");
        Assert.NotEqual(HubConnectionState.Connected, client.Connection.State);
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    /// <summary>
    /// Seeds an org with a host (Host in org AND workspace, so the resolver admits a host-group member connection),
    /// a workspace, a Live session, and one participant owned by its own user (so the resolver admits a participant
    /// connection).
    /// </summary>
    private static async Task<Seed> SeedAsync(WorkspaceApiFactory factory)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            var participantUser = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, "Stage Left");

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);

            seed = new Seed(
                org.Id, workspace.Id, session.Id, "host-a", hostUser.Id, "participant-a", participant.Id);
        });

        return seed;
    }

    private static string NewChannelPrefix(string role) => $"livecore-it-{role}-{Guid.NewGuid():N}";

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

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that models ONE API instance of a multi-instance deployment: it points the
    /// production persistence at a SHARED PostgreSQL database (so every instance reads/writes the same system of
    /// record) and configures the production Valkey/Redis backplane with the given channel prefix, which wires both
    /// the Redis SignalR backplane (CORE-OPS-007) and the cross-instance eviction backplane + listener (CORE-RES-008)
    /// through the UNCHANGED <c>AddLiveCoreRealtime</c> registration.
    /// </summary>
    private sealed class RedisInstanceApiFactory : WorkspaceApiFactory
    {
        private readonly string _sharedConnectionString;
        private readonly string _channelPrefix;
        private readonly Guid _instanceId = Guid.NewGuid();

        public RedisInstanceApiFactory(string sharedConnectionString, string channelPrefix)
        {
            _sharedConnectionString = sharedConnectionString;
            _channelPrefix = channelPrefix;
        }

        // Point the production Npgsql registration at the SHARED database; a distinct application name gives each
        // in-process instance its own connection pool (pools are keyed by the connection string). The shared
        // database's lifetime is owned by the test; the base's per-factory drop on dispose is an idempotent
        // best-effort DROP IF EXISTS, so several instances dropping the same database is harmless.
        protected override string CreatePostgresDatabase() =>
            new NpgsqlConnectionStringBuilder(_sharedConnectionString)
            {
                ApplicationName = $"livecore-it-instance-{_instanceId:N}",
            }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Configure the production realtime backplane: AddLiveCoreRealtime reads Realtime:Backplane:* from
            // configuration, so with a connection string present it wires the Redis SignalR backplane AND the
            // cross-instance eviction backplane + listener (the unchanged production conditionals), namespaced by the
            // channel prefix.
            builder.UseSetting("Realtime:Backplane:ConnectionString", RedisBackplaneTestServer.ConnectionString);
            builder.UseSetting("Realtime:Backplane:ChannelPrefix", _channelPrefix);

            base.ConfigureWebHost(builder);
        }
    }
}
