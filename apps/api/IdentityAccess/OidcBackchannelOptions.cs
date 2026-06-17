// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Tunable, configurable bounds on the OIDC JWT-bearer BACKCHANNEL — the outbound HTTP the handler makes to the
/// identity provider to fetch and refresh the discovery document and signing keys (JWKS) it validates tokens
/// against (CORE-RES-005, the "Reliability and Fault Tolerance" epic). Before this the bearer pipeline set the
/// Authority only, so the backchannel kept the framework's 60-second default timeout and a slow or unreachable
/// OIDC provider could STALL token validation — which, with the global problem-details handler (CORE-RES-001),
/// surfaces to the caller as a 500. These bounds make a hung provider FAIL FAST instead.
///
/// All values are read once from configuration (under <see cref="OidcAuthenticationExtensions.ConfigurationSection"/>)
/// with safe defaults so a host runs without any OIDC tuning, exactly like
/// <see cref="LiveCore.Api.Persistence.LiveCorePersistenceOptions"/> and the asset-storage request bounds:
///
/// <list type="bullet">
///   <item><b><see cref="BackchannelTimeout"/></b> — the per-request timeout on the backchannel
///   <see cref="System.Net.Http.HttpClient"/> (JwtBearer <c>BackchannelTimeout</c>): how long the handler waits
///   for the provider's discovery/JWKS response before it gives up, so a slow or unreachable provider fails fast
///   rather than holding the request thread. Default 30 seconds (shorter than the framework's 60-second
///   default).</item>
///   <item><b><see cref="AutomaticRefreshInterval"/></b> — how often the cached OIDC configuration (the
///   discovery document and signing keys) is refreshed in the background (JwtBearer
///   <c>AutomaticRefreshInterval</c>), so a provider's key rotation is picked up within a bounded window. Default
///   6 hours (tuned shorter than the framework's 12-hour default).</item>
///   <item><b><see cref="RefreshInterval"/></b> — the minimum interval between forced configuration refreshes
///   (JwtBearer <c>RefreshInterval</c>), the floor that stops a burst of validation failures from hammering the
///   provider's metadata endpoint. Default 5 minutes.</item>
/// </list>
///
/// <para>
/// WHY THIS IS FAIL-CLOSED AND CONTAINED. The timeout bounds an OUTBOUND call Core owns; it never relaxes token
/// validation. A token whose signing key cannot be fetched within the window is still rejected (no token is
/// accepted on a backchannel failure), so the bound contains a slow dependency without widening access
/// (threats T1/T5). The unconfigured-Authority path keeps its fail-closed default scheme unchanged — there is no
/// backchannel to bound there. No secret lives here (two timespans and one more; threat T7), and the values are
/// product-neutral (AGENTS.md).
/// </para>
/// </summary>
internal sealed class OidcBackchannelOptions
{
    /// <summary>Configuration key under the OIDC section holding the backchannel request timeout.</summary>
    public const string BackchannelTimeoutKey = "BackchannelTimeout";

    /// <summary>Configuration key under the OIDC section holding the automatic configuration-refresh interval.</summary>
    public const string AutomaticRefreshIntervalKey = "AutomaticRefreshInterval";

    /// <summary>Configuration key under the OIDC section holding the minimum forced configuration-refresh interval.</summary>
    public const string RefreshIntervalKey = "RefreshInterval";

    /// <summary>
    /// Default backchannel request timeout (30 seconds) when none is configured: short and safe, shorter than the
    /// framework's 60-second default, so a slow OIDC provider fails fast rather than stalling token validation.
    /// </summary>
    public static readonly TimeSpan DefaultBackchannelTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default automatic configuration-refresh interval (6 hours) when none is configured: tuned shorter than the
    /// framework's 12-hour default so a provider's signing-key rotation is picked up within a bounded window.
    /// </summary>
    public static readonly TimeSpan DefaultAutomaticRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Default minimum forced-refresh interval (5 minutes) when none is configured: the floor that keeps a burst
    /// of validation failures from hammering the provider's metadata endpoint.
    /// </summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>Creates OIDC backchannel-tuning options.</summary>
    /// <param name="backchannelTimeout">The per-request backchannel timeout. Must be positive.</param>
    /// <param name="automaticRefreshInterval">The automatic configuration-refresh interval. Must be positive.</param>
    /// <param name="refreshInterval">The minimum forced configuration-refresh interval. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any interval is not strictly positive.</exception>
    public OidcBackchannelOptions(
        TimeSpan backchannelTimeout,
        TimeSpan automaticRefreshInterval,
        TimeSpan refreshInterval)
    {
        if (backchannelTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backchannelTimeout), backchannelTimeout, "The OIDC backchannel timeout must be positive.");
        }

        if (automaticRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(automaticRefreshInterval),
                automaticRefreshInterval,
                "The OIDC automatic refresh interval must be positive.");
        }

        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval), refreshInterval, "The OIDC refresh interval must be positive.");
        }

        BackchannelTimeout = backchannelTimeout;
        AutomaticRefreshInterval = automaticRefreshInterval;
        RefreshInterval = refreshInterval;
    }

    /// <summary>The per-request timeout on the backchannel <see cref="System.Net.Http.HttpClient"/>.</summary>
    public TimeSpan BackchannelTimeout { get; }

    /// <summary>How often the cached OIDC configuration (discovery document + signing keys) is refreshed.</summary>
    public TimeSpan AutomaticRefreshInterval { get; }

    /// <summary>The minimum interval between forced configuration refreshes.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>The safe defaults, used when no OIDC tuning is configured.</summary>
    public static OidcBackchannelOptions Default { get; } =
        new(DefaultBackchannelTimeout, DefaultAutomaticRefreshInterval, DefaultRefreshInterval);

    /// <summary>
    /// Reads the backchannel-tuning options from configuration under
    /// <see cref="OidcAuthenticationExtensions.ConfigurationSection"/> (<c>:BackchannelTimeout</c> /
    /// <c>:AutomaticRefreshInterval</c> / <c>:RefreshInterval</c>), falling back to the safe defaults when a value
    /// is absent or blank. A value that is PRESENT but cannot be parsed (or is out of range) is rejected at
    /// startup rather than silently falling back — a misconfigured bound is a startup error, never a degenerate
    /// (or removed) ceiling.
    /// </summary>
    /// <param name="section">The OIDC configuration section.</param>
    /// <exception cref="ArgumentNullException">The configuration section is null.</exception>
    /// <exception cref="InvalidOperationException">A present interval value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured interval value is out of range.</exception>
    public static OidcBackchannelOptions FromConfiguration(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var backchannelTimeout = ParseInterval(section[BackchannelTimeoutKey], BackchannelTimeoutKey, DefaultBackchannelTimeout);
        var automaticRefreshInterval = ParseInterval(
            section[AutomaticRefreshIntervalKey], AutomaticRefreshIntervalKey, DefaultAutomaticRefreshInterval);
        var refreshInterval = ParseInterval(section[RefreshIntervalKey], RefreshIntervalKey, DefaultRefreshInterval);
        return new OidcBackchannelOptions(backchannelTimeout, automaticRefreshInterval, refreshInterval);
    }

    private static TimeSpan ParseInterval(string? value, string key, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{OidcAuthenticationExtensions.ConfigurationSection}:{key}' is not a valid TimeSpan.");
        }

        return parsed;
    }
}
