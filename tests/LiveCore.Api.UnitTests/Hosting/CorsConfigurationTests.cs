// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="CorsConfiguration.ReadAllowedOrigins"/> (CORE-OPS-003) — the configured
/// browser/PWA allow-list read from <c>Cors:AllowedOrigins</c>. They pin the FAIL-CLOSED default (no
/// configuration ⇒ no origin allowed), the normalization (trim, drop trailing slash, drop blanks,
/// case-insensitive de-duplication) and the null guard. The end-to-end preflight behavior (an allowed
/// origin succeeds, a disallowed origin is rejected, the same policy on the SignalR hub) is covered over
/// real HTTP by the integration suite.
/// </summary>
public class CorsConfigurationTests
{
    [Fact]
    public void ReadAllowedOrigins_is_empty_when_unconfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var origins = CorsConfiguration.ReadAllowedOrigins(configuration);

        // Fail-closed: nothing configured means no cross-origin browser client is allowed.
        Assert.Empty(origins);
    }

    [Fact]
    public void ReadAllowedOrigins_is_empty_when_the_configured_list_is_only_blanks()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "",
                ["Cors:AllowedOrigins:1"] = "   ",
            })
            .Build();

        var origins = CorsConfiguration.ReadAllowedOrigins(configuration);

        Assert.Empty(origins);
    }

    [Fact]
    public void ReadAllowedOrigins_reads_normalizes_and_deduplicates_the_configured_list()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "  https://app.example  ",
                ["Cors:AllowedOrigins:1"] = "https://app.example/",
                ["Cors:AllowedOrigins:2"] = "HTTPS://APP.EXAMPLE",
                ["Cors:AllowedOrigins:3"] = "https://admin.example",
            })
            .Build();

        var origins = CorsConfiguration.ReadAllowedOrigins(configuration);

        // Trimmed, trailing slash removed, and the three spellings of the first origin
        // collapse to one (case-insensitive), leaving two distinct origins.
        Assert.Equal(["https://app.example", "https://admin.example"], origins);
    }

    [Fact]
    public void ReadAllowedOrigins_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => CorsConfiguration.ReadAllowedOrigins(null!));
    }

    [Fact]
    public void ExposedResponseHeaders_is_the_documented_browser_consumer_set()
    {
        // CORE-DX-005 / CORE-DX-006: the exact headers a cross-origin browser SDK must be able to read. Pinned so
        // the set (and the agreement with @livecore/contracts ResponseHeaders and docs/08) cannot drift
        // unnoticed. The end-to-end Access-Control-Expose-Headers behavior is covered by the integration suite.
        Assert.Equal(
            [
                "ETag",
                "Location",
                "Retry-After",
                "X-Request-Id",
                "traceparent",
                "RateLimit-Limit",
                "RateLimit-Remaining",
                "RateLimit-Reset",
                "Deprecation",
                "Sunset",
            ],
            CorsConfiguration.ExposedResponseHeaders);
    }
}
