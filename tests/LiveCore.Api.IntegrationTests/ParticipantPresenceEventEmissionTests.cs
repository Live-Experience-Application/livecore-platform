// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the participant presence events of CORE-EVT-002 — the story's required "Integration:
/// join/leave persist one event each, host-visible, no PII leak (T7)". They boot the real application
/// (<see cref="WorkspaceApiFactory"/>: real persistence over EF Core SQLite with foreign keys ON, the real
/// session-event publisher and the real per-recipient recipient resolver) and substitute a recording
/// <see cref="IRealtimeBackplane"/> for the in-process default, so the test observes end-to-end exactly which
/// SERVER-MANAGED groups each presence event was delivered to AND the recipient-safe payload each carried —
/// without a live SignalR connection. The backplane is the single transport boundary, so capturing it captures
/// every send.
///
/// The join and leave flows are application services
/// (<see cref="SessionParticipantJoinService"/> / <see cref="SessionParticipantLeaveService"/>) whose HTTP
/// endpoints are later stories, so the test drives them through the real DI container (the same scoped
/// services a future endpoint will resolve), exercising the real publish path. The group names are re-derived
/// independently from the documented connection model (docs/11_REALTIME_SYNC.md), so a regression in the
/// production naming cannot hide behind a matching test constant.
///
/// Coverage:
/// <list type="bullet">
///   <item>JOIN persists exactly ONE ParticipantJoined event and delivers it to the hosts group
///   (host-visible), carrying the participant IDENTIFIER only — never the participant's display name (no PII
///   leak, threat T7).</item>
///   <item>LEAVE persists exactly ONE ParticipantLeft event and delivers it to the hosts group (host-visible),
///   again identifier-only, and the just-removed participant is excluded from the audience fan-out.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class ParticipantPresenceEventEmissionTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    // A distinctive display name (participant PII) that must NEVER appear in any persisted event payload or any
    // delivered envelope (threat T7).
    private const string _participantDisplayName = "Backstage Pass Holder";

    [Fact]
    public async Task Join_persists_one_host_visible_ParticipantJoined_event_without_leaking_PII()
    {
        await using var factory = new RecordingDeliveryApiFactory();
        var seed = await SeedSessionWithParticipantsAsync(factory);

        SessionJoinResult result;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var joinService = scope.ServiceProvider.GetRequiredService<SessionParticipantJoinService>();
            result = await joinService.JoinAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, seed.ParticipantId, CancellationToken.None);
        }

        Assert.True(result.Admitted);

        // Persist ONE event: exactly one ParticipantJoined in the session's append-only stream, carrying the
        // participant id and NOT the display name (threat T7).
        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        var joined = Assert.Single(events);
        Assert.Equal(SessionEventTypes.ParticipantJoined, joined.EventType);
        AssertPayloadIsParticipantIdentifierOnly(joined.Payload, seed.ParticipantId);

        // Host-visible: the event was delivered to the session's hosts group.
        var deliveries = factory.Backplane.Snapshot();
        AssertDeliveredToHosts(deliveries, seed.SessionId, SessionEventTypes.ParticipantJoined);

        // No PII leak: not one delivered envelope carries the participant's display name.
        AssertNoDeliveryLeaksDisplayName(deliveries);
    }

    [Fact]
    public async Task Leave_persists_one_host_visible_ParticipantLeft_event_without_leaking_PII()
    {
        await using var factory = new RecordingDeliveryApiFactory();
        var seed = await SeedSessionWithParticipantsAsync(factory);

        // Arrange a present participant (join first), then clear the recorded deliveries so the assertions below
        // observe only the LEAVE.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var joinService = scope.ServiceProvider.GetRequiredService<SessionParticipantJoinService>();
            var joined = await joinService.JoinAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, seed.ParticipantId, CancellationToken.None);
            Assert.True(joined.Admitted);
        }

        factory.Backplane.Reset();

        SessionParticipantLeaveResult result;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var leaveService = scope.ServiceProvider.GetRequiredService<SessionParticipantLeaveService>();
            result = await leaveService.LeaveAsync(
                seed.OrganizationId, seed.WorkspaceId, seed.SessionId, seed.ParticipantId, CancellationToken.None);
        }

        Assert.Equal(ParticipantLeaveOutcome.Left, result.Outcome);

        // Persist ONE event each: the stream now holds the earlier ParticipantJoined plus exactly one
        // ParticipantLeft — one event per flow.
        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.EventType == SessionEventTypes.ParticipantJoined);
        var left = Assert.Single(events, e => e.EventType == SessionEventTypes.ParticipantLeft);
        AssertPayloadIsParticipantIdentifierOnly(left.Payload, seed.ParticipantId);

        // Host-visible: the leave was delivered to the session's hosts group.
        var deliveries = factory.Backplane.Snapshot();
        AssertDeliveredToHosts(deliveries, seed.SessionId, SessionEventTypes.ParticipantLeft);

        // The just-removed participant is no longer in the audience, so their own participant group received
        // nothing for the leave (the optional participant feed).
        Assert.DoesNotContain(deliveries, d => d.Group == ParticipantGroup(seed.SessionId, seed.ParticipantId));

        // No PII leak.
        AssertNoDeliveryLeaksDisplayName(deliveries);
    }

    // =====================================================================
    // Group-name contract, re-derived independently from docs/11_REALTIME_SYNC.md.
    // =====================================================================

    private static string HostsGroup(Guid sessionId) => $"session:{sessionId}:hosts";

    private static string ParticipantGroup(Guid sessionId, Guid participantId)
        => $"session:{sessionId}:participant:{participantId}";

    // =====================================================================
    // Assertions.
    // =====================================================================

    private static void AssertDeliveredToHosts(
        IReadOnlyList<RecordedDelivery> deliveries,
        Guid sessionId,
        string eventType)
        => Assert.Contains(
            deliveries,
            d => d.Group == HostsGroup(sessionId) && d.PayloadJson.Contains(eventType, StringComparison.Ordinal));

    private static void AssertNoDeliveryLeaksDisplayName(IReadOnlyList<RecordedDelivery> deliveries)
        => Assert.All(
            deliveries,
            d => Assert.False(
                d.PayloadJson.Contains(_participantDisplayName, StringComparison.OrdinalIgnoreCase),
                "A delivered envelope leaked the participant display name (PII)."));

    /// <summary>The payload is the participant id and nothing else — no display name or any other PII (threat T7).</summary>
    private static void AssertPayloadIsParticipantIdentifierOnly(string payload, Guid participantId)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(participantId, document.RootElement.GetProperty("ParticipantId").GetGuid());
        Assert.False(
            payload.Contains(_participantDisplayName, StringComparison.OrdinalIgnoreCase),
            "A persisted event payload leaked the participant display name (PII).");
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<IReadOnlyList<SessionEvent>> ReadSessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<ISessionEventRepository>();
        return await events.ListBySessionAsync(organizationId, sessionId, CancellationToken.None);
    }

    /// <summary>
    /// Seeds (in org A) a workspace, a Live session and one ACTIVE participant carrying a distinctive display
    /// name (the PII under test). No role membership is needed: the join/leave services are object-level flows,
    /// and the hosts group is addressed by the recipient resolver regardless of who is connected.
    /// </summary>
    private static async Task<Seed> SeedSessionWithParticipantsAsync(WorkspaceApiFactory factory)
    {
        Seed seed = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participant = await db.AddParticipantAsync(
                org.Id, workspace.Id, userProfileId: null, displayName: _participantDisplayName);
            seed = new Seed(org.Id, workspace.Id, session.Id, participant.Id);
        });
        return seed;
    }

    private readonly record struct Seed(Guid OrganizationId, Guid WorkspaceId, Guid SessionId, Guid ParticipantId);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a recording <see cref="IRealtimeBackplane"/> for
    /// the production in-process one, so the test can read which server-managed groups each event was delivered
    /// to and the recipient-safe payload each carried. Only the realtime transport seam is swapped; every other
    /// production behavior (the publisher, the recipient resolver, the Visibility engine) runs unchanged.
    /// </summary>
    private sealed class RecordingDeliveryApiFactory : WorkspaceApiFactory
    {
        public RecordingRealtimeBackplane Backplane { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRealtimeBackplane>();
                services.AddSingleton<IRealtimeBackplane>(Backplane);
            });
        }
    }

    private sealed class RecordingRealtimeBackplane : IRealtimeBackplane
    {
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
        private readonly object _gate = new();
        private readonly List<RecordedDelivery> _deliveries = [];

        /// <summary>A thread-safe snapshot of every delivery (group + the serialized recipient-safe envelope).</summary>
        public IReadOnlyList<RecordedDelivery> Snapshot()
        {
            lock (_gate)
            {
                return _deliveries.ToArray();
            }
        }

        /// <summary>Clears the recorded deliveries so a later phase observes only its own sends.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _deliveries.Clear();
            }
        }

        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
        {
            // Serialize the recipient-safe envelope so the test can inspect exactly what a recipient would
            // receive (including the embedded payload), without needing access to the internal envelope type.
            var payloadJson = JsonSerializer.Serialize(payload, _json);
            lock (_gate)
            {
                _deliveries.Add(new RecordedDelivery(group, method, payloadJson));
            }

            return Task.CompletedTask;
        }
    }

    private readonly record struct RecordedDelivery(string Group, string Method, string PayloadJson);
}
