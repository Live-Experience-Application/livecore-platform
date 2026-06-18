// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using StackExchange.Redis;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Live reachability probe for the realtime scale-out backplane (CORE-OBS-009). When a deployment configures a
/// Redis/Valkey backplane (CORE-OPS-007, <see cref="LiveCore.Api.Realtime.RealtimeServiceCollectionExtensions"/>)
/// the SignalR <c>HubLifetimeManager</c> publishes every group send over it so realtime deliveries reach
/// connections on EVERY API instance. A configured-but-dead backplane silently drops cross-instance deliveries, so
/// a multi-instance host whose backplane is unreachable should leave the ready rotation rather than keep accepting
/// hub traffic it cannot fan out.
///
/// The probe owns a dedicated, lazily-established <see cref="IConnectionMultiplexer"/> (separate from SignalR's
/// internal connection, which is not exposed) configured to never abort on connect failure — so a transient outage
/// self-heals — and issues a single bounded <c>PING</c>. A successful PING means the backplane answered; a failure
/// throws and the <see cref="DependencyReadinessHealthCheck"/> treats it as not-ready (fail-closed). The connection
/// string is supplied from configuration only and is never logged (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md); the terms are the generic Redis/Valkey connection vocabulary (AGENTS.md).
/// </summary>
internal sealed class RealtimeBackplaneReadinessProbe : IDependencyReadinessProbe, IDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _multiplexer;

    /// <param name="connectionString">The Redis/Valkey-compatible backplane connection string.</param>
    /// <param name="probeTimeout">
    /// The short readiness timeout. It is mirrored onto the multiplexer's connect and async-command timeouts so a
    /// PING against an unreachable backplane fails fast within the bound (CORE-RES-005).
    /// </param>
    /// <exception cref="ArgumentException">The connection string is null or whitespace.</exception>
    public RealtimeBackplaneReadinessProbe(string connectionString, TimeSpan probeTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Defer parsing AND connecting to first use: a malformed connection string then surfaces as a probe
        // failure (not-ready) rather than throwing while the health check is constructed, which keeps a single
        // bad dependency from breaking the whole readiness check.
        _multiplexer = new Lazy<Task<IConnectionMultiplexer>>(
            () => ConnectAsync(connectionString, probeTimeout));
    }

    /// <inheritdoc />
    public string DependencyName => "realtime-backplane";

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        // Bounded by the readiness timeout while connecting; the multiplexer never aborts on connect failure, so
        // this returns promptly even against a dead backplane and the PING below surfaces the outage.
        var multiplexer = await _multiplexer.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        // PingAsync has no cancellation-token overload; its bound is the multiplexer's async timeout (set to the
        // readiness timeout). The health check additionally hard-bounds the whole probe, so a hung backplane can
        // never hold the readiness response open.
        await multiplexer.GetDatabase().PingAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_multiplexer.IsValueCreated && _multiplexer.Value.IsCompletedSuccessfully)
        {
            _multiplexer.Value.Result.Dispose();
        }
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(string connectionString, TimeSpan probeTimeout)
    {
        var options = ConfigurationOptions.Parse(connectionString);

        // Never abort on connect failure: ConnectAsync then returns even when the backplane is down (reconnecting
        // in the background), so a transient outage self-heals on a later probe instead of caching a faulted task.
        options.AbortOnConnectFail = false;

        // Mirror the short readiness timeout onto the connect and async-command bounds so a connect/PING against an
        // unreachable backplane fails fast within the readiness window (CORE-RES-005).
        var timeoutMilliseconds = Math.Max(1, (int)probeTimeout.TotalMilliseconds);
        options.ConnectTimeout = timeoutMilliseconds;
        options.AsyncTimeout = timeoutMilliseconds;
        options.ConnectRetry = 1;

        return await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }
}
