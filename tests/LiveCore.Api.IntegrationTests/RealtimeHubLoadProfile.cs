// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// The runnable, repeatable configuration of the realtime hub load/soak harness (CORE-PERF-009) and the
/// CAPACITY BASELINE it asserts. The harness (<see cref="RealtimeHubLoadBaselineTests"/>) drives the SignalR
/// realtime hub to a target concurrency across the backplane-backed multi-instance topology and asserts the
/// recorded baseline — sustained connections, p95/p99 reveal latency and the delivery error rate — so a
/// regression below the baseline FAILS the run.
///
/// Every parameter is read from an environment variable with a documented default, so the harness is
/// repeatable and tunable without a code change: the dedicated scheduled CI job sets the opt-in flag and may
/// raise the target; a default <c>dotnet test</c> leaves <see cref="EnabledVariable"/> unset, so the harness
/// is a no-op (it never runs on the per-PR critical path). The defaults below ARE the recorded baseline
/// documented in <c>docs/15_OBSERVABILITY.md</c> and cited by <c>docs/13_SELF_HOSTING_REQUIREMENTS.md</c>.
///
/// The values are generic Core vocabulary (AGENTS.md) and carry no credential — only counts and timespans
/// (threat T7).
/// </summary>
internal sealed class RealtimeHubLoadProfile
{
    /// <summary>
    /// Opt-in flag. When set to a truthy value (<c>1</c>/<c>true</c>/<c>yes</c>, case-insensitive) the load
    /// harness runs; absent (the default for every per-PR build) it is skipped, keeping the load/soak out of
    /// the per-PR CI critical path. A backplane-backed multi-instance run additionally requires the shared
    /// PostgreSQL (<see cref="PostgresTestDatabase.IsConfigured"/>) and Redis/Valkey backplane
    /// (<see cref="RedisBackplaneTestServer.IsConfigured"/>) the cross-instance topology needs.
    /// </summary>
    public const string EnabledVariable = "LIVECORE_REALTIME_LOAD";

    /// <summary>Number of API instances to boot against the shared database + backplane (the multi-instance topology).</summary>
    public const string InstancesVariable = "LIVECORE_REALTIME_LOAD_INSTANCES";

    /// <summary>Target simultaneous hub connections PER API instance; the total target is this times the instance count.</summary>
    public const string ConnectionsPerInstanceVariable = "LIVECORE_REALTIME_LOAD_CONNECTIONS_PER_INSTANCE";

    /// <summary>Number of audience-wide reveal/event fan-out rounds the harness drives (the soak length).</summary>
    public const string RevealRoundsVariable = "LIVECORE_REALTIME_LOAD_REVEALS";

    /// <summary>The p95 reveal-command latency ceiling, in milliseconds — the baseline a run must stay under.</summary>
    public const string P95Variable = "LIVECORE_REALTIME_LOAD_P95_MS";

    /// <summary>The p99 reveal-command latency ceiling, in milliseconds — the baseline a run must stay under.</summary>
    public const string P99Variable = "LIVECORE_REALTIME_LOAD_P99_MS";

    /// <summary>The maximum tolerated delivery error rate (failed fan-out deliveries ÷ delivery attempts), in [0, 1].</summary>
    public const string MaxErrorRateVariable = "LIVECORE_REALTIME_LOAD_MAX_ERROR_RATE";

    /// <summary>Wall-clock bound, in seconds, applied to connection establishment and to each reveal round.</summary>
    public const string TimeoutSecondsVariable = "LIVECORE_REALTIME_LOAD_TIMEOUT_SECONDS";

    /// <summary>Optional path the harness writes its measured-baseline summary line to (captured by the CI job as an artifact).</summary>
    public const string ResultPathVariable = "LIVECORE_REALTIME_LOAD_RESULT_PATH";

    // The recorded baseline (defaults). These are deliberately conservative FLOORS/CEILINGS validated against
    // the in-repo backplane-backed multi-instance test topology on a modest CI runner, so the harness is a
    // dependable regression detector rather than a flaky gate. Production per-instance capacity scales with the
    // instance's CPU/memory sizing (docs/13, CORE-DEP-007); the scheduled job can raise the target to measure a
    // sized deployment. Keep these in sync with the baseline table in docs/15_OBSERVABILITY.md.
    public const int DefaultInstances = 2;
    public const int DefaultConnectionsPerInstance = 50;
    public const int DefaultRevealRounds = 20;
    public const double DefaultP95Ms = 1500;
    public const double DefaultP99Ms = 3000;
    public const double DefaultMaxErrorRate = 0.01;
    public const int DefaultTimeoutSeconds = 120;

    private RealtimeHubLoadProfile(
        bool enabled,
        int instances,
        int connectionsPerInstance,
        int revealRounds,
        double p95CapMs,
        double p99CapMs,
        double maxErrorRate,
        TimeSpan timeout,
        string? resultPath)
    {
        Enabled = enabled;
        Instances = instances;
        ConnectionsPerInstance = connectionsPerInstance;
        RevealRounds = revealRounds;
        P95CapMs = p95CapMs;
        P99CapMs = p99CapMs;
        MaxErrorRate = maxErrorRate;
        Timeout = timeout;
        ResultPath = resultPath;
    }

    public bool Enabled { get; }

    public int Instances { get; }

    public int ConnectionsPerInstance { get; }

    public int RevealRounds { get; }

    public double P95CapMs { get; }

    public double P99CapMs { get; }

    public double MaxErrorRate { get; }

    public TimeSpan Timeout { get; }

    public string? ResultPath { get; }

    /// <summary>The total simultaneous connection target across every instance — the documented "sustained connections" baseline.</summary>
    public int TotalConnections => Instances * ConnectionsPerInstance;

    /// <summary>Reads the profile from the process environment.</summary>
    public static RealtimeHubLoadProfile FromEnvironment() => Parse(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Builds the profile from an arbitrary variable source so the parsing/clamping logic can be asserted in a
    /// test without touching the process environment. A blank or unparsable value falls back to the documented
    /// default; counts are clamped to a sane minimum so a misconfigured target can never make the harness a
    /// silent no-op while still reporting itself enabled.
    /// </summary>
    public static RealtimeHubLoadProfile Parse(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var enabled = ParseBool(read(EnabledVariable));
        var instances = Math.Max(1, ParseInt(read(InstancesVariable), DefaultInstances));
        var connectionsPerInstance = Math.Max(1, ParseInt(read(ConnectionsPerInstanceVariable), DefaultConnectionsPerInstance));
        var revealRounds = Math.Max(1, ParseInt(read(RevealRoundsVariable), DefaultRevealRounds));
        var p95 = ParsePositiveDouble(read(P95Variable), DefaultP95Ms);
        var p99 = ParsePositiveDouble(read(P99Variable), DefaultP99Ms);
        var maxErrorRate = Math.Clamp(ParseDouble(read(MaxErrorRateVariable), DefaultMaxErrorRate), 0d, 1d);
        var timeoutSeconds = Math.Max(1, ParseInt(read(TimeoutSecondsVariable), DefaultTimeoutSeconds));
        var resultPath = read(ResultPathVariable) is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;

        return new RealtimeHubLoadProfile(
            enabled,
            instances,
            connectionsPerInstance,
            revealRounds,
            p95,
            p99,
            maxErrorRate,
            TimeSpan.FromSeconds(timeoutSeconds),
            resultPath);
    }

    private static bool ParseBool(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParsePositiveDouble(string? value, double fallback)
    {
        var parsed = ParseDouble(value, fallback);
        return parsed > 0 ? parsed : fallback;
    }
}
