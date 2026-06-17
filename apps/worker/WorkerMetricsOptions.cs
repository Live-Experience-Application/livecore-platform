// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Worker;

/// <summary>
/// Configuration for the worker's metrics/health HTTP surface (CORE-DR-003). The worker records job-failure
/// metrics onto the shared <c>LiveCore</c> meter but, as a background host, used to expose no way to scrape
/// them. It now binds a small Kestrel listener that serves the Prometheus <c>/metrics</c> scrape endpoint
/// (mirroring the API's <c>/metrics</c>) and the per-loop <c>/health/live</c> endpoint.
///
/// The only knob is the listen URL — deployment policy, read once from configuration (under
/// <see cref="ConfigurationSection"/>) with a safe default so the worker runs without any metrics
/// configuration. No credentials or secrets live here (only a URL). The surface is unauthenticated by
/// convention (a Prometheus server scrapes it from inside the deployment network, exactly as orchestration
/// probes the API's <c>/health/*</c> and <c>/metrics</c>), so a deployment restricts it at the
/// reverse-proxy/network edge (docs/13_SELF_HOSTING_REQUIREMENTS.md); it carries only low-cardinality
/// aggregates and an overall status, never content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class WorkerMetricsOptions
{
    /// <summary>Configuration section the metrics surface settings are read from (<c>Worker:Metrics</c>).</summary>
    public const string ConfigurationSection = "Worker:Metrics";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the listen URL.</summary>
    public const string UrlKey = "Url";

    /// <summary>
    /// Default listen URL when none is configured: port 9464 (the conventional OpenTelemetry Prometheus
    /// exporter port) on all interfaces, so an in-network Prometheus server can scrape the container. A
    /// deployment overrides it through <c>Worker:Metrics:Url</c> and restricts the surface at the network edge.
    /// </summary>
    public const string DefaultUrl = "http://0.0.0.0:9464";

    /// <summary>Creates metrics-surface options.</summary>
    /// <param name="url">The URL the worker's metrics/health listener binds to.</param>
    /// <exception cref="ArgumentException">The URL is null, empty, whitespace or not a valid absolute HTTP(S) URL.</exception>
    public WorkerMetricsOptions(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("The worker metrics URL must be a non-empty value.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"The worker metrics URL '{url}' must be an absolute http(s) URL.", nameof(url));
        }

        Url = url;
    }

    /// <summary>The URL the worker's metrics/health listener binds to.</summary>
    public string Url { get; }

    /// <summary>
    /// Reads the metrics-surface options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Worker:Metrics:Url</c>), falling back to <see cref="DefaultUrl"/> when the value is absent or blank.
    /// A value that is PRESENT but malformed is rejected at startup rather than silently falling back.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">A present URL value is malformed.</exception>
    public static WorkerMetricsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection(ConfigurationSection)[UrlKey];
        return new WorkerMetricsOptions(string.IsNullOrWhiteSpace(configured) ? DefaultUrl : configured);
    }
}
