using System.Globalization;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Graceful-shutdown drain window for both hosts (CORE-DEP-002, the "Deployment and Supply Chain" epic). On a
/// rolling restart an orchestrator sends the old instance a termination signal; the host then stops accepting
/// new work and DRAINS its in-flight work before exiting. <see cref="Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout"/>
/// is the bound on that drain: how long the host waits for in-flight HTTP requests and SignalR connections to
/// complete (API) and for each background job loop's current tick to observe cancellation and unwind (worker)
/// before shutdown is forced. Both hosts previously left it at the implicit framework default — an untuned,
/// undocumented window not coordinated with the deployment's termination grace period — so this gives them ONE
/// explicit, tuned, configurable drain window.
///
/// The single knob is the drain window, read once from configuration (under <see cref="ConfigurationSection"/>)
/// with a safe default so both hosts run without any shutdown configuration, exactly like the worker's
/// heartbeat/metrics options and <see cref="LiveCore.Api.Assets.AssetCleanupOptions"/>. No credentials or
/// secrets live here (only a timespan), and the value is product-neutral (AGENTS.md).
///
/// Coordination with the orchestration grace period is a deployment concern documented in
/// docs/13_SELF_HOSTING_REQUIREMENTS.md: the drain window must stay at or below the orchestrator's termination
/// grace period (for example Kubernetes <c>terminationGracePeriodSeconds</c>, default 30s) so the process exits
/// cleanly before it is force-killed; the default below is deliberately a few seconds under that conventional
/// 30s so there is headroom for the runtime to flush and exit. A deployment that needs a longer drain raises
/// both in lockstep.
/// </summary>
public sealed class GracefulShutdownOptions
{
    /// <summary>Configuration section the shutdown settings are read from (<c>Hosting</c>).</summary>
    public const string ConfigurationSection = "Hosting";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the drain window.</summary>
    public const string ShutdownTimeoutKey = "ShutdownTimeout";

    /// <summary>
    /// Default drain window (25 seconds) when none is configured. Deliberately a few seconds below the
    /// conventional 30-second orchestration termination grace period (for example Kubernetes
    /// <c>terminationGracePeriodSeconds</c>) so an in-flight HTTP/SignalR request or a background job tick has
    /// time to drain AND the process still exits cleanly before it is force-killed. A deployment tunes it
    /// through <c>Hosting:ShutdownTimeout</c> in lockstep with its own grace period.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Creates graceful-shutdown options.</summary>
    /// <param name="shutdownTimeout">The drain window the host waits for in-flight work before forcing shutdown.</param>
    /// <exception cref="ArgumentOutOfRangeException">The drain window is not positive.</exception>
    public GracefulShutdownOptions(TimeSpan shutdownTimeout)
    {
        if (shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownTimeout), shutdownTimeout, "The shutdown drain window must be positive.");
        }

        ShutdownTimeout = shutdownTimeout;
    }

    /// <summary>The drain window the host waits for in-flight work before forcing shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; }

    /// <summary>
    /// Reads the shutdown options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Hosting:ShutdownTimeout</c>), falling back to <see cref="DefaultShutdownTimeout"/> when the value is
    /// absent or blank. A value that is PRESENT but cannot be parsed (or is out of range) is rejected at startup
    /// rather than silently falling back — a misconfigured window is a startup error, never a degenerate drain.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present timeout value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured timeout value is out of range.</exception>
    public static GracefulShutdownOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection(ConfigurationSection)[ShutdownTimeoutKey];
        return new GracefulShutdownOptions(ParseShutdownTimeout(configured));
    }

    private static TimeSpan ParseShutdownTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultShutdownTimeout;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{ShutdownTimeoutKey}' is not a valid TimeSpan.");
        }

        return parsed;
    }
}

/// <summary>
/// Dependency-injection wiring for the graceful-shutdown drain window (CORE-DEP-002). Applies the tuned,
/// configurable <see cref="GracefulShutdownOptions.ShutdownTimeout"/> to the host's
/// <see cref="Microsoft.Extensions.Hosting.HostOptions"/>, so both the API and worker hosts drain in-flight
/// work within ONE explicit window on shutdown rather than relying on the implicit framework default.
/// </summary>
public static class GracefulShutdownServiceCollectionExtensions
{
    /// <summary>
    /// Reads <see cref="GracefulShutdownOptions"/> from configuration (<c>Hosting:ShutdownTimeout</c>) and
    /// applies the drain window to <see cref="Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout"/>. A
    /// present-but-malformed value is rejected at startup (see <see cref="GracefulShutdownOptions.FromConfiguration"/>);
    /// with nothing configured the safe default applies, so a host runs without any shutdown configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (<c>Hosting:ShutdownTimeout</c>).</param>
    /// <returns>The drain window applied, so a caller can log or assert it.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static TimeSpan AddLiveCoreGracefulShutdown(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = GracefulShutdownOptions.FromConfiguration(configuration);

        // Override HostOptions.ShutdownTimeout with the tuned window. This Configure runs after the host's
        // own default registration, so it wins: the host reads IOptions<HostOptions> at StopAsync time and
        // waits up to this window for in-flight HTTP/SignalR requests (API) and background job ticks (worker)
        // to drain before forcing shutdown.
        services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);

        return options.ShutdownTimeout;
    }
}
