// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Assets;
using LiveCore.Api.Exports;
using LiveCore.Api.Observability;
using LiveCore.Api.Recaps;
using LiveCore.Api.Retention;
using LiveCore.Api.Store;
using LiveCore.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.SmokeTests;

/// <summary>
/// Worker distributed-tracing tests (CORE-OBS-012) — the story's required test. The background worker is the
/// host doing IRREVERSIBLE purge/cleanup/export/recap/reconciliation work, yet before this story produced zero
/// spans even when the API was configured for tracing (no ActivitySource/TracerProvider/OTLP anywhere under
/// apps/worker). It now mirrors the API tracing wiring: each loop iteration produces one
/// <see cref="LiveCoreActivitySource.WorkerJobLoopActivityName"/> span carrying the coarse loop name and
/// outcome, inert until <c>Tracing:Otlp:Endpoint</c> is configured.
///
/// <para>
/// These tests drive the REAL background-service loop with an EMPTY dependency-injection scope, so the sweep
/// fails fast (the sweep service is not registered — a DI resolution error, no database, no retry) and the
/// resilient loop records the iteration as a <see cref="WorkerJobOutcomes.Failure"/> outcome and moves on. That
/// makes the span deterministic and fast to observe (no real Postgres needed), while still exercising the
/// actual loop the worker runs. A real <see cref="ActivityListener"/> over the
/// <see cref="LiveCoreActivitySource.SourceName"/> source — the same subscription a collector's exporter uses —
/// captures the produced span, exactly as the API's tracing integration tests do.
/// </para>
/// </summary>
public class WorkerTracingTests
{
    private static readonly IConfiguration _defaultConfiguration = new ConfigurationBuilder().Build();

    /// <summary>
    /// The ONLY attribute names a worker job-loop span is allowed to carry (CORE-OBS-012, threat T7): the coarse
    /// loop name and the iteration outcome. Both are low-cardinality status values — never a tenant/principal id,
    /// resource coordinate or content. The "assert no content/PII attribute names are emitted" criterion.
    /// </summary>
    private static readonly string[] _allowedTagKeys =
    [
        LiveCoreActivitySource.WorkerJobNameTag,
        LiveCoreActivitySource.WorkerJobOutcomeTag,
    ];

    /// <summary>
    /// Identifier/content concepts that must NEVER appear as a span attribute NAME (threat T7). A backstop on
    /// top of the exact-allow-list assertion: even a future attribute must not name one of these.
    /// </summary>
    private static readonly string[] _forbiddenTagSubstrings =
    [
        "tenant", "organization", "workspace", "session", "user", "participant",
        "email", "principal", "token", "content", "purchase", "subject",
    ];

    public static IEnumerable<object[]> AllLoops()
    {
        yield return [WorkerJobNames.AssetCleanup];
        yield return [WorkerJobNames.RecapGeneration];
        yield return [WorkerJobNames.ExportProcessing];
        yield return [WorkerJobNames.StoreNotificationReconciliation];
        yield return [WorkerJobNames.DataRetention];
    }

    [Theory]
    [MemberData(nameof(AllLoops))]
    public async Task Each_loop_emits_one_span_per_run_with_the_loop_name_and_outcome(string jobName)
    {
        using var source = new LiveCoreActivitySource();
        using var metrics = new LiveCoreMetrics();
        using var capture = new ActivityCapture();

        await using var emptyScopeProvider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = emptyScopeProvider.GetRequiredService<IServiceScopeFactory>();
        var heartbeats = NewHeartbeats(jobName);

        using var service = CreateLoop(jobName, scopeFactory, heartbeats, metrics, source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await ((IHostedService)service).StartAsync(timeout.Token);
        try
        {
            var span = await capture.WaitForJobSpanAsync(jobName, timeout.Token);

            // Expected NAME and KIND (CORE-OBS-012): one INTERNAL span per loop iteration on the single LiveCore
            // source.
            Assert.Equal(LiveCoreActivitySource.WorkerJobLoopActivityName, span.OperationName);
            Assert.Equal(ActivityKind.Internal, span.Kind);

            // Expected ATTRIBUTES: the coarse loop name and the outcome. The empty scope makes the sweep fail
            // fast, so the resilient loop records the iteration as a failure.
            Assert.Equal(jobName, TagValue(span, LiveCoreActivitySource.WorkerJobNameTag));
            Assert.Equal(WorkerJobOutcomes.Failure, TagValue(span, LiveCoreActivitySource.WorkerJobOutcomeTag));
            Assert.Equal(ActivityStatusCode.Error, span.Status);

            // No content/PII attribute names (threat T7): the span carries EXACTLY the two allowed low-cardinality
            // keys and nothing that names a tenant/principal/resource/content field.
            var tagKeys = span.TagObjects.Select(tag => tag.Key).ToList();
            Assert.Equal(_allowedTagKeys.OrderBy(key => key), tagKeys.OrderBy(key => key));
            Assert.All(tagKeys, key => Assert.All(
                _forbiddenTagSubstrings,
                forbidden => Assert.DoesNotContain(forbidden, key, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await ((IHostedService)service).StopAsync(CancellationToken.None);
        }
    }

    private static BackgroundService CreateLoop(
        string jobName,
        IServiceScopeFactory scopeFactory,
        WorkerJobHeartbeats heartbeats,
        LiveCoreMetrics metrics,
        LiveCoreActivitySource source)
        => jobName switch
        {
            WorkerJobNames.AssetCleanup => new AssetCleanupBackgroundService(
                scopeFactory,
                AssetCleanupOptions.FromConfiguration(_defaultConfiguration),
                heartbeats,
                metrics,
                source,
                NullLogger<AssetCleanupBackgroundService>.Instance),
            WorkerJobNames.RecapGeneration => new RecapGenerationBackgroundService(
                scopeFactory,
                RecapGenerationOptions.FromConfiguration(_defaultConfiguration),
                heartbeats,
                metrics,
                source,
                NullLogger<RecapGenerationBackgroundService>.Instance),
            WorkerJobNames.ExportProcessing => new ExportProcessingBackgroundService(
                scopeFactory,
                ExportProcessingOptions.FromConfiguration(_defaultConfiguration),
                heartbeats,
                metrics,
                source,
                NullLogger<ExportProcessingBackgroundService>.Instance),
            WorkerJobNames.StoreNotificationReconciliation => new StoreNotificationReconciliationBackgroundService(
                scopeFactory,
                StoreNotificationReconciliationOptions.FromConfiguration(_defaultConfiguration),
                heartbeats,
                metrics,
                source,
                NullLogger<StoreNotificationReconciliationBackgroundService>.Instance),
            WorkerJobNames.DataRetention => new DataRetentionSweepBackgroundService(
                scopeFactory,
                DataRetentionOptions.FromConfiguration(_defaultConfiguration),
                heartbeats,
                metrics,
                source,
                NullLogger<DataRetentionSweepBackgroundService>.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(jobName), jobName, "Unknown worker loop."),
        };

    private static WorkerJobHeartbeats NewHeartbeats(string jobName)
        => new(
            new WorkerHeartbeatOptions(
                Path.Combine(Path.GetTempPath(), $"livecore-tracing-test-{Guid.NewGuid():N}"),
                WorkerHeartbeatOptions.DefaultStaleAfter),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            [jobName]);

    private static string? TagValue(Activity activity, string key)
        => activity.TagObjects.FirstOrDefault(tag => tag.Key == key).Value?.ToString();

    /// <summary>
    /// A real <see cref="ActivityListener"/> over the <see cref="LiveCoreActivitySource.SourceName"/> source
    /// that records every completed span, sampling each as recorded so spans are created even without an
    /// exporter wired — the same mechanism a collector's exporter subscribes by.
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Lock _gate = new();
        private readonly List<Activity> _stopped = [];

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == LiveCoreActivitySource.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>
        /// Polls until a worker job-loop span for <paramref name="jobName"/> has been recorded, returning it (or
        /// failing the test on timeout). Correlation is by the loop-name tag, so it stays reliable even though
        /// the source name is process-global.
        /// </summary>
        public async Task<Activity> WaitForJobSpanAsync(string jobName, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 500 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                Activity? match;
                lock (_gate)
                {
                    match = _stopped.FirstOrDefault(activity =>
                        activity.OperationName == LiveCoreActivitySource.WorkerJobLoopActivityName
                        && activity.TagObjects.Any(tag =>
                            tag.Key == LiveCoreActivitySource.WorkerJobNameTag
                            && Equals(tag.Value, jobName)));
                }

                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(20, cancellationToken);
            }

            Assert.Fail($"No '{LiveCoreActivitySource.WorkerJobLoopActivityName}' span for loop '{jobName}' was captured.");
            return null!;
        }

        public void Dispose() => _listener.Dispose();
    }
}
