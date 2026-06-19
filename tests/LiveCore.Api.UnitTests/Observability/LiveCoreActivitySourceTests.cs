// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Tests for <see cref="LiveCoreActivitySource"/> (CORE-OBS-003), the single owner of the distributed-tracing
/// spans docs/15_OBSERVABILITY.md requires the key request and realtime flows to produce. They assert —
/// through a real <see cref="ActivityListener"/> (<see cref="RecordedActivities"/>), the same subscription
/// mechanism the OpenTelemetry SDK uses — that each documented <c>Start*</c> method produces a span with the
/// expected name, kind and low-cardinality tags, and that <see cref="TracingServiceCollectionExtensions"/>
/// wires an exportable OpenTelemetry tracer (the "exportable by a configured collector" acceptance criterion).
///
/// The class shares the serialized "LiveCore tracing" collection so its listener never captures another
/// span-producing test's activities.
/// </summary>
[Collection(LiveCoreTracingTestCollection.Name)]
public sealed class LiveCoreActivitySourceTests
{
    [Fact]
    public void StartHttpServerRequest_produces_a_server_span()
    {
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        using (source.StartHttpServerRequest())
        {
            // span is open here
        }

        var activity = recorded.Single("http.server.request");
        Assert.Equal(ActivityKind.Server, activity.Kind);
    }

    [Fact]
    public void StartReveal_produces_a_span_tagged_by_operation()
    {
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        using (source.StartReveal(operation: "reveal"))
        {
        }

        Assert.Equal(1, recorded.Count("livecore.reveal"));
        Assert.True(recorded.HasTag("livecore.reveal", "operation", "reveal"));
    }

    [Fact]
    public void StartSessionEventPublish_produces_a_producer_span_tagged_by_event_type()
    {
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        using (source.StartSessionEventPublish("ContentRevealed"))
        {
        }

        var activity = recorded.Single("livecore.session_event.publish");
        Assert.Equal(ActivityKind.Producer, activity.Kind);
        Assert.True(recorded.HasTag("livecore.session_event.publish", "livecore.event.type", "ContentRevealed"));
    }

    [Fact]
    public void A_publish_started_inside_a_reveal_nests_under_it()
    {
        // The reveal/publish path: a session-event publish started while a reveal span is current becomes a
        // CHILD of the reveal span (same trace), so a collector reconstructs the reveal -> publish causal tree.
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        using (var reveal = source.StartReveal(operation: "reveal"))
        {
            using (source.StartSessionEventPublish("ContentRevealed"))
            {
            }

            var revealActivity = reveal!;
            var publish = recorded.Single("livecore.session_event.publish");
            Assert.Equal(revealActivity.TraceId, publish.TraceId);
            Assert.Equal(revealActivity.SpanId, publish.ParentSpanId);
        }
    }

    [Fact]
    public void No_span_is_produced_when_nothing_is_listening()
    {
        // With no listener attached, starting a span is a cheap no-op that returns null, so an uninstrumented
        // host pays nothing for the hooks.
        using var source = new LiveCoreActivitySource();

        using var activity = source.StartReveal(operation: "reveal");

        Assert.Null(activity);
    }

    [Fact]
    public void StartWorkerJobLoop_produces_an_internal_span_tagged_by_the_loop_name()
    {
        // CORE-OBS-012: one INTERNAL span per background worker job-loop iteration, tagged with the coarse loop
        // name. The loop sets the outcome tag when the iteration completes (asserted end-to-end in the worker's
        // own tests); here we pin the span name, kind and loop-name attribute the worker emits.
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        using (source.StartWorkerJobLoop("data-retention"))
        {
        }

        var activity = recorded.Single(LiveCoreActivitySource.WorkerJobLoopActivityName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.True(recorded.HasTag(
            LiveCoreActivitySource.WorkerJobLoopActivityName,
            LiveCoreActivitySource.WorkerJobNameTag,
            "data-retention"));
    }

    [Fact]
    public void StartWorkerJobLoop_produces_no_span_when_nothing_is_listening()
    {
        // Same inert posture as the request/reveal spans: with no listener (no tracer/exporter wired) starting
        // the worker loop span is a no-op that returns null, so an unconfigured worker pays nothing.
        using var source = new LiveCoreActivitySource();

        using var activity = source.StartWorkerJobLoop("data-retention");

        Assert.Null(activity);
    }

    [Fact]
    public void AddLiveCoreWorkerOpenTelemetryTracing_wires_an_exportable_tracer_with_an_otlp_collector()
    {
        // CORE-OBS-012, parity with the API: with an OTLP endpoint configured the worker's tracing pipeline
        // (TracerProvider + OTLP exporter + the LiveCore source) resolves and builds without error, and the
        // export decision reports the exporter is wired. The exporter connects lazily, so building opens no
        // connection.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TracingServiceCollectionExtensions.OtlpEndpointConfigurationKey] = "http://collector.test:4317",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLiveCoreWorkerOpenTelemetryTracing(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<LiveCoreActivitySource>());
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.True(TracingServiceCollectionExtensions.IsOtlpExportConfigured(configuration));
    }

    [Fact]
    public void AddLiveCoreWorkerOpenTelemetryTracing_wires_no_exporter_without_an_endpoint()
    {
        // The "with no endpoint configured no exporter is wired" acceptance criterion (inert by default, parity
        // with the API). With nothing configured the source is still registered (so spans are produced and any
        // in-process listener observes them) but the OTLP exporter is NOT wired, so the worker never reaches a
        // non-existent collector.
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLiveCoreWorkerOpenTelemetryTracing(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<LiveCoreActivitySource>());
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.False(TracingServiceCollectionExtensions.IsOtlpExportConfigured(configuration));
    }

    [Fact]
    public void AddLiveCoreOpenTelemetryTracing_wires_an_exportable_tracer_with_an_otlp_collector()
    {
        // The "traces can be exported by a configured collector" acceptance criterion: with an OTLP endpoint
        // configured the tracing pipeline (TracerProvider + OTLP exporter + the LiveCore source) resolves and
        // builds without error. The exporter connects lazily, so building the provider opens no connection.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TracingServiceCollectionExtensions.OtlpEndpointConfigurationKey] = "http://collector.test:4317",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLiveCoreOpenTelemetryTracing(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<LiveCoreActivitySource>());
        Assert.NotNull(provider.GetService<TracerProvider>());
    }
}
