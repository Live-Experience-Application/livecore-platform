using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Forwarded-headers wiring for running behind a reverse proxy / TLS-terminating load balancer
/// (CORE-OPS-003, the "Production Operations Readiness" epic). In production the Core API runs behind a
/// proxy that terminates TLS and forwards the request over plain HTTP on an internal network
/// (docs/02_ARCHITECTURE.md deployment options; docs/13_SELF_HOSTING_REQUIREMENTS.md). Without this the
/// app would see the proxy's scheme/host/IP (<c>http</c>, the internal hop) rather than the real client's
/// — breaking absolute URL generation, the OIDC <c>RequireHttpsMetadata</c> posture and per-client
/// diagnostics.
///
/// <c>UseForwardedHeaders</c> rewrites <c>Request.Scheme</c>, <c>Request.Host</c> and the remote IP
/// from the proxy's <c>X-Forwarded-Proto</c> / <c>X-Forwarded-Host</c> / <c>X-Forwarded-For</c> headers,
/// but ONLY when the immediate peer is a TRUSTED proxy. A forwarded header is attacker-controlled if any
/// untrusted host could set it (a spoofed <c>X-Forwarded-Proto: https</c> would make the app believe an
/// http request was secure), so the framework honors these headers only from
/// <see cref="ForwardedHeadersOptions.KnownProxies"/> / <see cref="ForwardedHeadersOptions.KnownIPNetworks"/>.
/// Loopback is trusted by the framework default; any other proxy (a container-network ingress, a managed
/// load balancer) is added from configuration (<c>ForwardedHeaders:KnownProxies</c> /
/// <c>ForwardedHeaders:KnownNetworks</c>) — never hardcoded, so a deployment names exactly the hops it
/// trusts. With nothing configured only loopback is trusted, so an arbitrary client cannot spoof the
/// scheme (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: do not trust unattested request metadata).
/// </summary>
public static class ForwardedHeadersConfiguration
{
    /// <summary>Configuration section that carries the forwarded-headers settings (<c>ForwardedHeaders</c>).</summary>
    public const string ConfigurationSection = "ForwardedHeaders";

    /// <summary>
    /// Registers the <see cref="ForwardedHeadersOptions"/> the <c>UseForwardedHeaders</c> middleware reads,
    /// configured from <see cref="ConfigurationSection"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ForwardedHeadersOptions>(options => Apply(configuration, options));
        return services;
    }

    /// <summary>
    /// Applies the configured forwarded-headers posture to <paramref name="options"/>: process the
    /// proxy's scheme/host/for headers, and trust them only from the configured known proxies/networks
    /// (in addition to the framework's loopback default). Exposed so the wiring is testable against the
    /// exact options the host runs with.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or options is null.</exception>
    /// <exception cref="ArgumentException">A configured proxy or network is not a valid IP / CIDR.</exception>
    public static void Apply(IConfiguration configuration, ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        // Honor the real client's scheme, host and IP a reverse proxy forwards.
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        var section = configuration.GetSection(ConfigurationSection);

        foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(proxy))
            {
                continue;
            }

            if (!IPAddress.TryParse(proxy.Trim(), out var address))
            {
                throw new ArgumentException(
                    $"Configured {ConfigurationSection}:KnownProxies entry '{proxy}' is not a valid IP address.",
                    nameof(configuration));
            }

            options.KnownProxies.Add(address);
        }

        foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                continue;
            }

            options.KnownIPNetworks.Add(ParseNetwork(network.Trim(), nameof(configuration)));
        }

        // Allow a deployment with more than one trusted hop to raise the forward limit
        // (the framework default is one). Left at the default when unset.
        var forwardLimit = section.GetValue<int?>("ForwardLimit");
        if (forwardLimit.HasValue)
        {
            options.ForwardLimit = forwardLimit.Value;
        }
    }

    /// <summary>
    /// Parses a CIDR string (for example <c>10.0.0.0/8</c>) into the trusted-network type the
    /// forwarded-headers middleware expects.
    /// </summary>
    private static IPNetwork ParseNetwork(string cidr, string parameterName)
    {
        if (IPNetwork.TryParse(cidr, out var network))
        {
            return network;
        }

        throw new ArgumentException(
            $"Configured {ConfigurationSection}:KnownNetworks entry '{cidr}' is not a valid CIDR network.",
            parameterName);
    }
}
