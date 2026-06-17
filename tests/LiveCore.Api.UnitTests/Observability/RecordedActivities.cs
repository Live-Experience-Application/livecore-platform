// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// xUnit collection that SERIALIZES every test which listens to the <see cref="LiveCoreActivitySource.SourceName"/>
/// source (CORE-OBS-003). The source name is process-global, so two tests producing Core spans at the same time
/// would each capture the other's spans through a shared <see cref="RecordedActivities"/> listener; running them
/// in one non-parallel collection makes each test see only its own spans (so the "exactly one" assertions are
/// reliable). It is the tracing counterpart of <c>LiveCoreMetricsTestCollection</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCoreTracingTestCollection
{
    public const string Name = "LiveCore tracing";
}

/// <summary>
/// A test-only listener over the <see cref="LiveCoreActivitySource.SourceName"/> source that records every
/// completed activity so a test can assert which Core spans were produced and how they are tagged
/// (CORE-OBS-003). It uses the built-in <see cref="ActivityListener"/> — the same subscription mechanism the
/// OpenTelemetry SDK uses — and samples every activity as recorded, so the spans are created and captured even
/// with no exporter wired. Dispose it to stop listening.
/// </summary>
internal sealed class RecordedActivities : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly Lock _gate = new();
    private readonly List<Activity> _stopped = [];

    public RecordedActivities()
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

    /// <summary>The number of recorded activities with the given operation name.</summary>
    public int Count(string operationName)
    {
        lock (_gate)
        {
            return _stopped.Count(activity => activity.OperationName == operationName);
        }
    }

    /// <summary>The single recorded activity with the given operation name (asserts exactly one).</summary>
    public Activity Single(string operationName)
    {
        lock (_gate)
        {
            return _stopped.Single(activity => activity.OperationName == operationName);
        }
    }

    /// <summary>Whether any recorded activity with the given operation name carries the given tag value.</summary>
    public bool HasTag(string operationName, string tagKey, object tagValue)
    {
        lock (_gate)
        {
            return _stopped.Any(activity =>
                activity.OperationName == operationName
                && activity.TagObjects.Any(tag => tag.Key == tagKey && Equals(tag.Value, tagValue)));
        }
    }

    public void Dispose() => _listener.Dispose();
}
