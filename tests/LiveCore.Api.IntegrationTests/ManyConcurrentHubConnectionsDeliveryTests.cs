// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
/// END-TO-END realtime fan-out at a REALISTIC CONNECTION COUNT over MANY concurrent REAL SignalR
/// <see cref="HubConnection"/>s in ONE live session (CORE-E2E-005, the "End-to-End Scenarios" epic). It
/// extends the single-connection delivery proof (CORE-TST-002 / <see cref="RealtimeHubDeliveryTests"/>) from
/// one or two connections to a small audience of concurrent live sockets, using the SAME harness approach: a
/// <see cref="HubConnectionBuilder"/> opening real connections to <c>/hubs/session</c> against the in-memory
/// test host (<see cref="WorkspaceApiFactory"/>: test auth + EF Core SQLite, foreign keys ON), so every
/// connection runs the production <c>SessionHub.OnConnectedAsync</c> — admitted (or aborted fail-closed) by the
/// real <see cref="RealtimeConnectionResolver"/>, placed into its server-managed groups, recorded in the real
/// <see cref="RealtimeConnectionRegistry"/> — and each published reveal travels the ACTUAL realtime TRANSPORT
/// (the in-process <see cref="IRealtimeBackplane"/> over <c>IHubContext</c> and the SignalR wire), not a
/// substituted recording backplane.
///
/// The phase-3 real-hub test proves delivery + denial for ONE connection; this proves the actual GROUP FAN-OUT
/// delivers correctly to MANY concurrent connections of one session. It is correctness at live-transport scale,
/// asserted deterministically — each connection's receipt (or non-receipt) is awaited with a bounded poll, and
/// every negative is pinned by a POSITIVE BARRIER on the SAME connection (a connection that has received a later
/// event would already have received an earlier one, delivered first over its FIFO socket, had it leaked) — NOT
/// a stress/throughput test, and scoped to a realistic-but-bounded audience (<see cref="_audienceSize"/>). It
/// pairs with CORE-E2E-004, which covers the read-surface (feed/replay catch-up) half of the same picture.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>AUDIENCE FAN-OUT: an audience-wide reveal is received by EVERY one of the N authorized connections
///   in the session (the live group fan-out at audience scale).</item>
///   <item>PRIVATE ROUTING: a selected-participant reveal is received by ONLY its target's connection — every
///   other connection in the session receives the later audience barrier but never the private resource (THE
///   crown jewel over the wire, at scale; threat T3).</item>
///   <item>CROSS-SESSION DENIAL (CORE-SVIS-001): a connection in a CONCURRENT session of the same workspace
///   receives NOTHING from the reveals made in the other session, while still receiving its own session's
///   reveal — so the difference is provably the session boundary (threats T3/T5).</item>
///   <item>REMOVAL: a participant removed mid-session is evicted from its open socket and receives NOTHING
///   FURTHER, while every remaining connection keeps receiving (CORE-RTC-002; threat T3).</item>
///   <item>LATE JOIN: a connection that joins after earlier reveals is folded into the LIVE fan-out and
///   receives every subsequent audience reveal — its transport "catch-up" from the moment it joins (the prior
///   backlog is the reconnect-replay surface CORE-E2E-004 covers).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class ManyConcurrentHubConnectionsDeliveryTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _hostSubject = "host-a";
    private const string _crossSubject = "cross-participant";

    // A realistic-but-bounded small audience of concurrent live sockets — several times the existing 1-2, and
    // deliberately NOT a stress/throughput count (the test asserts exact correctness, not capacity). The
    // assertions are exact regardless of the size, so this is the single knob.
    private const int _audienceSize = 4;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // Required test: an audience-wide reveal reaches EVERY authorized connection, and a cross-session
    // connection receives nothing.
    // =====================================================================

    [Fact]
    public async Task An_audience_reveal_reaches_every_authorized_connection_and_a_cross_session_connection_receives_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        var audience = await SeedAudienceAsync(factory, withCrossSession: true);

        var audienceResource = Guid.CreateVersion7();
        var sessionBResource = Guid.CreateVersion7();

        // Every participant opens its OWN real connection to session A; a separate participant opens a
        // connection to the CONCURRENT session B of the same workspace.
        await using var probes = await ConnectAudienceAsync(factory, audience);
        await using var crossProbe = await ConnectProbeAsync(
            factory, _crossSubject, audience.SessionBId, audience.CrossParticipantId);
        await WaitForRegisteredAsync(factory, expected: _audienceSize + 1);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);

        // A host performs ONE audience-wide reveal in session A over real HTTP; the durable event fans out over
        // the real backplane and the SignalR transport.
        await PostRevealAsync(host, audience.SessionAId, audienceResource, targetParticipantId: null, "reveal-audience");

        // EVERY one of the N session-A connections receives it on its own open socket — the group fan-out
        // delivered to the whole audience, not just one connection.
        for (var index = 0; index < _audienceSize; index++)
        {
            await AwaitRevealAsync(probes[index], audienceResource);
        }

        // BARRIER for the cross-session connection: a reveal made in session B (published AFTER the session-A
        // reveal) that the cross-session connection DOES receive. Once it has arrived, the earlier session-A
        // reveal — delivered first over the same FIFO socket, had it leaked across the session boundary — would
        // already be present.
        await PostRevealAsync(host, audience.SessionBId, sessionBResource, targetParticipantId: null, "reveal-session-b");
        await AwaitRevealAsync(crossProbe, sessionBResource);

        // CORE-SVIS-001: the cross-session connection never received session A's reveal.
        Assert.False(
            crossProbe.HasReceivedRevealOf(audienceResource),
            "A connection in a concurrent session received a reveal made in the other session.");
    }

    // =====================================================================
    // Required test: a selected-participant (private) reveal reaches ONLY its target's connection.
    // =====================================================================

    [Fact]
    public async Task A_private_reveal_reaches_only_its_target_connection_and_no_one_else()
    {
        await using var factory = new WorkspaceApiFactory();
        var audience = await SeedAudienceAsync(factory, withCrossSession: true);

        const int targetIndex = 3;
        var privateResource = Guid.CreateVersion7();
        var audienceBarrier = Guid.CreateVersion7();
        var sessionBResource = Guid.CreateVersion7();

        await using var probes = await ConnectAudienceAsync(factory, audience);
        await using var crossProbe = await ConnectProbeAsync(
            factory, _crossSubject, audience.SessionBId, audience.CrossParticipantId);
        await WaitForRegisteredAsync(factory, expected: _audienceSize + 1);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);

        // A private reveal targeting exactly one participant of the session.
        await PostRevealAsync(
            host, audience.SessionAId, privateResource, audience.ParticipantIds[targetIndex], "reveal-private");

        // The target's connection receives it.
        await AwaitRevealAsync(probes[targetIndex], privateResource);

        // BARRIER for every OTHER session-A connection: a later audience-wide reveal that all of them receive.
        // Because it is published AFTER the private reveal, a non-target connection that received this barrier
        // would already have received the private reveal first — over the same FIFO socket — had it leaked.
        await PostRevealAsync(host, audience.SessionAId, audienceBarrier, targetParticipantId: null, "reveal-barrier");

        // BARRIER for the cross-session connection: a reveal in session B it does receive.
        await PostRevealAsync(host, audience.SessionBId, sessionBResource, targetParticipantId: null, "reveal-session-b");
        await AwaitRevealAsync(crossProbe, sessionBResource);

        for (var index = 0; index < _audienceSize; index++)
        {
            // Every connection — target included — receives the audience barrier (it is audience-wide).
            await AwaitRevealAsync(probes[index], audienceBarrier);

            if (index == targetIndex)
            {
                continue;
            }

            // ...but ONLY the target ever received the private reveal.
            Assert.False(
                probes[index].HasReceivedRevealOf(privateResource),
                $"A non-target connection (index {index}) received another participant's private reveal.");
        }

        // The cross-session connection received neither the private reveal nor the in-session audience barrier.
        Assert.False(
            crossProbe.HasReceivedRevealOf(privateResource),
            "A cross-session connection received a private reveal made in the other session.");
        Assert.False(
            crossProbe.HasReceivedRevealOf(audienceBarrier),
            "A cross-session connection received an audience reveal made in the other session.");
    }

    // =====================================================================
    // Required test: a removed participant's socket receives nothing further.
    // =====================================================================

    [Fact]
    public async Task A_removed_participants_socket_is_evicted_and_receives_nothing_further()
    {
        await using var factory = new WorkspaceApiFactory();
        var audience = await SeedAudienceAsync(factory, withCrossSession: false);

        const int removedIndex = 0;
        var firstResource = Guid.CreateVersion7();
        var afterRemovalResource = Guid.CreateVersion7();

        await using var probes = await ConnectAudienceAsync(factory, audience);
        await WaitForRegisteredAsync(factory, expected: _audienceSize);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);

        // Before removal, the soon-to-be-removed connection is a normal audience member: it receives the first
        // audience reveal along with everyone else.
        await PostRevealAsync(host, audience.SessionAId, firstResource, targetParticipantId: null, "reveal-first");
        for (var index = 0; index < _audienceSize; index++)
        {
            await AwaitRevealAsync(probes[index], firstResource);
        }

        // The participant is removed mid-session through the real leave/remove flow (CORE-RTC-002), which
        // re-authorizes the realtime layer and evicts the participant's open socket.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var leaveService = scope.ServiceProvider.GetRequiredService<SessionParticipantLeaveService>();
            var result = await leaveService.LeaveAsync(
                audience.OrganizationId,
                audience.WorkspaceId,
                audience.SessionAId,
                audience.ParticipantIds[removedIndex],
                CancellationToken.None);
            Assert.Equal(ParticipantLeaveOutcome.Left, result.Outcome);
        }

        // The removed connection's open socket is torn down — observed directly as its Closed event firing.
        await AwaitClosedAsync(probes[removedIndex]);

        // A subsequent audience reveal is received by every REMAINING connection (the barrier proving the
        // fan-out actually ran) but NOT by the removed one — its socket is gone, so it receives nothing further.
        await PostRevealAsync(host, audience.SessionAId, afterRemovalResource, targetParticipantId: null, "reveal-after-removal");
        for (var index = 0; index < _audienceSize; index++)
        {
            if (index == removedIndex)
            {
                continue;
            }

            await AwaitRevealAsync(probes[index], afterRemovalResource);
        }

        Assert.NotEqual(HubConnectionState.Connected, probes[removedIndex].Connection.State);
        Assert.False(
            probes[removedIndex].HasReceivedRevealOf(afterRemovalResource),
            "A removed participant's socket received an event published after its removal.");
    }

    // =====================================================================
    // Acceptance criterion: a connection that joins late is folded into the live fan-out (its transport
    // catch-up from the moment it joins; the backlog is the reconnect-replay half — CORE-E2E-004).
    // =====================================================================

    [Fact]
    public async Task A_connection_that_joins_late_is_folded_into_the_live_fan_out()
    {
        await using var factory = new WorkspaceApiFactory();
        var audience = await SeedAudienceAsync(factory, withCrossSession: false);

        const int anchorIndex = 0;
        const int lateIndex = 1;
        var beforeJoinResource = Guid.CreateVersion7();
        var afterJoinResource = Guid.CreateVersion7();

        // Only the anchor is present from the start.
        await using var anchor = await ConnectProbeAsync(
            factory, ParticipantSubject(anchorIndex), audience.SessionAId, audience.ParticipantIds[anchorIndex]);
        await WaitForRegisteredAsync(factory, expected: 1);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);

        // A reveal happens BEFORE the late connection exists; the anchor receives it.
        await PostRevealAsync(host, audience.SessionAId, beforeJoinResource, targetParticipantId: null, "reveal-before-join");
        await AwaitRevealAsync(anchor, beforeJoinResource);

        // The late connection joins now, after that reveal.
        await using var late = await ConnectProbeAsync(
            factory, ParticipantSubject(lateIndex), audience.SessionAId, audience.ParticipantIds[lateIndex]);
        await WaitForRegisteredAsync(factory, expected: 2);

        // A subsequent audience reveal reaches BOTH the anchor and the newly-joined late connection — the late
        // connection is now part of the live group fan-out.
        await PostRevealAsync(host, audience.SessionAId, afterJoinResource, targetParticipantId: null, "reveal-after-join");
        await AwaitRevealAsync(anchor, afterJoinResource);
        await AwaitRevealAsync(late, afterJoinResource);

        // The transport delivers live-forward only: the late socket never received the reveal that predated it
        // (it has now received the later one, so an earlier delivery — if any — would already be present). The
        // backlog catch-up is the reconnect-replay surface (CORE-E2E-004), the paired read-surface half.
        Assert.False(
            late.HasReceivedRevealOf(beforeJoinResource),
            "A late-joining socket received a reveal published before it connected (that is the replay surface's job).");
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    /// <summary>
    /// Seeds one live session A in a workspace with a host (workspace Host, so it may reveal) and an audience of
    /// <see cref="_audienceSize"/> participants, each an organization member owning one ACTIVE participant
    /// record in the workspace (so the resolver admits a participant connection and the audience fan-out
    /// delivers to it). When <paramref name="withCrossSession"/> is set, it also seeds a CONCURRENT live session
    /// B in the same workspace and one extra participant connected to it — the cross-session subject. All
    /// fixtures are generic Core vocabulary (AGENTS.md).
    /// </summary>
    private static async Task<Audience> SeedAudienceAsync(WorkspaceApiFactory factory, bool withCrossSession)
    {
        Audience audience = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            // A host who may reveal (Host role in org AND workspace).
            var hostUser = await db.AddUserAsync(_issuer, _hostSubject);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            var sessionA = await db.AddSessionAsync(org.Id, workspace.Id, "A", SessionStatus.Live);

            var participantIds = new Guid[_audienceSize];
            for (var index = 0; index < _audienceSize; index++)
            {
                var participantUser = await db.AddUserAsync(_issuer, ParticipantSubject(index));
                await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
                var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, $"Seat {index:00}");
                participantIds[index] = participant.Id;
            }

            var sessionBId = Guid.Empty;
            var crossParticipantId = Guid.Empty;
            if (withCrossSession)
            {
                var sessionB = await db.AddSessionAsync(org.Id, workspace.Id, "B", SessionStatus.Live);
                sessionBId = sessionB.Id;

                var crossUser = await db.AddUserAsync(_issuer, _crossSubject);
                await db.AddOrganizationMemberAsync(org.Id, crossUser.Id, MembershipRole.Participant);
                var crossParticipant = await db.AddParticipantAsync(org.Id, workspace.Id, crossUser.Id, "Cross Seat");
                crossParticipantId = crossParticipant.Id;
            }

            audience = new Audience(
                org.Id, workspace.Id, sessionA.Id, sessionBId, participantIds, crossParticipantId);
        });

        return audience;
    }

    // =====================================================================
    // Connection + assertion helpers (the RealtimeHubDeliveryTests harness approach, for many connections).
    // =====================================================================

    /// <summary>Opens one real connection per seeded session-A participant, each to its own server-managed group.</summary>
    private static async Task<ProbeSet> ConnectAudienceAsync(WorkspaceApiFactory factory, Audience audience)
    {
        var probes = new HubProbe[_audienceSize];
        for (var index = 0; index < _audienceSize; index++)
        {
            probes[index] = await ConnectProbeAsync(
                factory, ParticipantSubject(index), audience.SessionAId, audience.ParticipantIds[index]);
        }

        return new ProbeSet(probes);
    }

    /// <summary>
    /// Opens a real hub connection to <c>/hubs/session</c> as the given caller over the in-memory test server
    /// (long polling, header authentication via <see cref="TestAuthenticationHandler"/>) and wraps it in a
    /// <see cref="HubProbe"/> that records every delivered <c>SessionEvent</c>. The probe's handler is
    /// registered BEFORE the connection starts so no early event is missed. A non-null
    /// <paramref name="participantId"/> opens a participant connection.
    /// </summary>
    private static async Task<HubProbe> ConnectProbeAsync(
        WorkspaceApiFactory factory,
        string subject,
        Guid sessionId,
        Guid? participantId)
    {
        // Snapshot the registry before opening this connection. Connections are established sequentially (every
        // ConnectProbeAsync is awaited and confirmed below before the next), so exactly this connection raises
        // the registered count by one once its server-side OnConnectedAsync completes.
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        var registeredBefore = registry.Count;

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

        var probe = new HubProbe(connection);

        using var startCancellation = new CancellationTokenSource(_timeout);
        await connection.StartAsync(startCancellation.Token);
        Assert.Equal(HubConnectionState.Connected, connection.State);

        // Confirm THIS connection's server-side OnConnectedAsync has finished before returning — registration is
        // its last step, after the group joins. StartAsync returns once the transport handshake completes, which
        // is BEFORE OnConnectedAsync runs; opening several connections and only polling for a bulk count lets
        // their server-side handshakes run concurrently, and on a loaded 2-core CI runner one can be starved past
        // the wait (seen as "saw 4 of 5"). Establishing one at a time and confirming each keeps at most one
        // OnConnectedAsync in flight, which is what makes the over-the-wire fan-out deterministic (CORE-E2E-005).
        await WaitForRegisteredAsync(factory, registeredBefore + 1);
        return probe;
    }

    /// <summary>
    /// Waits until the hub has admitted <paramref name="expected"/> connections (recorded in the registry), so a
    /// reveal is published only after the connections it must reach have actually joined their groups — the
    /// deterministic barrier that makes the over-the-wire test reliable (registration is the last step of a
    /// successful <c>OnConnectedAsync</c>, after the group joins).
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

    /// <summary>Awaits a <c>ContentRevealed</c> referencing the given resource on the probe's socket, failing if none arrives in time.</summary>
    private static async Task AwaitRevealAsync(HubProbe probe, Guid resourceId)
    {
        var wait = probe.WaitForRevealOf(resourceId);
        var completed = await Task.WhenAny(wait, Task.Delay(_timeout));
        Assert.True(
            completed == wait,
            $"Expected a ContentRevealed for resource {resourceId} but none was received in time.");
        await wait;
    }

    /// <summary>Awaits the probe's connection closing (e.g. a server-initiated eviction abort), failing if it stays open.</summary>
    private static async Task AwaitClosedAsync(HubProbe probe)
    {
        var completed = await Task.WhenAny(probe.Closed, Task.Delay(_timeout));
        Assert.True(completed == probe.Closed, "The connection was not evicted (its socket stayed open).");
    }

    private static async Task PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        Guid resourceId,
        Guid? targetParticipantId,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId, participantId = targetParticipantId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string ParticipantSubject(int index) => $"participant-{index:00}";

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

    /// <summary>Whether a raw envelope is a <c>ContentRevealed</c> whose identifier-only payload references the given resource.</summary>
    private static bool IsRevealOf(string rawEnvelope, Guid resourceId)
        => EventTypeOf(rawEnvelope) == SessionEventTypes.ContentRevealed
            && rawEnvelope.Contains(resourceId.ToString(), StringComparison.OrdinalIgnoreCase);

    private readonly record struct Audience(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionAId,
        Guid SessionBId,
        IReadOnlyList<Guid> ParticipantIds,
        Guid CrossParticipantId);

    /// <summary>A disposable set of probes that disposes every wrapped connection together.</summary>
    private sealed class ProbeSet : IAsyncDisposable
    {
        private readonly HubProbe[] _probes;

        public ProbeSet(HubProbe[] probes) => _probes = probes;

        public HubProbe this[int index] => _probes[index];

        public async ValueTask DisposeAsync()
        {
            foreach (var probe in _probes)
            {
                await probe.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Wraps a live <see cref="HubConnection"/> and records every <c>SessionEvent</c> the server delivers to it
    /// (the recipient-safe envelope, as raw JSON). Exposes a per-resource awaiter (<see cref="WaitForRevealOf"/>)
    /// that completes when a <c>ContentRevealed</c> for a given resource arrives — checking the already-received
    /// envelopes first, so a reveal that arrived before the await is never missed — and a snapshot predicate
    /// (<see cref="HasReceivedRevealOf"/>) for the negative assertions. The <c>Closed</c> event is captured at
    /// construction so a server-initiated eviction abort is never missed.
    /// </summary>
    private sealed class HubProbe : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _received = [];
        private readonly List<(Guid ResourceId, TaskCompletionSource<string> Completion)> _waiters = [];

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
                List<TaskCompletionSource<string>> matched = [];
                lock (_gate)
                {
                    _received.Add(raw);
                    for (var i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (IsRevealOf(raw, _waiters[i].ResourceId))
                        {
                            matched.Add(_waiters[i].Completion);
                            _waiters.RemoveAt(i);
                        }
                    }
                }

                foreach (var completion in matched)
                {
                    completion.TrySetResult(raw);
                }
            });
        }

        public HubConnection Connection { get; }

        /// <summary>Completes when the connection closes (e.g. a server-initiated eviction/abort).</summary>
        public Task Closed { get; }

        /// <summary>
        /// A task that completes when a <c>ContentRevealed</c> referencing <paramref name="resourceId"/> has been
        /// delivered. If one already arrived it completes immediately, so registering the await never races a
        /// just-delivered event.
        /// </summary>
        public Task<string> WaitForRevealOf(Guid resourceId)
        {
            lock (_gate)
            {
                var existing = _received.FirstOrDefault(raw => IsRevealOf(raw, resourceId));
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }

                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((resourceId, completion));
                return completion.Task;
            }
        }

        /// <summary>Whether a <c>ContentRevealed</c> referencing the given resource has been delivered to this connection.</summary>
        public bool HasReceivedRevealOf(Guid resourceId)
        {
            lock (_gate)
            {
                return _received.Any(raw => IsRevealOf(raw, resourceId));
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
