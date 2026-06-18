// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using StackExchange.Redis;

namespace LiveCore.Api.Persistence;

/// <summary>
/// The Valkey/Redis-backed <see cref="IAuthorizationCacheInvalidationBackplane"/> (CORE-RES-007). It owns a
/// dedicated <see cref="IConnectionMultiplexer"/> to the deployment's configured backplane (the same Valkey/Redis
/// server the realtime scale-out uses; <see cref="AuthorizationCacheInvalidationOptions"/>) and carries the local
/// cache invalidations to the other API replicas over a single pub/sub channel:
/// <list type="bullet">
///   <item><see cref="Publish"/> publishes the invalidation-group token (a surrogate id, never content — threat T7)
///   FIRE-AND-FORGET, so a slow or unreachable backplane never blocks the request that revoked access; a publish
///   failure is swallowed and the peer replicas fall back to the TTL backstop (no new fail-open path).</item>
///   <item><see cref="Subscribe"/> registers the receive handler the <see cref="AuthorizationCacheInvalidationListener"/>
///   supplies; StackExchange.Redis re-establishes the subscription automatically across reconnects.</item>
/// </list>
///
/// <para>
/// FAIL-SAFE STARTUP. The multiplexer connects with <c>AbortOnConnectFail = false</c>, mirroring the realtime
/// SignalR backplane: a briefly-unreachable backplane never fails host startup; it connects (and re-subscribes) in
/// the background, and until it does the cache simply relies on the local eviction plus the TTL backstop. This is
/// the only type that touches the Redis client directly — the publish/subscribe LOGIC and the loop-prevention live
/// behind the seam in <see cref="AuthorizationLookupCache"/> and the listener, which are exercised against an
/// in-memory fake; this transport is covered end-to-end by the real-backplane integration test.
/// </para>
/// </summary>
internal sealed class RedisAuthorizationCacheInvalidationBackplane : IAuthorizationCacheInvalidationBackplane, IDisposable
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisChannel _channel;

    public RedisAuthorizationCacheInvalidationBackplane(AuthorizationCacheInvalidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "A backplane connection string is required for cross-instance authorization-cache invalidation.",
                nameof(options));
        }

        var configuration = ConfigurationOptions.Parse(options.ConnectionString);

        // Fail-safe: do not block or fail host startup if the backplane is briefly unreachable. The multiplexer
        // connects (and the subscription re-establishes) in the background; until then the local eviction plus the
        // TTL backstop keep the cache correct (the realtime backplane uses the same posture).
        configuration.AbortOnConnectFail = false;

        _multiplexer = ConnectionMultiplexer.Connect(configuration);
        _channel = RedisChannel.Literal(options.Channel);
    }

    /// <summary>Internal constructor for tests: injects an already-built multiplexer (e.g. a shared test server connection).</summary>
    internal RedisAuthorizationCacheInvalidationBackplane(IConnectionMultiplexer multiplexer, string channel)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _multiplexer = multiplexer;
        _channel = RedisChannel.Literal(channel);
    }

    /// <inheritdoc />
    public void Publish(string invalidationGroup)
    {
        ArgumentException.ThrowIfNullOrEmpty(invalidationGroup);

        try
        {
            // Fire-and-forget: the local eviction already happened, so the broadcast is best-effort. A peer that
            // misses it falls back to the TTL backstop.
            _multiplexer.GetSubscriber().Publish(_channel, invalidationGroup, CommandFlags.FireAndForget);
        }
        catch (RedisException)
        {
            // Best-effort broadcast: never turn a successful local revocation into a request error.
        }
        catch (ObjectDisposedException)
        {
            // The multiplexer was disposed during shutdown; nothing to broadcast to.
        }
    }

    /// <inheritdoc />
    public void Subscribe(Action<string> onRemoteInvalidation)
    {
        ArgumentNullException.ThrowIfNull(onRemoteInvalidation);

        try
        {
            _multiplexer.GetSubscriber().Subscribe(
                _channel,
                (_, value) =>
                {
                    if (!value.IsNullOrEmpty)
                    {
                        onRemoteInvalidation(value.ToString());
                    }
                });
        }
        catch (RedisException)
        {
            // Fail-safe startup: the backplane may be briefly unreachable when the host starts (a rolling deploy),
            // so this must never fail host startup. The handler is registered in the subscription map before the send
            // is attempted, so StackExchange.Redis re-establishes the subscription automatically once connected; until
            // then this replica simply relies on the local eviction plus the TTL backstop (no new fail-open path).
        }
        catch (ObjectDisposedException)
        {
            // The multiplexer was disposed during shutdown.
        }
    }

    public void Dispose() => _multiplexer.Dispose();
}
