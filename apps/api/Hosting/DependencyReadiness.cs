// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.S3;
using LiveCore.Api.Assets;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Realtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.Hosting;

/// <summary>
/// A single LIVE reachability probe of one critical dependency (CORE-OBS-009, the "Observability Depth" epic).
/// Unlike the startup-captured config-presence gate (<see cref="RequiredDependencyReadiness"/>, which only
/// records whether a dependency is CONFIGURED), a probe makes a real, bounded outbound call EACH TIME readiness
/// is evaluated, so a host whose dependency is configured but currently DEAD is detected and taken out of the
/// ready rotation rather than continuing to advertise Ready.
///
/// A probe COMPLETES NORMALLY when the dependency is reachable and THROWS when it is not; the
/// <see cref="DependencyReadinessHealthCheck"/> bounds every probe by a short timeout and treats any throw —
/// including a timeout — as not-ready (fail-closed). The implementation must honor the supplied
/// <see cref="CancellationToken"/> so the bound can cancel a hung call promptly.
/// </summary>
internal interface IDependencyReadinessProbe
{
    /// <summary>
    /// A stable, generic, secret-free name for the dependency (for example <c>oidc-discovery</c>). It is used
    /// only in the server-side failure description and never carries an endpoint, connection string or any other
    /// sensitive value (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    string DependencyName { get; }

    /// <summary>
    /// Probes the dependency for live reachability. Returns when it is reachable; throws when it is not. The
    /// caller bounds this call by a short timeout (CORE-RES-005) via <paramref name="cancellationToken"/> and
    /// treats any exception as not-ready (fail-closed), so an implementation should make a single, cheap call and
    /// let failures propagate rather than swallow them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe when the readiness timeout elapses.</param>
    Task ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> (tagged <see cref="HealthEndpoints.ReadinessTag"/>) that runs the
/// configured deep dependency probes (CORE-OBS-009). It is the live counterpart to the database connectivity
/// check and the startup config-presence gate (<see cref="RequiredDependencyReadiness"/>): it probes the OIDC
/// discovery endpoint, the object-storage backend and the realtime backplane for ACTUAL reachability, so a host
/// with a healthy database but a dead critical dependency reports NOT-READY (503 on <c>/health/ready</c>) and
/// leaves the rotation instead of taking traffic it cannot serve. Liveness (<c>/health/live</c>) is separate and
/// shallow — it runs no checks — so a dead dependency never restarts an otherwise-live process.
///
/// <para>
/// BOUNDED AND FAIL-CLOSED (CORE-RES-005). Each probe runs concurrently and is HARD-bounded by the short,
/// configurable <see cref="DependencyReadinessOptions.ProbeTimeout"/>: a probe that throws OR exceeds the timeout
/// is counted as unreachable, so a slow or hung dependency fails the readiness probe FAST and never stalls the
/// readiness response (which orchestrators poll frequently). The bound and the catch-all are why a probe can only
/// ever leave a host out of rotation, never falsely keep it in: any uncertainty resolves to not-ready.
/// </para>
///
/// <para>
/// STATUS-ONLY (threat T7). The failure description names which dependencies are unreachable for the server-side
/// structured log / health UI, but the unauthenticated readiness response carries ONLY the overall status
/// (<see cref="HealthEndpoints"/>), so neither the failing dependency nor any endpoint/connection detail leaks to
/// an anonymous caller — the same posture as the database check and the required-dependency gate.
/// </para>
///
/// <para>
/// When no dependency is configured the probe set is empty and the check is inert (Healthy), preserving the
/// existing local-development latitude of running with no OIDC/storage/backplane configured.
/// </para>
/// </summary>
internal sealed class DependencyReadinessHealthCheck : IHealthCheck
{
    /// <summary>
    /// Name the readiness check is registered under. Kept server-side (the readiness endpoint writes only the
    /// overall status), never leaked to the unauthenticated response.
    /// </summary>
    public const string CheckName = "dependency-reachability";

    private readonly IReadOnlyList<IDependencyReadinessProbe> _probes;
    private readonly TimeSpan _probeTimeout;

    public DependencyReadinessHealthCheck(
        IEnumerable<IDependencyReadinessProbe> probes,
        DependencyReadinessOptions options)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(options);

        _probes = probes.ToArray();
        _probeTimeout = options.ProbeTimeout;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_probes.Count == 0)
        {
            // No critical dependency is configured, so there is nothing to reach: stay inert (Ready), preserving
            // the local-development latitude of running without OIDC/storage/backplane.
            return HealthCheckResult.Healthy();
        }

        var outcomes = await Task.WhenAll(_probes.Select(probe => RunProbeAsync(probe, cancellationToken)));

        var unreachable = outcomes.Where(outcome => !outcome.Reachable).Select(outcome => outcome.Name).ToArray();
        if (unreachable.Length == 0)
        {
            return HealthCheckResult.Healthy();
        }

        // Generic, secret-free description: it is inspected server-side only and is never written to the
        // unauthenticated readiness response (threat T7).
        return HealthCheckResult.Unhealthy(
            $"Critical dependency unreachable: {string.Join(", ", unreachable)}.");
    }

    private async Task<(string Name, bool Reachable)> RunProbeAsync(
        IDependencyReadinessProbe probe,
        CancellationToken cancellationToken)
    {
        // Hard-bound this probe to the short timeout regardless of whether the probe honors cancellation, so a
        // misbehaving or hung dependency can never hold the readiness response open. A cooperative probe also sees
        // the linked token cancelled, so it can release its resources promptly.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTask = ProbeSafelyAsync(probe, timeoutSource.Token);
        var timeoutTask = Task.Delay(_probeTimeout, CancellationToken.None);

        var completed = await Task.WhenAny(probeTask, timeoutTask);
        if (completed != probeTask)
        {
            // Timed out: ask the probe to stop, observe its eventual result so it never surfaces as an unobserved
            // task exception, and report the dependency as unreachable (fail-closed).
            timeoutSource.Cancel();
            _ = probeTask.ContinueWith(
                static task => _ = task.Exception, TaskScheduler.Default);
            return (probe.DependencyName, false);
        }

        return (probe.DependencyName, await probeTask);
    }

    private static async Task<bool> ProbeSafelyAsync(
        IDependencyReadinessProbe probe,
        CancellationToken cancellationToken)
    {
        try
        {
            await probe.ProbeAsync(cancellationToken);
            return true;
        }
        catch
        {
            // Any failure — a transport error, a non-success response or a cancellation — means the dependency is
            // not reachable right now. Fail closed: report not-ready rather than guessing it is healthy.
            return false;
        }
    }
}

/// <summary>
/// Registers the deep dependency reachability readiness checks (CORE-OBS-009). It adds one LIVE probe per
/// CONFIGURED critical dependency — the OIDC discovery endpoint when an Authority is configured, the
/// object-storage backend when storage is configured, and the realtime backplane when a backplane connection
/// string is configured — plus the single readiness check (tagged <see cref="HealthEndpoints.ReadinessTag"/>)
/// that runs them, bounded and fail-closed. A dependency that is not configured is not probed: the
/// startup config-presence gate (<see cref="RequiredDependencyReadiness"/>) already covers the
/// unconfigured-in-production case, and an unconfigured optional dependency (the single-instance in-process
/// backplane, the fail-closed unconfigured storage) has nothing live to reach.
/// </summary>
internal static class DependencyReadinessExtensions
{
    /// <summary>
    /// Adds the deep dependency reachability readiness checks. Probes are registered only for the dependencies
    /// the host is actually configured to use, so the check matches the host's real dependency surface. The
    /// readiness check itself is registered unconditionally and stays inert (Ready) when no probe is configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddDeepDependencyReadinessChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = DependencyReadinessOptions.FromConfiguration(configuration);
        services.AddSingleton(options);

        // OIDC discovery reachability — only when an Authority is configured. Without one the host has the
        // fail-closed default scheme and there is no identity provider to reach.
        var authority = configuration.GetSection(OidcAuthenticationExtensions.ConfigurationSection)["Authority"];
        if (!string.IsNullOrWhiteSpace(authority))
        {
            services.AddSingleton<IDependencyReadinessProbe>(_ => new OidcDiscoveryReadinessProbe(authority));
        }

        // Object-storage reachability — only when storage is configured. Otherwise the fail-closed
        // UnconfiguredAssetStorage is in place and there is no backend to reach. Reuses the SAME IAmazonS3 the
        // storage adapter registered (CORE-OPS-006), so the probe exercises the deployment's real client.
        if (S3AssetStorageOptions.FromConfiguration(configuration).IsConfigured)
        {
            services.AddSingleton<IDependencyReadinessProbe>(
                serviceProvider => new ObjectStorageReadinessProbe(serviceProvider.GetRequiredService<IAmazonS3>()));
        }

        // Realtime backplane reachability — only when a backplane connection string is configured. Otherwise the
        // host runs the in-process single-instance backplane with nothing external to reach.
        var backplane = RealtimeBackplaneOptions.FromConfiguration(configuration);
        if (backplane.IsConfigured)
        {
            services.AddSingleton<IDependencyReadinessProbe>(
                _ => new RealtimeBackplaneReadinessProbe(backplane.ConnectionString!, options.ProbeTimeout));
        }

        services.AddHealthChecks().AddCheck<DependencyReadinessHealthCheck>(
            DependencyReadinessHealthCheck.CheckName,
            failureStatus: HealthStatus.Unhealthy,
            tags: [HealthEndpoints.ReadinessTag]);

        return services;
    }
}
