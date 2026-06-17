// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="SecurityHeadersConfiguration.SecurityHeadersSettings"/> (CORE-SEC-009) — the
/// baseline security-header settings read from <c>SecurityHeaders:*</c> and applied to a response. They pin the
/// ON-by-default posture, the documented default directive values, the individual per-header toggles, the
/// blank-value fallback, the null guards, and that <see cref="SecurityHeadersConfiguration.SecurityHeadersSettings.Apply"/>
/// writes exactly the enabled headers. The end-to-end behavior (every API/error/negotiate response carries them,
/// a 404/406/500 included) is covered over real HTTP by the integration suite.
/// </summary>
public class SecurityHeadersConfigurationTests
{
    [Fact]
    public void Read_defaults_to_on_with_the_documented_directives()
    {
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder().Build());

        Assert.True(settings.Enabled);
        Assert.True(settings.ContentTypeOptionsEnabled);
        Assert.True(settings.ReferrerPolicyEnabled);
        Assert.True(settings.ContentSecurityPolicyEnabled);

        Assert.Equal(SecurityHeadersConfiguration.DefaultContentTypeOptions, settings.ContentTypeOptions);
        Assert.Equal(SecurityHeadersConfiguration.DefaultReferrerPolicy, settings.ReferrerPolicy);
        Assert.Equal(SecurityHeadersConfiguration.DefaultContentSecurityPolicy, settings.ContentSecurityPolicy);

        // The deny-all CSP shape the story documents.
        Assert.Equal("nosniff", settings.ContentTypeOptions);
        Assert.Equal("no-referrer", settings.ReferrerPolicy);
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", settings.ContentSecurityPolicy);
    }

    [Fact]
    public void Read_honors_the_per_header_and_feature_toggles()
    {
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SecurityHeaders:Enabled"] = "false",
                    ["SecurityHeaders:ContentTypeOptions:Enabled"] = "false",
                    ["SecurityHeaders:ReferrerPolicy:Enabled"] = "true",
                    ["SecurityHeaders:ContentSecurityPolicy:Enabled"] = "false",
                })
                .Build());

        Assert.False(settings.Enabled);
        Assert.False(settings.ContentTypeOptionsEnabled);
        Assert.True(settings.ReferrerPolicyEnabled);
        Assert.False(settings.ContentSecurityPolicyEnabled);
    }

    [Fact]
    public void Read_allows_overriding_a_header_value()
    {
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SecurityHeaders:ReferrerPolicy:Value"] = "same-origin",
                })
                .Build());

        Assert.Equal("same-origin", settings.ReferrerPolicy);
        // The others keep their defaults.
        Assert.Equal(SecurityHeadersConfiguration.DefaultContentTypeOptions, settings.ContentTypeOptions);
        Assert.Equal(SecurityHeadersConfiguration.DefaultContentSecurityPolicy, settings.ContentSecurityPolicy);
    }

    [Fact]
    public void Read_falls_back_to_the_default_for_a_blank_configured_value()
    {
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SecurityHeaders:ContentSecurityPolicy:Value"] = "   ",
                })
                .Build());

        // A blank value never emits an empty header — it falls back to the documented default.
        Assert.Equal(SecurityHeadersConfiguration.DefaultContentSecurityPolicy, settings.ContentSecurityPolicy);
    }

    [Fact]
    public void Read_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => SecurityHeadersConfiguration.SecurityHeadersSettings.Read(null!));
    }

    [Fact]
    public void Apply_writes_exactly_the_enabled_headers()
    {
        var settings = new SecurityHeadersConfiguration.SecurityHeadersSettings(
            Enabled: true,
            ContentTypeOptionsEnabled: true,
            ContentTypeOptions: "nosniff",
            ReferrerPolicyEnabled: false,
            ReferrerPolicy: "no-referrer",
            ContentSecurityPolicyEnabled: true,
            ContentSecurityPolicy: "default-src 'none'; frame-ancestors 'none'");

        var headers = new HeaderDictionary();
        settings.Apply(headers);

        // The two enabled headers are present with their exact values; the disabled one is absent.
        Assert.Equal("nosniff", headers[SecurityHeadersConfiguration.ContentTypeOptionsHeaderName].ToString());
        Assert.Equal(
            "default-src 'none'; frame-ancestors 'none'",
            headers[SecurityHeadersConfiguration.ContentSecurityPolicyHeaderName].ToString());
        Assert.False(headers.ContainsKey(SecurityHeadersConfiguration.ReferrerPolicyHeaderName));
    }

    [Fact]
    public void Apply_replaces_an_existing_header_value()
    {
        var settings = new SecurityHeadersConfiguration.SecurityHeadersSettings(
            Enabled: true,
            ContentTypeOptionsEnabled: true,
            ContentTypeOptions: "nosniff",
            ReferrerPolicyEnabled: true,
            ReferrerPolicy: "no-referrer",
            ContentSecurityPolicyEnabled: true,
            ContentSecurityPolicy: "default-src 'none'; frame-ancestors 'none'");

        var headers = new HeaderDictionary
        {
            [SecurityHeadersConfiguration.ContentTypeOptionsHeaderName] = "sniff-everything",
        };
        settings.Apply(headers);

        // The baseline value replaces a pre-existing one (a single, exact value — not appended).
        Assert.Equal("nosniff", headers[SecurityHeadersConfiguration.ContentTypeOptionsHeaderName].ToString());
    }

    [Fact]
    public void Apply_rejects_a_null_headers_dictionary()
    {
        var settings = SecurityHeadersConfiguration.SecurityHeadersSettings.Read(
            new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentNullException>(() => settings.Apply(null!));
    }
}
