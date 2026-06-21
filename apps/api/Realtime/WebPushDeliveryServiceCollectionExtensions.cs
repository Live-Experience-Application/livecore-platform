// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Dependency-injection wiring for the background closed-app push DISPATCH sweep (CORE-PUSH-002). The sweep runs in
/// the worker host (docs/02_ARCHITECTURE.md: the worker owns async jobs), a separate process from the API. Like the
/// scheduled-reveal wiring (<c>AddScheduledReveal</c>), this PUBLIC extension keeps the registration inside the
/// module that owns it: the worker calls <see cref="AddWebPushDelivery"/> and registers its own scheduling
/// <c>BackgroundService</c>, while the outbox repository, the push-subscription repository, the VAPID sender and the
/// <see cref="PushNotificationDispatchService"/> stay internal to the module.
///
/// GATED on a configured connection string AND an explicit opt-in WITH VAPID configured. The OUTBOUND-HTTP fan-out
/// is OFF BY DEFAULT (the story's headline requirement): this returns <see langword="false"/> and registers nothing
/// unless BOTH a database is configured AND <see cref="WebPushDeliveryOptions.IsActive"/> (delivery enabled and the
/// full VAPID signing material present). So a deployment that does not use closed-app push runs no dispatch sweep at
/// all (fail-safe, not fail-open). The shared infrastructure (the <see cref="LiveCoreDbContext"/>, the
/// <see cref="TimeProvider"/> and the repositories) is registered with TryAdd/AddDbContextPool semantics so it
/// composes safely when the worker also wires the other jobs in the same container. The VAPID PRIVATE key is read
/// from configuration only and never logged (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class WebPushDeliveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the closed-app push dispatch sweep's dependencies when a database connection string is configured
    /// (<c>ConnectionStrings:Database</c>) AND delivery is enabled with VAPID configured
    /// (<c>WebPush:Delivery:Enabled=true</c> plus <c>WebPush:Vapid:PublicKey</c>/<c>:PrivateKey</c>/<c>:Subject</c>).
    /// Returns whether the sweep — and therefore the scheduling background service — should be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (connection string + <c>WebPush:*</c> settings).</param>
    /// <returns><see langword="true"/> when persistence is configured, delivery is active, and its services were registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddWebPushDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No database configured: the worker runs without persistence and registers no sweep. Fail-safe.
            return false;
        }

        var options = WebPushDeliveryOptions.FromConfiguration(configuration);
        if (!options.IsActive)
        {
            // OFF BY DEFAULT: the outbound-HTTP fan-out is opt-in and inert without VAPID configured. Not active ->
            // register no sweep.
            return false;
        }

        // DbContext POOLING (CORE-PERF-003), connection resilience (CORE-CONC-003) and per-command timeouts
        // (CORE-RES-004) via UseLiveCoreNpgsql — the same pooled, resilient registration the API host and the other
        // worker jobs use. AddDbContextPool registers with TryAdd semantics, so composing it with another job's
        // wiring (same connection string) is safe.
        services.AddDbContextPool<LiveCoreDbContext>(contextOptions =>
            contextOptions.UseLiveCoreNpgsql(connectionString, LiveCorePersistenceOptions.FromConfiguration(configuration)));

        // The system clock; the VAPID JWT stamps its expiry from it. TryAdd so it composes with the other jobs.
        services.TryAddSingleton(TimeProvider.System);

        // Deployment delivery policy (enablement, VAPID signing material, cadence, batch, TTL), read once with safe
        // defaults.
        services.AddSingleton(options);

        // The outbox + subscription repositories the sweep drains, scoped so each sweep runs in its own DbContext
        // scope (the background service creates a scope per run). TryAdd so they compose with other modules.
        services.TryAddScoped<IPushNotificationDeliveryRepository, PushNotificationDeliveryRepository>();
        services.TryAddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>();

        // The outbound VAPID sender as a typed HttpClient (Microsoft.Extensions.Http is part of the shared
        // framework — no new dependency), so it gets a pooled, factory-managed HttpClient.
        services.AddHttpClient<IWebPushSender, VapidWebPushSender>();

        // The dispatch application service the scheduling loop invokes.
        services.AddScoped<PushNotificationDispatchService>();

        return true;
    }
}
