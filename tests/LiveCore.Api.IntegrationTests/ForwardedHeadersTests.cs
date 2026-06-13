using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Tests for the forwarded-headers posture behind a TLS-terminating reverse proxy (CORE-OPS-003). They
/// assert the acceptance criterion "behind a reverse proxy the app sees the real scheme/host": the real
/// framework <see cref="ForwardedHeadersMiddleware"/>, configured with the EXACT options the running host
/// produces, honors a trusted proxy's <c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c> and — fail-closed —
/// ignores the same headers from an untrusted peer (a client cannot spoof the scheme; threat T7). The
/// configuration parsing itself (which proxies/networks are trusted, rejection of malformed values) is
/// pinned directly against <see cref="ForwardedHeadersConfiguration.Apply"/>.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private const string _trustedProxy = "10.20.30.40";
    private const string _untrustedPeer = "203.0.113.7";

    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    settings.ToDictionary(s => s.Key, s => (string?)s.Value))));
    }

    [Fact]
    public void Apply_processes_scheme_host_and_for_headers()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Apply(new ConfigurationBuilder().Build(), options);

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
    }

    [Fact]
    public void Apply_reads_known_proxies_networks_and_forward_limit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = _trustedProxy,
                ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
                ["ForwardedHeaders:ForwardLimit"] = "2",
            })
            .Build();
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Apply(configuration, options);

        Assert.Contains(IPAddress.Parse(_trustedProxy), options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("10.0.0.0/8"), options.KnownIPNetworks);
        Assert.Equal(2, options.ForwardLimit);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/not-a-mask")]
    public void Apply_rejects_a_malformed_value(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<ArgumentException>(
            () => ForwardedHeadersConfiguration.Apply(configuration, new ForwardedHeadersOptions()));
    }

    [Fact]
    public void Apply_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => ForwardedHeadersConfiguration.Apply(null!, new ForwardedHeadersOptions()));
    }

    [Fact]
    public async Task A_trusted_proxy_forwarded_scheme_and_host_are_respected()
    {
        await using var factory = CreateFactory(("ForwardedHeaders:KnownProxies:0", _trustedProxy));
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(_trustedProxy);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.23";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "api.public.example";

        await middleware.Invoke(context);

        // Behind a trusted proxy the app sees the real client's scheme and host.
        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal("api.public.example", context.Request.Host.Value);
    }

    [Fact]
    public async Task An_untrusted_peer_cannot_spoof_the_forwarded_scheme()
    {
        await using var factory = CreateFactory(("ForwardedHeaders:KnownProxies:0", _trustedProxy));
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);

        var context = new DefaultHttpContext();
        // The direct peer is NOT a known proxy (and not loopback), so its forwarded headers are ignored.
        context.Connection.RemoteIpAddress = IPAddress.Parse(_untrustedPeer);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example";

        await middleware.Invoke(context);

        // Fail-closed: a spoofed X-Forwarded-Proto from an untrusted peer does not make the app see https.
        Assert.Equal("http", context.Request.Scheme);
        Assert.NotEqual("attacker.example", context.Request.Host.Value);
    }
}
