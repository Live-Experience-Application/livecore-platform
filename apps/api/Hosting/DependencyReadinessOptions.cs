// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Hosting;

/// <summary>
/// The short, configurable per-probe timeout that bounds the deep readiness reachability checks
/// (CORE-OBS-009, the "Observability Depth" epic; the timeout half pairs with CORE-RES-005). The deep
/// readiness checks make a LIVE outbound probe of each configured critical dependency — the OIDC discovery
/// endpoint, the object-storage backend and the realtime backplane — so a host with a valid database but a
/// dead critical dependency leaves the ready rotation instead of reporting Ready
/// (<see cref="DependencyReadinessHealthCheck"/>). Those probes cross the network, so each one MUST be bounded:
/// an unreachable or hung dependency has to fail the probe FAST rather than stall the readiness response (which
/// orchestrators poll frequently), and the probe always fails CLOSED — a probe that times out is treated as
/// unreachable (not-ready), never as healthy.
///
/// The bound is read once from configuration (under <see cref="ConfigurationSection"/>) with a safe, short
/// default so a host runs without any readiness tuning, exactly like
/// <see cref="LiveCore.Api.IdentityAccess.OidcBackchannelOptions"/> and the asset-storage request bounds. The
/// value carries no secret (one timespan; threat T7 in docs/07_SECURITY_THREAT_MODEL.md) and is
/// product-neutral (AGENTS.md).
/// </summary>
internal sealed class DependencyReadinessOptions
{
    /// <summary>Configuration section the deep readiness settings are read from (<c>HealthChecks:Readiness</c>).</summary>
    public const string ConfigurationSection = "HealthChecks:Readiness";

    /// <summary>Configuration key under the section holding the per-probe timeout.</summary>
    public const string ProbeTimeoutKey = "ProbeTimeout";

    /// <summary>
    /// Default per-probe timeout (2 seconds) when none is configured: short and safe, so an unreachable critical
    /// dependency fails the readiness probe fast rather than holding the readiness response open. Readiness is
    /// polled often, so the bound is deliberately tight.
    /// </summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Creates the deep readiness options.</summary>
    /// <param name="probeTimeout">The per-probe reachability timeout. Must be strictly positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not strictly positive.</exception>
    public DependencyReadinessOptions(TimeSpan probeTimeout)
    {
        if (probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout), probeTimeout, "The deep readiness probe timeout must be positive.");
        }

        ProbeTimeout = probeTimeout;
    }

    /// <summary>The per-probe reachability timeout. Each dependency probe is bounded by this, fail-closed.</summary>
    public TimeSpan ProbeTimeout { get; }

    /// <summary>The safe default, used when no readiness tuning is configured.</summary>
    public static DependencyReadinessOptions Default { get; } = new(DefaultProbeTimeout);

    /// <summary>
    /// Reads the deep readiness options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>:ProbeTimeout</c>), falling back to the safe default when the value is absent or blank. A value that is
    /// PRESENT but cannot be parsed (or is out of range) is rejected at startup rather than silently falling back —
    /// a misconfigured bound is a startup error, never a degenerate (or removed) ceiling, exactly like the OIDC
    /// backchannel and asset-storage bounds (CORE-RES-005).
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present timeout value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured timeout value is out of range.</exception>
    public static DependencyReadinessOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var value = section[ProbeTimeoutKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{ProbeTimeoutKey}' is not a valid TimeSpan.");
        }

        return new DependencyReadinessOptions(parsed);
    }
}
