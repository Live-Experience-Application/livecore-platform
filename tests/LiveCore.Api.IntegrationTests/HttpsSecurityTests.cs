// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Tests for the app-level HSTS and HTTPS-redirection posture (CORE-SEC-005, the "Audit Integrity and Security
/// Hardening" epic). They cover both the configuration parsing — pinned directly against
/// <see cref="HttpsSecurityConfiguration.HttpsSecuritySettings.Read"/>, exactly as <see cref="ForwardedHeadersTests"/>
/// pins <c>Apply</c> and <see cref="RateLimitingEndpointTests"/> pins <c>Read</c> — and the runtime behavior the
/// story's required tests name, driving the REAL framework middleware configured with the EXACT options the
/// running host produces:
/// <list type="bullet">
///   <item>with HSTS enabled a secure response carries <c>Strict-Transport-Security</c> (and an insecure one
///   does not, so the documented edge posture is never broken);</item>
///   <item>with redirection enabled an <c>http</c> request is redirected to <c>https</c> end-to-end over real
///   HTTP;</item>
///   <item>with the documented proxy posture (forwarded headers trusted) an <c>http</c> request the proxy
///   already terminated as <c>https</c> is NOT redirected — the default behavior is unchanged — and the
///   forwarded-headers handling itself is unregressed (the app still sees the real <c>https</c> scheme).</item>
/// </list>
/// Both toggles default OFF, so the unmodified host adds neither; the tests enable them through in-memory
/// configuration (the same override mechanism the CORS/forwarded-headers/rate-limiting tests use).
/// </summary>
public sealed class HttpsSecurityTests
{
    private const string _trustedProxy = "10.20.30.40";

    // ====================================================================
    // Configuration parsing (toggles + values are configurable; fail-safe).
    // ====================================================================

    [Fact]
    public void Read_defaults_disable_both_features()
    {
        var settings = HttpsSecurityConfiguration.HttpsSecuritySettings.Read(new ConfigurationBuilder().Build());

        // The documented default is the TLS-terminating proxy posture: the app adds nothing.
        Assert.False(settings.HstsEnabled);
        Assert.False(settings.HttpsRedirectionEnabled);
        Assert.Equal(HttpsSecurityConfiguration.DefaultHstsMaxAgeDays, settings.HstsMaxAgeDays);
        Assert.Equal(HttpsSecurityConfiguration.DefaultHttpsRedirectionStatusCode, settings.HttpsRedirectionStatusCode);
        Assert.Null(settings.HttpsRedirectionPort);
    }

    [Fact]
    public void Read_reflects_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpsSecurity:Hsts:Enabled"] = "true",
                ["HttpsSecurity:Hsts:MaxAgeDays"] = "730",
                ["HttpsSecurity:Hsts:IncludeSubDomains"] = "true",
                ["HttpsSecurity:Hsts:Preload"] = "true",
                ["HttpsSecurity:HttpsRedirection:Enabled"] = "true",
                ["HttpsSecurity:HttpsRedirection:StatusCode"] = "307",
                ["HttpsSecurity:HttpsRedirection:Port"] = "8443",
            })
            .Build();

        var settings = HttpsSecurityConfiguration.HttpsSecuritySettings.Read(configuration);

        Assert.True(settings.HstsEnabled);
        Assert.Equal(730, settings.HstsMaxAgeDays);
        Assert.True(settings.HstsIncludeSubDomains);
        Assert.True(settings.HstsPreload);
        Assert.True(settings.HttpsRedirectionEnabled);
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, settings.HttpsRedirectionStatusCode);
        Assert.Equal(8443, settings.HttpsRedirectionPort);
    }

    [Theory]
    [InlineData("HttpsSecurity:Hsts:MaxAgeDays", "0")]
    [InlineData("HttpsSecurity:Hsts:MaxAgeDays", "-30")]
    public void Read_falls_back_to_the_default_max_age_for_a_non_positive_value(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var settings = HttpsSecurityConfiguration.HttpsSecuritySettings.Read(configuration);

        Assert.Equal(HttpsSecurityConfiguration.DefaultHstsMaxAgeDays, settings.HstsMaxAgeDays);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("404")]
    [InlineData("0")]
    public void Read_falls_back_to_the_default_redirect_status_for_a_non_3xx_value(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpsSecurity:HttpsRedirection:StatusCode"] = value,
            })
            .Build();

        var settings = HttpsSecurityConfiguration.HttpsSecuritySettings.Read(configuration);

        // A misconfigured non-redirect status never produces a degenerate redirect; it falls back to the default.
        Assert.Equal(HttpsSecurityConfiguration.DefaultHttpsRedirectionStatusCode, settings.HttpsRedirectionStatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    public void Read_drops_an_out_of_range_redirect_port(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpsSecurity:HttpsRedirection:Port"] = value,
            })
            .Build();

        var settings = HttpsSecurityConfiguration.HttpsSecuritySettings.Read(configuration);

        Assert.Null(settings.HttpsRedirectionPort);
    }

    [Fact]
    public void Read_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => HttpsSecurityConfiguration.HttpsSecuritySettings.Read(null!));
    }

    // ====================================================================
    // HSTS: a secure response carries Strict-Transport-Security; an
    // insecure request does not (the framework's secure-only behavior).
    // ====================================================================

    [Fact]
    public async Task Hsts_enabled_adds_strict_transport_security_on_a_secure_response()
    {
        await using var factory = new HttpsSecuredApiFactory(new Dictionary<string, string?>
        {
            ["HttpsSecurity:Hsts:Enabled"] = "true",
            ["HttpsSecurity:Hsts:MaxAgeDays"] = "365",
            ["HttpsSecurity:Hsts:IncludeSubDomains"] = "true",
            ["HttpsSecurity:Hsts:Preload"] = "true",
        });
        var options = factory.Services.GetRequiredService<IOptions<HstsOptions>>();
        var middleware = new HstsMiddleware(_ => Task.CompletedTask, options, NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.Request.IsHttps = true;
        // A non-excluded public host (the framework excludes loopback by default).
        context.Request.Host = new HostString("api.public.example");

        await middleware.Invoke(context);

        var header = context.Response.Headers.StrictTransportSecurity.ToString();
        Assert.Contains("max-age=31536000", header, StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", header, StringComparison.Ordinal);
        Assert.Contains("preload", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_is_not_added_on_an_insecure_request()
    {
        await using var factory = new HttpsSecuredApiFactory(new Dictionary<string, string?>
        {
            ["HttpsSecurity:Hsts:Enabled"] = "true",
        });
        var options = factory.Services.GetRequiredService<IOptions<HstsOptions>>();
        var middleware = new HstsMiddleware(_ => Task.CompletedTask, options, NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.Request.IsHttps = false;
        context.Request.Host = new HostString("api.public.example");

        await middleware.Invoke(context);

        // HSTS is a directive for an already-secure channel; it is never emitted over plain http, so the edge
        // posture is not broken (an http request the proxy will redirect carries no stale directive).
        Assert.True(context.Response.Headers.StrictTransportSecurity.Count == 0);
    }

    // ====================================================================
    // HTTPS redirection: an http request is redirected end-to-end.
    // ====================================================================

    [Fact]
    public async Task Redirection_enabled_redirects_an_http_request_to_https()
    {
        await using var factory = new HttpsSecuredApiFactory(new Dictionary<string, string?>
        {
            ["HttpsSecurity:HttpsRedirection:Enabled"] = "true",
            // Pin the target port so the redirect is deterministic without a live https server address.
            ["HttpsSecurity:HttpsRedirection:Port"] = "443",
        });
        factory.ClientOptions.AllowAutoRedirect = false;
        using var client = factory.CreateAnonymousClient();

        // The redirect short-circuits before routing reaches any endpoint; /health/live is just a reachable path.
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("https://", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ====================================================================
    // Proxy posture: a request the trusted proxy already terminated as
    // https is NOT redirected (the documented default), and the
    // forwarded-headers handling is unregressed.
    // ====================================================================

    [Fact]
    public async Task A_forwarded_https_request_behind_a_trusted_proxy_is_not_redirected()
    {
        await using var factory = new HttpsSecuredApiFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = _trustedProxy,
            ["HttpsSecurity:HttpsRedirection:Enabled"] = "true",
            ["HttpsSecurity:HttpsRedirection:Port"] = "443",
        });

        var forwardedOptions = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var redirectionOptions = factory.Services.GetRequiredService<IOptions<HttpsRedirectionOptions>>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        // The pipeline order: UseForwardedHeaders restores the real scheme BEFORE UseHttpsRedirection runs.
        var forwardedHeaders = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask, NullLoggerFactory.Instance, forwardedOptions);

        var nextCalled = false;
        var redirection = new HttpsRedirectionMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            redirectionOptions,
            configuration,
            NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(_trustedProxy);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await forwardedHeaders.Invoke(context);
        await redirection.Invoke(context);

        // No regression to forwarded-headers handling: the trusted proxy's forwarded scheme is honored.
        Assert.Equal("https", context.Request.Scheme);
        // The documented proxy posture: the app sees https, so the redirect does not fire and the request
        // continues through the pipeline (no double-redirect, no fight with the edge).
        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that layers the supplied <c>HttpsSecurity:*</c> (and, for the proxy
    /// posture test, <c>ForwardedHeaders:*</c>) overrides on top of the production configuration, so a test can
    /// enable the toggles without weakening any other production wiring — exactly the pattern
    /// <see cref="RateLimitingEndpointTests"/> uses for its rate-limit overrides.
    /// </summary>
    private sealed class HttpsSecuredApiFactory(IReadOnlyDictionary<string, string?> overrides) : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
        }
    }
}
