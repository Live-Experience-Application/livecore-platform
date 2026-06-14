using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// END-TO-END realtime DELIVERY and DENIAL tests over a REAL SignalR <see cref="HubConnection"/>
/// (CORE-TST-002). They open a live hub connection with <see cref="HubConnectionBuilder"/> against the
/// in-memory test host (<see cref="WorkspaceApiFactory"/>: test auth + EF Core SQLite, foreign keys ON),
/// so the connection runs the production <c>SessionHub.OnConnectedAsync</c> — admitted (or aborted
/// fail-closed) by the real <see cref="RealtimeConnectionResolver"/>, placed into its server-managed
/// groups, recorded in the real <see cref="RealtimeConnectionRegistry"/> — and the published event travels
/// the ACTUAL transport (the in-process <see cref="IRealtimeBackplane"/> over <c>IHubContext</c> and the
/// SignalR wire), not a substituted recording backplane.
///
/// This closes the gap the existing suites leave: <c>RealtimeHubAuthenticationTests</c> covers only the
/// <c>/negotiate</c> authentication gate, and <c>RealtimeDeliveryEndpointTests</c> substitutes a recording
/// <see cref="IRealtimeBackplane"/> and asserts group NAMES — neither ever opens a real connection that
/// RECEIVES (or is DENIED) a payload. Here the receipt is observed directly: an authorized participant's
/// registered <c>SessionEvent</c> handler fires with the reveal envelope, and a denied connection never
/// stays open and never receives anything.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>DELIVERY: an authorized participant RECEIVES a reveal payload over the live socket (the
///   product's core live mechanism, end-to-end).</item>
///   <item>DENIAL (group authz): a participant connecting with ANOTHER participant's id is aborted at
///   connect — the <see cref="RealtimeConnectionResolver"/> own-participant gate, exercised over real
///   HTTP.</item>
///   <item>SESSION SCOPE (CORE-SVIS-001): a participant connected to a CONCURRENT session of the same
///   workspace receives NOTHING from a reveal made in the other session, while a participant connected to
///   the revealing session does — the same reveal, delivered to one and not the other, so the difference is
///   provably the session boundary (threats T3/T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeHubDeliveryTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task An_authorized_participant_receives_a_revealed_payload_over_a_real_hub_connection()
    {
        await using var factory = new WorkspaceApiFactory();
        var resourceId = Guid.CreateVersion7();

        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            // A host who may reveal (Host role in org AND workspace).
            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            // A participant: an org member who owns an ACTIVE participant in the workspace, so the resolver
            // admits a participant connection and the audience fan-out delivers to it.
            var participantUser = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, "Stage Left");

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new Seed(org.Id, workspace.Id, session.Id, "participant-a", participant.Id);
        });

        // The participant opens a real connection and joins ONLY its own server-managed group.
        await using var participantProbe = await ConnectProbeAsync(factory, seed.ParticipantSubject, seed.SessionId, seed.ParticipantId);
        await WaitForRegisteredAsync(factory, expected: 1);

        // A host performs an audience-wide reveal over real HTTP; the durable event is delivered to the
        // live audience through the real backplane and the SignalR transport.
        using (var host = factory.CreateClientFor("host-a", _issuer, _orgA))
        {
            var response = await PostRevealAsync(host, seed.SessionId, resourceId, "reveal-1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The participant RECEIVES the reveal on its open socket — proving end-to-end delivery, not a
        // captured group name. The envelope carries the revealing session and the resource identifier.
        var revealed = await AwaitContentRevealedAsync(participantProbe);
        Assert.Contains(seed.SessionId.ToString(), revealed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(resourceId.ToString(), revealed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionEventTypes.ContentRevealed, EventTypeOf(revealed));
    }

    [Fact]
    public async Task A_participant_connecting_with_another_participants_id_is_aborted_at_connect()
    {
        await using var factory = new WorkspaceApiFactory();

        Guid foreignParticipantId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");

            // The caller: an org member who owns their OWN active participant.
            var callerUser = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, callerUser.Id, MembershipRole.Participant);
            await db.AddParticipantAsync(org.Id, workspace.Id, callerUser.Id, "Stage Left");

            // A DIFFERENT participant, owned by another user — the id the caller will try to impersonate.
            var otherUser = await db.AddUserAsync(_issuer, "participant-b");
            await db.AddOrganizationMemberAsync(org.Id, otherUser.Id, MembershipRole.Participant);
            var otherParticipant = await db.AddParticipantAsync(org.Id, workspace.Id, otherUser.Id, "Stage Right");

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            foreignParticipantId = otherParticipant.Id;
            sessionId = session.Id;
        });

        // The caller authenticates as themselves but supplies ANOTHER participant's id. The resolver's
        // own-participant gate denies it (it does not belong to the caller), so the hub aborts the
        // connection fail-closed — it never stays open and never receives an event.
        await AssertDeniedAtConnectAsync(factory, subject: "participant-a", sessionId, foreignParticipantId);

        // No admitted connection was ever recorded (a denied connect is never registered).
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task A_cross_session_participant_receives_no_reveal_from_another_session()
    {
        await using var factory = new WorkspaceApiFactory();
        var resourceInSessionA = Guid.CreateVersion7();
        var resourceInSessionB = Guid.CreateVersion7();

        CrossSessionSeed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            var hostUser = await db.AddUserAsync(_issuer, "host-a");
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);

            // ONE workspace running TWO concurrent live sessions.
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            var sessionA = await db.AddSessionAsync(org.Id, workspace.Id, "A", SessionStatus.Live);
            var sessionB = await db.AddSessionAsync(org.Id, workspace.Id, "B", SessionStatus.Live);

            // Two participants in the workspace; each owns its own active participant record.
            var userInA = await db.AddUserAsync(_issuer, "participant-a");
            await db.AddOrganizationMemberAsync(org.Id, userInA.Id, MembershipRole.Participant);
            var participantInA = await db.AddParticipantAsync(org.Id, workspace.Id, userInA.Id, "Stage Left");

            var userInB = await db.AddUserAsync(_issuer, "participant-b");
            await db.AddOrganizationMemberAsync(org.Id, userInB.Id, MembershipRole.Participant);
            var participantInB = await db.AddParticipantAsync(org.Id, workspace.Id, userInB.Id, "Stage Right");

            seed = new CrossSessionSeed(
                sessionA.Id,
                sessionB.Id,
                "participant-a",
                participantInA.Id,
                "participant-b",
                participantInB.Id);
        });

        // One participant connects to session A, the other to the CONCURRENT session B.
        await using var probeInA = await ConnectProbeAsync(factory, seed.ParticipantInASubject, seed.SessionAId, seed.ParticipantInAId);
        await using var probeInB = await ConnectProbeAsync(factory, seed.ParticipantInBSubject, seed.SessionBId, seed.ParticipantInBId);
        await WaitForRegisteredAsync(factory, expected: 2);

        using var host = factory.CreateClientFor("host-a", _issuer, _orgA);

        // A reveal in session A is received by the participant connected to session A...
        Assert.Equal(HttpStatusCode.OK, (await PostRevealAsync(host, seed.SessionAId, resourceInSessionA, "reveal-a")).StatusCode);
        var revealedInA = await AwaitContentRevealedAsync(probeInA);
        Assert.Contains(resourceInSessionA.ToString(), revealedInA, StringComparison.OrdinalIgnoreCase);

        // ...and a reveal in session B is received by the participant connected to session B.
        Assert.Equal(HttpStatusCode.OK, (await PostRevealAsync(host, seed.SessionBId, resourceInSessionB, "reveal-b")).StatusCode);
        var revealedInB = await AwaitContentRevealedAsync(probeInB);
        Assert.Contains(resourceInSessionB.ToString(), revealedInB, StringComparison.OrdinalIgnoreCase);

        // THE CROWN JEWEL (CORE-SVIS-001): neither participant ever received the OTHER session's reveal.
        // Each positive receipt above is the deterministic barrier — once a participant has received its own
        // session's reveal over the wire, the other session's reveal (published first / processed in order)
        // would already have arrived if the session boundary leaked. It did not.
        Assert.DoesNotContain(
            probeInB.Received,
            envelope => envelope.Contains(resourceInSessionA.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            probeInA.Received,
            envelope => envelope.Contains(resourceInSessionB.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================
    // Connection + assertion helpers.
    // =====================================================================

    /// <summary>
    /// Opens a real hub connection to <c>/hubs/session</c> as the given caller over the in-memory test
    /// server (long polling, header authentication via <see cref="TestAuthenticationHandler"/>) and wraps it
    /// in a <see cref="HubProbe"/> that records every delivered <c>SessionEvent</c>. The probe's handlers are
    /// registered BEFORE the connection starts so no early event is missed. A non-null
    /// <paramref name="participantId"/> opens a participant connection.
    /// </summary>
    private static async Task<HubProbe> ConnectProbeAsync(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid? participantId)
    {
        var connection = BuildConnection(factory, subject, sessionId, participantId);
        var probe = new HubProbe(connection);

        using var startCancellation = new CancellationTokenSource(_timeout);
        await connection.StartAsync(startCancellation.Token);
        Assert.Equal(HubConnectionState.Connected, connection.State);
        return probe;
    }

    /// <summary>
    /// Asserts the connection is DENIED at connect: the hub aborts it fail-closed (the resolver returned no
    /// groups). Robust to either observable outcome — the start itself fails, or the handshake completes and
    /// the abort then tears the socket down (raising <c>Closed</c> and leaving the Connected state) — and in
    /// both cases no event is ever received.
    /// </summary>
    private static async Task AssertDeniedAtConnectAsync(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid? participantId)
    {
        var connection = BuildConnection(factory, subject, sessionId, participantId);
        await using var probe = new HubProbe(connection);

        try
        {
            using var startCancellation = new CancellationTokenSource(_timeout);
            await connection.StartAsync(startCancellation.Token);
        }
        catch (Exception)
        {
            // The connect failed outright — the hub aborted before/while completing the handshake. Denied.
            return;
        }

        // The handshake can complete just before the hub aborts in OnConnectedAsync; the abort then closes
        // the socket. Either way the connection does not survive and never receives an event.
        var completed = await Task.WhenAny(probe.Closed, Task.Delay(_timeout));
        Assert.True(completed == probe.Closed, "The foreign-participant connection was not denied; its socket stayed open.");
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
        Assert.Empty(probe.Received);
    }

    private static HubConnection BuildConnection(
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
    /// Waits until the hub has admitted <paramref name="expected"/> connections (recorded in the registry),
    /// so a reveal is published only after the connections it must reach have actually joined their groups —
    /// the deterministic barrier that makes the over-the-wire test reliable (registration is the last step of
    /// a successful <c>OnConnectedAsync</c>, after the group joins).
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

    /// <summary>Awaits the probe's first <c>ContentRevealed</c> envelope (raw JSON), failing if none arrives in time.</summary>
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

    /// <summary>Reads the envelope's <c>eventType</c> case-insensitively, independent of the transport's JSON casing.</summary>
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

    private readonly record struct Seed(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        string ParticipantSubject,
        Guid ParticipantId);

    private readonly record struct CrossSessionSeed(
        Guid SessionAId,
        Guid SessionBId,
        string ParticipantInASubject,
        Guid ParticipantInAId,
        string ParticipantInBSubject,
        Guid ParticipantInBId);

    /// <summary>
    /// Wraps a live <see cref="HubConnection"/> and records every <c>SessionEvent</c> the server delivers to
    /// it (the recipient-safe envelope, as raw JSON), plus a signal for the first <c>ContentRevealed</c>. The
    /// <c>Closed</c> event is captured at construction so a server-initiated abort is never missed.
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

            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            Closed = closed.Task;

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

        /// <summary>Completes when the connection closes (e.g. a server-initiated eviction/abort).</summary>
        public Task Closed { get; }

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
