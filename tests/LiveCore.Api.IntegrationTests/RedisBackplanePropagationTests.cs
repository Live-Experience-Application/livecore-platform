// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// CROSS-INSTANCE realtime propagation tests over the REAL Redis/Valkey SignalR backplane (CORE-TST-003).
/// They boot TWO (or three) <see cref="WorkspaceApiFactory"/> instances that model separate API instances of
/// a multi-instance deployment: every instance reads/writes ONE shared PostgreSQL system of record and runs
/// the UNCHANGED production realtime wiring (<see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>),
/// which - with a <c>Realtime:Backplane:ConnectionString</c> configured - selects the Redis/Valkey-backed
/// <c>HubLifetimeManager</c> (CORE-OPS-007). A reveal performed on one instance therefore publishes its
/// already-authorized group send over Redis pub/sub, and the test observes whether it reaches a client whose
/// live hub connection is held by a DIFFERENT instance.
///
/// This closes the gap the existing suites leave: <c>RealtimeServiceCollectionExtensionsTests</c>
/// asserts only that <c>AddStackExchangeRedis</c> is registered when configured (DI wiring), and
/// <c>RealtimeHubDeliveryTests</c> / <c>RealtimeDeliveryEndpointTests</c> exercise delivery and denial on a
/// SINGLE instance - none ever proves an event computed on one instance reaches a client on another, nor that
/// the channel prefix isolates two deployments sharing one server. Here both are proven end-to-end over real
/// pub/sub.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>CROSS-INSTANCE FAN-OUT: a reveal on the publisher instance is received by a participant whose
///   socket is held by a second instance - delivery crosses the backplane.</item>
///   <item>CHANNEL-PREFIX ISOLATION: with a DIFFERENT <c>ChannelPrefix</c>, an instance never receives the
///   publisher's reveal, while a same-prefix instance does (the deterministic barrier proving the publish
///   really crossed Redis) - so the prefix namespaces the deployment and does not leak.</item>
/// </list>
///
/// The test is SKIPPED unless both a real PostgreSQL provider (<see cref="PostgresTestDatabase.IsConfigured"/>,
/// for the shared system of record) and a Redis/Valkey backplane (<see cref="RedisBackplaneTestServer.IsConfigured"/>)
/// are configured out of band - the CI <c>integration-postgres</c> job's service containers. A default
/// <c>dotnet test</c> needs neither and skips it. Enabling scale-out never widens the audience: the backplane
/// only transports an already-computed, per-recipient delivery (threat T3). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RedisBackplanePropagationTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // The propagation test needs BOTH a shared PostgreSQL system of record (so every modeled instance sees the
    // same seeded data) and a Redis/Valkey backplane (so a group send fans out across instances). With either
    // absent it is skipped, exactly as the PostgreSQL-only coverage is skipped on a default local run.
    private static bool ConfiguredForCrossInstance =>
        PostgresTestDatabase.IsConfigured && RedisBackplaneTestServer.IsConfigured;

    [Fact]
    public async Task An_event_published_on_one_instance_reaches_a_client_connected_to_another_instance()
    {
        if (!ConfiguredForCrossInstance)
        {
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        // One shared system of record both instances read/write, as in production.
        var sharedDatabase = PostgresTestDatabase.CreateMigratedDatabase();
        try
        {
            // Both instances share the SAME channel prefix, so a group send on one is published on the very
            // channels the other subscribes to.
            var channelPrefix = NewChannelPrefix("shared");
            var resourceId = Guid.CreateVersion7();

            await using var publisher = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);
            await using var receiver = new RedisInstanceApiFactory(sharedDatabase, channelPrefix);

            var seed = await SeedRevealableSessionAsync(publisher, "participant-a", "Stage Left");

            // The participant's live socket is held ONLY by the receiver instance.
            await using var probe = await ConnectParticipantAsync(receiver, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
            await WaitForRegisteredAsync(receiver, expected: 1);

            // The host reveals on the PUBLISHER instance - a different instance from the one holding the socket.
            using (var host = publisher.CreateClientFor("host-a", _issuer, _orgA))
            {
                var response = await PostRevealAsync(host, seed.SessionId, resourceId, "reveal-1");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // The event crossed the Redis/Valkey backplane and was delivered to the client on the OTHER instance.
            var revealed = await AwaitContentRevealedAsync(probe);
            Assert.Equal(SessionEventTypes.ContentRevealed, EventTypeOf(revealed));
            Assert.Contains(seed.SessionId.ToString(), revealed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(resourceId.ToString(), revealed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(sharedDatabase);
        }
    }

    [Fact]
    public async Task A_reveal_does_not_reach_an_instance_configured_with_a_different_channel_prefix()
    {
        if (!ConfiguredForCrossInstance)
        {
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        var sharedDatabase = PostgresTestDatabase.CreateMigratedDatabase();
        try
        {
            var onPrefix = NewChannelPrefix("on");
            var offPrefix = NewChannelPrefix("off");
            var resourceId = Guid.CreateVersion7();

            await using var publisher = new RedisInstanceApiFactory(sharedDatabase, onPrefix);
            // SAME prefix as the publisher: its client is the deterministic barrier and MUST receive the reveal.
            await using var sameChannelReceiver = new RedisInstanceApiFactory(sharedDatabase, onPrefix);
            // DIFFERENT prefix: its client MUST receive nothing (the isolation under test).
            await using var otherChannelReceiver = new RedisInstanceApiFactory(sharedDatabase, offPrefix);

            var seed = await SeedRevealableSessionWithTwoParticipantsAsync(publisher);

            await using var sameProbe = await ConnectParticipantAsync(
                sameChannelReceiver, seed.ParticipantOneSubject, seed.SessionId, seed.ParticipantOneId);
            await using var otherProbe = await ConnectParticipantAsync(
                otherChannelReceiver, seed.ParticipantTwoSubject, seed.SessionId, seed.ParticipantTwoId);
            await WaitForRegisteredAsync(sameChannelReceiver, expected: 1);
            await WaitForRegisteredAsync(otherChannelReceiver, expected: 1);

            using (var host = publisher.CreateClientFor("host-a", _issuer, _orgA))
            {
                Assert.Equal(HttpStatusCode.OK, (await PostRevealAsync(host, seed.SessionId, resourceId, "reveal-1")).StatusCode);
            }

            // The SAME-prefix instance received the reveal across the backplane - the deterministic barrier:
            // once it has the event over the wire, a publish that ignored the prefix would already have reached
            // the different-prefix subscriber too (they are subscribed to entirely separate Redis channels, so
            // there is no later arrival to wait for).
            var revealed = await AwaitContentRevealedAsync(sameProbe);
            Assert.Contains(resourceId.ToString(), revealed, StringComparison.OrdinalIgnoreCase);

            // The DIFFERENT-prefix instance never received anything: the prefix namespaced the deployment's
            // channels and the publish did not leak across them.
            Assert.DoesNotContain(
                otherProbe.Received,
                envelope => envelope.Contains(resourceId.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.Empty(otherProbe.Received);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(sharedDatabase);
        }
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    /// <summary>
    /// Seeds an org with a host who may reveal (Host in org AND workspace), a workspace, a Live session, and
    /// one participant owned by its own user (so the resolver admits a participant connection and the audience
    /// fan-out delivers to it).
    /// </summary>
    private static async Task<Seed> SeedRevealableSessionAsync(
        WorkspaceApiFactory factory,
        string participantSubject,
        string participantDisplayName)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            var participantUser = await db.AddUserAsync(_issuer, participantSubject);
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, participantDisplayName);

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new Seed(session.Id, participantSubject, participant.Id);
        });
        return seed;
    }

    /// <summary>
    /// Seeds the same revealable session as <see cref="SeedRevealableSessionAsync"/> but with TWO participants,
    /// each owned by its own user, so an audience-wide reveal fans out to both participant groups - one
    /// connected to a same-prefix instance, one to a different-prefix instance.
    /// </summary>
    private static async Task<TwoParticipantSeed> SeedRevealableSessionWithTwoParticipantsAsync(WorkspaceApiFactory factory)
    {
        TwoParticipantSeed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            var userOne = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, userOne.Id, MembershipRole.Participant);
            var participantOne = await db.AddParticipantAsync(org.Id, workspace.Id, userOne.Id, "Stage Left");

            var userTwo = await db.AddUserAsync(_issuer, "participant-b");
            await db.AddOrganizationMemberAsync(org.Id, userTwo.Id, MembershipRole.Participant);
            var participantTwo = await db.AddParticipantAsync(org.Id, workspace.Id, userTwo.Id, "Stage Right");

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new TwoParticipantSeed(
                session.Id, "participant-a", participantOne.Id, "participant-b", participantTwo.Id);
        });
        return seed;
    }

    // =====================================================================
    // Connection + assertion helpers (a focused subset of RealtimeHubDeliveryTests, scoped to this suite).
    // =====================================================================

    private static async Task<HubProbe> ConnectParticipantAsync(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid participantId)
    {
        var connection = BuildConnection(factory, subject, sessionId, participantId);
        var probe = new HubProbe(connection);

        using var startCancellation = new CancellationTokenSource(_timeout);
        await connection.StartAsync(startCancellation.Token);
        Assert.Equal(HubConnectionState.Connected, connection.State);
        return probe;
    }

    private static HubConnection BuildConnection(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid participantId)
    {
        var query = $"organizationSlug={_orgA}&sessionId={sessionId}&participantId={participantId}";
        var url = new Uri(factory.Server.BaseAddress, $"hubs/session?{query}");
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthenticationHandler.SubjectHeader, subject);
                options.Headers.Add(TestAuthenticationHandler.IssuerHeader, _issuer);
                options.Headers.Add(TestAuthenticationHandler.OrganizationHeader, _orgA);
            })
            .Build();
    }

    /// <summary>
    /// Waits until the given instance has admitted <paramref name="expected"/> connections (recorded in ITS
    /// registry), so the reveal is published only after the connections it must reach across the backplane have
    /// actually joined their groups - the deterministic barrier registration is the last step of a successful
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

    private static async Task<string> AwaitContentRevealedAsync(HubProbe probe)
    {
        var completed = await Task.WhenAny(probe.ContentRevealed, Task.Delay(_timeout));
        Assert.True(completed == probe.ContentRevealed, "Expected a ContentRevealed event but none was received in time.");
        return await probe.ContentRevealed;
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        Guid resourceId,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static string? EventTypeOf(string rawEnvelope)
    {
        using var document = JsonDocument.Parse(rawEnvelope);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string NewChannelPrefix(string role) => $"livecore-it-{role}-{Guid.NewGuid():N}";

    private readonly record struct Seed(Guid SessionId, string ParticipantSubject, Guid ParticipantId);

    private readonly record struct TwoParticipantSeed(
        Guid SessionId,
        string ParticipantOneSubject,
        Guid ParticipantOneId,
        string ParticipantTwoSubject,
        Guid ParticipantTwoId);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that models ONE API instance of a multi-instance deployment. It
    /// points the production persistence at a SHARED PostgreSQL database (so every instance reads/writes the
    /// same system of record) and configures the production Redis/Valkey SignalR backplane (CORE-OPS-007) with
    /// the given channel prefix. Two such instances sharing a database and a backplane are exactly the
    /// production scale-out topology; the realtime wiring (<c>AddLiveCoreRealtime</c>) is the unchanged
    /// production registration - only the shared database and the test auth scheme the base already swaps differ.
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

        // Point the production Npgsql registration at the SHARED database instead of provisioning a fresh
        // per-instance one, so every instance sees the same seeded data. A distinct application name gives each
        // in-process instance its own connection pool (pools are keyed by the connection string), so several
        // instances sharing one database never starve a single shared pool. The shared database's lifetime is
        // owned by the test (created and dropped there); the base's per-factory drop on dispose is an idempotent
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
            // configuration, so with a connection string present it selects the Redis/Valkey-backed SignalR
            // HubLifetimeManager (the unchanged production conditional) and the channel prefix namespaces this
            // instance's pub/sub channels.
            builder.UseSetting("Realtime:Backplane:ConnectionString", RedisBackplaneTestServer.ConnectionString);
            builder.UseSetting("Realtime:Backplane:ChannelPrefix", _channelPrefix);

            base.ConfigureWebHost(builder);
        }
    }

    /// <summary>
    /// Wraps a live <see cref="HubConnection"/> and records every <c>SessionEvent</c> the server delivers to it
    /// (the recipient-safe envelope, as raw JSON), plus a signal for the first <c>ContentRevealed</c>.
    /// </summary>
    private sealed class HubProbe : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _received = [];
        private readonly TaskCompletionSource<string> _contentRevealed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HubProbe(HubConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            Connection = connection;

            connection.On<JsonElement>(SessionEventEnvelope.ClientMethod, envelope =>
            {
                var raw = envelope.GetRawText();
                lock (_gate)
                {
                    _received.Add(raw);
                }

                if (EventTypeOf(raw) == SessionEventTypes.ContentRevealed)
                {
                    _contentRevealed.TrySetResult(raw);
                }
            });
        }

        public HubConnection Connection { get; }

        /// <summary>Completes with the raw JSON of the first delivered <c>ContentRevealed</c> envelope.</summary>
        public Task<string> ContentRevealed => _contentRevealed.Task;

        /// <summary>A thread-safe snapshot of every delivered envelope (raw JSON), in arrival order.</summary>
        public IReadOnlyList<string> Received
        {
            get
            {
                lock (_gate)
                {
                    return _received.ToArray();
                }
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
