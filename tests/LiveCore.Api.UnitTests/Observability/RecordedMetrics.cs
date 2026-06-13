using System.Diagnostics.Metrics;
using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// xUnit collection that SERIALIZES every test which listens to the <see cref="LiveCoreMetrics.MeterName"/>
/// meter (CORE-OBS-001). The meter name is process-global, so two tests recording on their own
/// <see cref="LiveCoreMetrics"/> instances at the same time would each capture the other's measurements
/// through a shared <see cref="RecordedMetrics"/> listener; running them in one non-parallel collection makes
/// each test see only its own measurements (so the negative "did not record" assertions are reliable).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCoreMetricsTestCollection
{
    public const string Name = "LiveCore metrics";
}

/// <summary>
/// A test-only listener over the <see cref="LiveCoreMetrics.MeterName"/> meter that records every long/double
/// measurement so a test can assert which Core instruments emitted and how much (CORE-OBS-001). It uses the
/// built-in <see cref="MeterListener"/> — the same subscription mechanism the OpenTelemetry SDK uses — so the
/// tests exercise the real instruments without an exporter. Dispose it to stop listening.
/// </summary>
internal sealed class RecordedMetrics : IDisposable
{
    private readonly MeterListener _listener;
    private readonly Lock _gate = new();
    private readonly List<Measurement> _measurements = [];

    public RecordedMetrics()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == LiveCoreMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
    }

    private void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagMap = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
        {
            tagMap[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(name, value, tagMap));
        }
    }

    /// <summary>The number of recorded measurements for the named instrument.</summary>
    public int Count(string instrumentName)
    {
        lock (_gate)
        {
            return _measurements.Count(measurement => measurement.Name == instrumentName);
        }
    }

    /// <summary>Whether the named instrument recorded any measurement carrying the given tag value.</summary>
    public bool HasTag(string instrumentName, string tagKey, object tagValue)
    {
        lock (_gate)
        {
            return _measurements.Any(measurement =>
                measurement.Name == instrumentName
                && measurement.Tags.TryGetValue(tagKey, out var value)
                && Equals(value, tagValue));
        }
    }

    public void Dispose() => _listener.Dispose();

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
}
