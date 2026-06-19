// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Observability;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// The realtime hub LOAD/SOAK harness and CAPACITY BASELINE (CORE-PERF-009, the "Performance and Capacity
/// Baselines" epic). It is the runnable, repeatable test that closes the SRE gap behind the HA claims — there
/// was no measured scaling number for the realtime hub. The harness drives the SignalR realtime hub to a
/// target concurrency (N simultaneous connections + reveal/event fan-out per API instance) across the
/// backplane-backed MULTI-INSTANCE topology, MEASURES the capacity baseline from the existing realtime metrics
/// and ASSERTS the recorded baseline thresholds, so a regression below the baseline FAILS the run.
///
/// TOPOLOGY (the same one <see cref="RedisBackplanePropagationTests"/> uses for cross-instance correctness):
/// it boots N <see cref="LoadInstanceApiFactory"/> instances that share ONE PostgreSQL system of record and a
/// Redis/Valkey SignalR backplane (CORE-OPS-007) under one channel prefix — exactly the production scale-out
/// shape (CORE-DEP-009 backplane, CORE-DEP-013 affinity, CORE-RES-006 fail-fast, CORE-RES-008 eviction). The
/// audience connections are distributed across the instances, so an audience-wide reveal performed on one
/// instance must fan out across the backplane to the connections held by the others — load over the real
/// transport, not a substituted backplane.
///
/// MEASUREMENT SOURCE — the existing realtime metrics (docs/15_OBSERVABILITY.md), captured with a
/// <see cref="MeterListener"/> scoped to each instance's own <see cref="LiveCoreMetrics.Meter"/> reference (so
/// a concurrently-running host's measurements are never captured):
/// <list type="bullet">
///   <item>SUSTAINED CONNECTIONS — the <c>livecore.realtime.connections</c> up/down gauge, summed across
///   instances, must reach and HOLD the full target while the soak runs.</item>
///   <item>REVEAL LATENCY — the <c>livecore.reveal.duration</c> histogram samples, from which the p95/p99
///   percentiles are computed and checked against the baseline ceilings.</item>
///   <item>ERROR RATE — failed fan-out deliveries (a connected probe that did not receive a round's reveal
///   within the per-round deadline, a non-200 reveal counting as a whole-round miss) ÷ delivery attempts, plus
///   the <c>livecore.event.delivery.failures</c> transport-failure counter, reported alongside.</item>
/// </list>
///
/// ON-DEMAND / SCHEDULED, NOT A PER-PR BLOCKER. The harness runs only when <see cref="RealtimeHubLoadProfile"/>
/// is opted in (<c>LIVECORE_REALTIME_LOAD</c>) AND the shared PostgreSQL + Redis/Valkey backplane are
/// configured; a default <c>dotnet test</c> (and the per-PR <c>integration-postgres</c> job, which does not set
/// the opt-in flag) returns early, so the load/soak never slows a normal build. A dedicated scheduled workflow
/// (<c>.github/workflows/realtime-load-baseline.yml</c>) sets the flag and the service containers. The first
/// fact below always runs and pins the documented default baseline in code, so the file is never a pure no-op.
///
/// It adds NO shipped runtime dependency: the load is driven by the test-only
/// <c>Microsoft.AspNetCore.SignalR.Client</c> the eviction/delivery suites already use. All fixtures are
/// generic Core vocabulary (AGENTS.md), and the captured baseline carries only counts and timings (threat T7).
/// </summary>
public sealed class RealtimeHubLoadBaselineTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _hostSubject = "load-host";

    // The realtime-connections gauge and reveal-duration histogram instrument names (docs/15). The harness
    // captures these by name off the per-instance LiveCore meter; kept as literals to mirror the names
    // LiveCoreMetrics defines (the SliMetricsEndpointTests assert the same exported series).
    private const string _realtimeConnectionsInstrument = "livecore.realtime.connections";
    private const string _revealDurationInstrument = "livecore.reveal.duration";
    private const string _eventDeliveryFailuresInstrument = "livecore.event.delivery.failures";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public RealtimeHubLoadBaselineTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    // =====================================================================
    // Always-on: the documented default baseline parses and is internally consistent. This pins the recorded
    // baseline numbers in code (docs/15) and gives the harness file real, always-running coverage even on a
    // default dotnet test where the load run itself is skipped.
    // =====================================================================

    [Fact]
    public void The_default_capacity_baseline_profile_is_well_formed()
    {
        // An empty environment yields the documented defaults...
        var defaults = RealtimeHubLoadProfile.Parse(_ => null);

        Assert.False(defaults.Enabled);
        Assert.Equal(RealtimeHubLoadProfile.DefaultInstances, defaults.Instances);
        Assert.Equal(RealtimeHubLoadProfile.DefaultConnectionsPerInstance, defaults.ConnectionsPerInstance);
        Assert.Equal(RealtimeHubLoadProfile.DefaultRevealRounds, defaults.RevealRounds);
        Assert.Equal(
            RealtimeHubLoadProfile.DefaultInstances * RealtimeHubLoadProfile.DefaultConnectionsPerInstance,
            defaults.TotalConnections);

        // ...and the baseline is internally consistent: a positive target, a p99 ceiling at or above p95, and
        // an error-rate cap in [0, 1].
        Assert.True(defaults.TotalConnections > 0);
        Assert.True(defaults.P95CapMs > 0);
        Assert.True(defaults.P99CapMs >= defaults.P95CapMs);
        Assert.InRange(defaults.MaxErrorRate, 0d, 1d);
        Assert.True(defaults.Timeout > TimeSpan.Zero);

        // A truthy opt-in flag enables it; counts are clamped to a sane minimum so a zero/garbage override can
        // never make an "enabled" run a silent no-op, and an out-of-range error rate is clamped into [0, 1].
        var overridden = RealtimeHubLoadProfile.Parse(name => name switch
        {
            RealtimeHubLoadProfile.EnabledVariable => "true",
            RealtimeHubLoadProfile.InstancesVariable => "0",
            RealtimeHubLoadProfile.ConnectionsPerInstanceVariable => "garbage",
            RealtimeHubLoadProfile.RevealRoundsVariable => "-5",
            RealtimeHubLoadProfile.MaxErrorRateVariable => "2.5",
            _ => null,
        });

        Assert.True(overridden.Enabled);
        Assert.True(overridden.Instances >= 1);
        Assert.Equal(RealtimeHubLoadProfile.DefaultConnectionsPerInstance, overridden.ConnectionsPerInstance);
        Assert.True(overridden.RevealRounds >= 1);
        Assert.Equal(1d, overridden.MaxErrorRate);
    }

    // =====================================================================
    // Opt-in: the load/soak harness drives the hub to the target concurrency over the backplane-backed
    // multi-instance topology and asserts the recorded baseline (sustained connections, p95/p99 reveal latency,
    // error rate under the cap). Skipped unless explicitly enabled AND the shared Postgres + Redis backplane are
    // configured, so it never runs on the per-PR critical path.
    // =====================================================================

    [Fact]
    public async Task The_realtime_hub_sustains_the_capacity_baseline_under_load()
    {
        var profile = RealtimeHubLoadProfile.FromEnvironment();
        if (!profile.Enabled)
        {
            // Opt-in only: a default dotnet test (and the per-PR integration job) skips the load/soak.
            return;
        }

        if (!PostgresTestDatabase.IsConfigured || !RedisBackplaneTestServer.IsConfigured)
        {
            // The recorded baseline is measured against the backplane-backed multi-instance topology, which
            // needs a shared PostgreSQL system of record and a Redis/Valkey backplane. Without both the run is
            // not configured and is skipped (mirroring RedisBackplanePropagationTests).
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        var sharedDatabase = PostgresTestDatabase.CreateMigratedDatabase();
        var channelPrefix = $"livecore-load-{Guid.NewGuid():N}";
        var instances = new List<LoadInstanceApiFactory>(profile.Instances);
        try
        {
            for (var i = 0; i < profile.Instances; i++)
            {
                instances.Add(new LoadInstanceApiFactory(sharedDatabase, channelPrefix));
            }

            // Force each instance's LiveCoreMetrics (and so its meter + instruments) to exist, then scope the
            // recorder to those exact meter references so it captures only this run's measurements.
            var meters = instances
                .Select(factory => factory.Services.GetRequiredService<LiveCoreMetrics>().Meter)
                .ToArray();
            using var recorder = new RealtimeLoadMetricsRecorder(meters);

            var audience = await SeedAudienceAsync(instances[0], profile.TotalConnections);

            await using var probes = await ConnectAudienceAsync(instances, audience, profile);

            // BARRIER: the realtime-connections gauge reached the full target — every connection is admitted
            // into its server-managed groups (the gauge is incremented as the last step of OnConnectedAsync),
            // so a reveal now fans out to the whole audience.
            await WaitForLiveConnectionsAsync(recorder, profile.TotalConnections, profile.Timeout);
            var sustainedAtStart = recorder.LiveConnections;

            // Drive the reveal/event fan-out from a host on the first instance; each round's audience-wide
            // reveal must reach every connected probe (across the backplane to the other instances) within the
            // per-round deadline.
            using var host = instances[0].CreateClientFor(_hostSubject, _issuer, _orgA);
            var roundDeadline = TimeSpan.FromMilliseconds(
                Math.Max(2_000, profile.Timeout.TotalMilliseconds / (profile.RevealRounds + 1)));

            long deliveryAttempts = 0;
            long deliveryFailures = 0;
            for (var round = 0; round < profile.RevealRounds; round++)
            {
                var resourceId = Guid.CreateVersion7();
                var connectedCount = probes.Count;
                deliveryAttempts += connectedCount;

                var revealOk = await PostRevealAsync(host, audience.SessionId, resourceId, $"load-reveal-{round}");
                if (!revealOk)
                {
                    // A reveal the command itself failed could reach no one — count the whole round as missed.
                    deliveryFailures += connectedCount;
                    continue;
                }

                deliveryFailures += await CountDeliveryMissesAsync(probes, resourceId, roundDeadline);
            }

            var sustainedAtEnd = recorder.LiveConnections;
            var revealDurationsMs = recorder.RevealDurationsMs;
            var p95 = Percentile(revealDurationsMs, 95);
            var p99 = Percentile(revealDurationsMs, 99);
            var errorRate = deliveryAttempts == 0 ? 0d : (double)deliveryFailures / deliveryAttempts;

            ReportBaseline(profile, sustainedAtStart, sustainedAtEnd, revealDurationsMs.Count, p95, p99, errorRate, recorder.EventDeliveryFailures);

            // ASSERT THE RECORDED BASELINE (the regression detector). A run that cannot sustain the target
            // connections, whose reveal latency percentiles exceed the ceilings, or whose delivery error rate
            // exceeds the cap, fails.
            Assert.True(
                sustainedAtStart >= profile.TotalConnections,
                $"Sustained connections {sustainedAtStart} below the baseline target {profile.TotalConnections}.");
            Assert.True(
                sustainedAtEnd >= profile.TotalConnections,
                $"Connections were not sustained through the soak: {sustainedAtEnd} of {profile.TotalConnections} remained.");
            Assert.True(
                revealDurationsMs.Count > 0,
                $"No reveal-latency samples were captured from the reveal-duration histogram across {profile.RevealRounds} rounds.");
            Assert.True(
                p95 <= profile.P95CapMs,
                $"p95 reveal latency {p95:F1}ms exceeded the baseline ceiling {profile.P95CapMs:F1}ms.");
            Assert.True(
                p99 <= profile.P99CapMs,
                $"p99 reveal latency {p99:F1}ms exceeded the baseline ceiling {profile.P99CapMs:F1}ms.");
            Assert.True(
                errorRate <= profile.MaxErrorRate,
                $"Delivery error rate {errorRate:P2} exceeded the baseline cap {profile.MaxErrorRate:P2}.");
        }
        finally
        {
            foreach (var factory in instances)
            {
                await factory.DisposeAsync();
            }

            PostgresTestDatabase.DropDatabase(sharedDatabase);
        }
    }

    // =====================================================================
    // Baseline reporting.
    // =====================================================================

    /// <summary>
    /// Emits the measured baseline as a single, parseable line — to the test output (local visibility) and, when
    /// <see cref="RealtimeHubLoadProfile.ResultPathVariable"/> is set, appended to that file so the scheduled CI
    /// job can capture it as a build artifact. It carries only counts and timings (threat T7).
    /// </summary>
    private void ReportBaseline(
        RealtimeHubLoadProfile profile,
        long sustainedAtStart,
        long sustainedAtEnd,
        int latencySamples,
        double p95Ms,
        double p99Ms,
        double errorRate,
        long eventDeliveryFailures)
    {
        var summary =
            $"[CORE-PERF-009] realtime hub capacity baseline: instances={profile.Instances} "
            + $"connectionsPerInstance={profile.ConnectionsPerInstance} targetConnections={profile.TotalConnections} "
            + $"sustainedStart={sustainedAtStart} sustainedEnd={sustainedAtEnd} revealRounds={profile.RevealRounds} "
            + $"latencySamples={latencySamples} p95Ms={p95Ms:F1} (cap={profile.P95CapMs:F1}) "
            + $"p99Ms={p99Ms:F1} (cap={profile.P99CapMs:F1}) errorRate={errorRate:P3} (cap={profile.MaxErrorRate:P3}) "
            + $"eventDeliveryFailures={eventDeliveryFailures}";

        _output.WriteLine(summary);

        if (profile.ResultPath is { } path)
        {
            try
            {
                File.AppendAllText(path, summary + Environment.NewLine);
            }
            catch (IOException)
            {
                // Best-effort artifact capture: a failure to persist the summary never fails the baseline run.
            }
        }
    }

    /// <summary>
    /// The nearest-rank percentile (in milliseconds) of the captured reveal-duration samples; returns 0 for an
    /// empty sample set (the assertions separately require at least one captured sample).
    /// </summary>
    private static double Percentile(IReadOnlyList<double> valuesMs, double percentile)
    {
        if (valuesMs.Count == 0)
        {
            return 0d;
        }

        var ordered = valuesMs.OrderBy(static value => value).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100d * ordered.Length);
        var index = Math.Clamp(rank - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    /// <summary>
    /// Seeds one Live session in a workspace with a host who may reveal (Host in org AND workspace) and
    /// <paramref name="participantCount"/> participants, each an organization member owning one ACTIVE
    /// participant record (so the resolver admits a participant connection and the audience fan-out delivers to
    /// it). All fixtures are generic Core vocabulary (AGENTS.md).
    /// </summary>
    private static async Task<Audience> SeedAudienceAsync(WorkspaceApiFactory factory, int participantCount)
    {
        Audience audience = default;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);

            var hostUser = await db.AddUserAsync(_issuer, _hostSubject);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);

            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Load", SessionStatus.Live);

            var participants = new ParticipantSeat[participantCount];
            for (var index = 0; index < participantCount; index++)
            {
                var subject = ParticipantSubject(index);
                var participantUser = await db.AddUserAsync(_issuer, subject);
                await db.AddOrganizationMemberAsync(org.Id, participantUser.Id, MembershipRole.Participant);
                var participant = await db.AddParticipantAsync(org.Id, workspace.Id, participantUser.Id, $"Seat {index:0000}");
                participants[index] = new ParticipantSeat(subject, participant.Id, session.Id);
            }

            audience = new Audience(session.Id, participants);
        });

        return audience;
    }

    // =====================================================================
    // Connection helpers.
    // =====================================================================

    /// <summary>
    /// Opens one real hub connection per seeded participant, distributing them round-robin across the API
    /// instances (so an audience-wide reveal must fan out across the backplane). Connections are established
    /// with a bounded concurrency so a busy CI runner is not overwhelmed while still exercising concurrent
    /// connects — the metric barrier in the caller waits for them all to be admitted.
    /// </summary>
    private static async Task<ProbeSet> ConnectAudienceAsync(
        IReadOnlyList<LoadInstanceApiFactory> instances,
        Audience audience,
        RealtimeHubLoadProfile profile)
    {
        var probes = new LoadProbe?[audience.Participants.Count];
        using var concurrency = new SemaphoreSlim(Math.Min(16, audience.Participants.Count));

        var opens = new Task[audience.Participants.Count];
        for (var index = 0; index < audience.Participants.Count; index++)
        {
            var seat = audience.Participants[index];
            var instance = instances[index % instances.Count];
            var slot = index;
            opens[slot] = Task.Run(async () =>
            {
                await concurrency.WaitAsync();
                try
                {
                    probes[slot] = await ConnectParticipantAsync(instance, seat, profile.Timeout);
                }
                finally
                {
                    concurrency.Release();
                }
            });
        }

        try
        {
            await Task.WhenAll(opens);
        }
        catch
        {
            // Dispose any connection that did open before surfacing the failure, so a partial open never leaks
            // live sockets.
            foreach (var probe in probes)
            {
                if (probe is not null)
                {
                    await probe.DisposeAsync();
                }
            }

            throw;
        }

        return new ProbeSet([.. probes.Select(probe => probe!)]);
    }

    private static async Task<LoadProbe> ConnectParticipantAsync(
        LoadInstanceApiFactory factory,
        ParticipantSeat seat,
        TimeSpan timeout)
    {
        var url = new Uri(
            factory.Server.BaseAddress,
            $"hubs/session?organizationSlug={_orgA}&sessionId={seat.SessionId}&participantId={seat.ParticipantId}");

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthenticationHandler.SubjectHeader, seat.Subject);
                options.Headers.Add(TestAuthenticationHandler.IssuerHeader, _issuer);
                options.Headers.Add(TestAuthenticationHandler.OrganizationHeader, _orgA);
            })
            .Build();

        var probe = new LoadProbe(connection);
        using var startCancellation = new CancellationTokenSource(timeout);
        await connection.StartAsync(startCancellation.Token);
        return probe;
    }

    /// <summary>
    /// Waits until the summed realtime-connections gauge reaches <paramref name="target"/> (every connection
    /// admitted into its groups), failing if the target is not reached within the run budget.
    /// </summary>
    private static async Task WaitForLiveConnectionsAsync(
        RealtimeLoadMetricsRecorder recorder,
        int target,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (recorder.LiveConnections < target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(
            recorder.LiveConnections >= target,
            $"Only {recorder.LiveConnections} of {target} realtime connections were admitted within the run budget.");
    }

    /// <summary>
    /// Counts how many connected probes did NOT receive the round's reveal within the per-round deadline. A
    /// per-resource awaiter that also checks already-received envelopes means a reveal delivered before the
    /// await is never miscounted as a miss.
    /// </summary>
    private static async Task<long> CountDeliveryMissesAsync(ProbeSet probes, Guid resourceId, TimeSpan deadline)
    {
        var waits = probes.Probes.Select(probe => probe.WaitForRevealOf(resourceId)).ToArray();
        var all = Task.WhenAll(waits);
        await Task.WhenAny(all, Task.Delay(deadline));
        return waits.Count(wait => !wait.IsCompletedSuccessfully);
    }

    private static async Task<bool> PostRevealAsync(HttpClient client, Guid sessionId, Guid resourceId, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static string ParticipantSubject(int index) => $"load-participant-{index:0000}";

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

    private static bool IsRevealOf(string rawEnvelope, Guid resourceId)
        => EventTypeOf(rawEnvelope) == SessionEventTypes.ContentRevealed
            && rawEnvelope.Contains(resourceId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>One seeded audience seat: its OIDC subject, its participant record id and the session it joins.</summary>
    private readonly record struct ParticipantSeat(string Subject, Guid ParticipantId, Guid SessionId);

    private readonly record struct Audience(Guid SessionId, IReadOnlyList<ParticipantSeat> Participants);

    /// <summary>A disposable set of probes that disposes every wrapped connection together.</summary>
    private sealed class ProbeSet : IAsyncDisposable
    {
        public ProbeSet(IReadOnlyList<LoadProbe> probes) => Probes = probes;

        public IReadOnlyList<LoadProbe> Probes { get; }

        public int Count => Probes.Count;

        public async ValueTask DisposeAsync()
        {
            foreach (var probe in Probes)
            {
                await probe.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Wraps a live <see cref="HubConnection"/> and records every <c>SessionEvent</c> delivered to it. Exposes a
    /// per-resource awaiter (<see cref="WaitForRevealOf"/>) that completes when a <c>ContentRevealed</c> for a
    /// resource arrives — checking the already-received envelopes first — so a reveal that arrived before the
    /// await is never missed.
    /// </summary>
    private sealed class LoadProbe : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly object _gate = new();
        private readonly List<string> _received = [];
        private readonly List<(Guid ResourceId, TaskCompletionSource<string> Completion)> _waiters = [];

        public LoadProbe(HubConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            _connection = connection;

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

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    /// <summary>
    /// Captures the realtime baseline measurements from the existing metrics, scoped to the exact per-instance
    /// <see cref="LiveCoreMetrics.Meter"/> references so a concurrently-running host's measurements are never
    /// captured. It enables only the three instruments the baseline reads — the realtime-connections gauge
    /// (summed deltas give the live connection count), the reveal-duration histogram (samples for the
    /// percentiles, captured in milliseconds) and the event-delivery-failure counter (reported alongside).
    /// </summary>
    private sealed class RealtimeLoadMetricsRecorder : IDisposable
    {
        private static readonly HashSet<string> _trackedInstruments =
        [
            _realtimeConnectionsInstrument,
            _revealDurationInstrument,
            _eventDeliveryFailuresInstrument,
        ];

        private readonly HashSet<Meter> _meters;
        private readonly MeterListener _listener;
        private readonly object _gate = new();
        private readonly List<double> _revealDurationsMs = [];
        private long _connectionDelta;
        private long _eventDeliveryFailures;

        public RealtimeLoadMetricsRecorder(IEnumerable<Meter> meters)
        {
            _meters = [.. meters];
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (_meters.Contains(instrument.Meter) && _trackedInstruments.Contains(instrument.Name))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
            _listener.Start();
        }

        /// <summary>The live realtime-connection count from the gauge (net opened minus closed) across all instances.</summary>
        public long LiveConnections => Interlocked.Read(ref _connectionDelta);

        /// <summary>The realtime event-delivery transport failures observed during the run.</summary>
        public long EventDeliveryFailures => Interlocked.Read(ref _eventDeliveryFailures);

        /// <summary>A snapshot of the captured reveal-command latency samples, in milliseconds.</summary>
        public IReadOnlyList<double> RevealDurationsMs
        {
            get
            {
                lock (_gate)
                {
                    return _revealDurationsMs.ToArray();
                }
            }
        }

        private void OnLongMeasurement(
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            switch (instrument.Name)
            {
                case _realtimeConnectionsInstrument:
                    Interlocked.Add(ref _connectionDelta, measurement);
                    break;
                case _eventDeliveryFailuresInstrument:
                    Interlocked.Add(ref _eventDeliveryFailures, measurement);
                    break;
            }
        }

        private void OnDoubleMeasurement(
            Instrument instrument,
            double measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            if (instrument.Name == _revealDurationInstrument)
            {
                lock (_gate)
                {
                    // The histogram records seconds (docs/15); the baseline is documented in milliseconds.
                    _revealDurationsMs.Add(measurement * 1000d);
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> modeling ONE API instance of the multi-instance load topology: it
    /// points the production persistence at a SHARED PostgreSQL database (so every instance reads/writes the
    /// same system of record) and configures the production Redis/Valkey SignalR backplane (CORE-OPS-007) under
    /// a shared channel prefix, so a group send on one instance fans out to connections on the others. It mirrors
    /// the cross-instance factory the correctness suite uses; the only test-host difference from production is the
    /// shared database, the test auth scheme the base already swaps, and the request rate limiter turned OFF so
    /// the limiter never skews the hub capacity measurement (the limiter is covered by RateLimitingEndpointTests).
    /// </summary>
    private sealed class LoadInstanceApiFactory : WorkspaceApiFactory
    {
        private readonly string _sharedConnectionString;
        private readonly string _channelPrefix;
        private readonly Guid _instanceId = Guid.NewGuid();

        public LoadInstanceApiFactory(string sharedConnectionString, string channelPrefix)
        {
            _sharedConnectionString = sharedConnectionString;
            _channelPrefix = channelPrefix;
        }

        // Point the production Npgsql registration at the SHARED database with a distinct application name so
        // each in-process instance gets its own connection pool (pools are keyed by the connection string). The
        // shared database's lifetime is owned by the test; the base's per-factory drop on dispose is an
        // idempotent best-effort DROP IF EXISTS, so several instances dropping it is harmless.
        protected override string CreatePostgresDatabase() =>
            new NpgsqlConnectionStringBuilder(_sharedConnectionString)
            {
                ApplicationName = $"livecore-load-instance-{_instanceId:N}",
            }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // The production realtime backplane: AddLiveCoreRealtime selects the Redis/Valkey-backed SignalR
            // HubLifetimeManager when a connection string is present, and the channel prefix namespaces this
            // run's pub/sub channels.
            builder.UseSetting("Realtime:Backplane:ConnectionString", RedisBackplaneTestServer.ConnectionString);
            builder.UseSetting("Realtime:Backplane:ChannelPrefix", _channelPrefix);

            // Measure the hub, not the edge rate limiter: turn the limiter off so a burst of audience connects /
            // reveals is never throttled into the baseline (CORE-SEC-007 documents this floor switch).
            builder.UseSetting("RateLimiting:Enabled", "false");

            base.ConfigureWebHost(builder);
        }
    }
}
