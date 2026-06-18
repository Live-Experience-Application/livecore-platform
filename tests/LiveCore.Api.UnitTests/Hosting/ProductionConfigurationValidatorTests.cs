// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Tests for the production secret-management and configuration contract (CORE-OPS-008). The host holds no
/// secret in source — every required value is supplied from configuration — and these pin the pure decision
/// behind the loud startup diagnostic: in a Production environment a required setting that is absent (null or
/// whitespace) is reported as missing BY NAME (never by value, so a secret is never logged; threat T7), while
/// outside Production the contract is inert (the same local-development latitude the OIDC audience guard,
/// CORE-OPS-004, and the readiness gate, CORE-OPS-005, grant). The end-to-end "the host still starts and fails
/// closed" wiring is covered over the real <c>Program</c> by the smoke suite.
/// </summary>
public class ProductionConfigurationValidatorTests
{
    private const string _connectionString = "Host=db;Port=5432;Database=livecore;Username=livecore;Password=secret";
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";

    private static IConfiguration BuildConfiguration(
        string? connectionString = null,
        string? authority = null,
        string? audience = null,
        string[]? corsOrigins = null,
        string[]? knownNetworks = null)
    {
        var values = new Dictionary<string, string?>
        {
            [ProductionConfigurationValidator.DatabaseConnectionStringKey] = connectionString,
            [ProductionConfigurationValidator.OidcAuthorityKey] = authority,
            [ProductionConfigurationValidator.OidcAudienceKey] = audience,
        };

        for (var i = 0; corsOrigins is not null && i < corsOrigins.Length; i++)
        {
            values[$"{ProductionConfigurationValidator.CorsAllowedOriginsKey}:{i}"] = corsOrigins[i];
        }

        for (var i = 0; knownNetworks is not null && i < knownNetworks.Length; i++)
        {
            values[$"{ProductionConfigurationValidator.ForwardedHeadersKnownNetworksKey}:{i}"] = knownNetworks[i];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void All_required_settings_are_present_in_a_fully_configured_production_host()
    {
        // The config-validation acceptance criterion: with every required prod setting present, the contract
        // reports nothing missing (a properly configured host is never blocked by an over-strict contract).
        var configuration = BuildConfiguration(_connectionString, _authority, _audience);

        var missing = ProductionConfigurationValidator.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_required_setting_is_reported_missing_when_production_is_fully_unconfigured()
    {
        var configuration = BuildConfiguration();

        var missing = ProductionConfigurationValidator.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(
            new[]
            {
                ProductionConfigurationValidator.DatabaseConnectionStringKey,
                ProductionConfigurationValidator.OidcAuthorityKey,
                ProductionConfigurationValidator.OidcAudienceKey,
            },
            missing);
    }

    [Theory]
    [InlineData(ProductionConfigurationValidator.DatabaseConnectionStringKey)]
    [InlineData(ProductionConfigurationValidator.OidcAuthorityKey)]
    [InlineData(ProductionConfigurationValidator.OidcAudienceKey)]
    public void A_single_missing_required_setting_is_reported_by_name_in_production(string missingKey)
    {
        // Start fully configured, then blank exactly one required value.
        var configuration = BuildConfiguration(
            connectionString: missingKey == ProductionConfigurationValidator.DatabaseConnectionStringKey ? null : _connectionString,
            authority: missingKey == ProductionConfigurationValidator.OidcAuthorityKey ? null : _authority,
            audience: missingKey == ProductionConfigurationValidator.OidcAudienceKey ? null : _audience);

        var missing = ProductionConfigurationValidator.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { missingKey }, missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_whitespace_required_value_is_treated_as_missing_in_production(string blank)
    {
        // A present-but-blank value silently disables the dependency, so it is treated as missing — the same
        // null-or-whitespace rule the OIDC audience guard applies (CORE-OPS-004).
        var configuration = BuildConfiguration(_connectionString, _authority, audience: blank);

        var missing = ProductionConfigurationValidator.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { ProductionConfigurationValidator.OidcAudienceKey }, missing);
    }

    [Fact]
    public void The_contract_is_inert_outside_production_even_when_fully_unconfigured()
    {
        // Local-development latitude: a Development run with none of the required values configured reports
        // nothing missing, so the host starts and fails closed rather than being blocked.
        var configuration = BuildConfiguration();

        var missing = ProductionConfigurationValidator.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: false);

        Assert.Empty(missing);
    }

    [Fact]
    public void Required_production_settings_are_the_documented_contract()
    {
        // The published contract (the .env.example and docs/13 table) must stay in step with the code: these
        // three keys are the always-required production values.
        Assert.Equal(
            new[]
            {
                "ConnectionStrings:Database",
                "Authentication:Oidc:Authority",
                "Authentication:Oidc:Audience",
            },
            ProductionConfigurationValidator.RequiredProductionSettings);
    }

    // ----- Well-formedness contract (CORE-OPS-013) -----

    private const string _wellFormedOrigin = "https://app.example.com";

    [Fact]
    public void A_well_formed_production_configuration_reports_nothing_malformed()
    {
        // The well-formedness acceptance criterion: with every critical value present AND well-formed, the
        // contract reports nothing malformed (a properly configured host is never blocked by an over-strict check).
        var configuration = BuildConfiguration(
            _connectionString,
            _authority,
            _audience,
            corsOrigins: ["https://app.example.com", "https://admin.example.com:8443", "http://localhost:3000/"],
            knownNetworks: ["10.0.0.0/8", "2001:db8::/32"]);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(malformed);
    }

    [Theory]
    [InlineData("Host=db;Port=not-a-number")] // a typed keyword (Port) that cannot be coerced
    [InlineData("NotARealKeyword=value")] // an unknown connection-string keyword
    public void A_malformed_connection_string_is_reported_by_name_only(string malformedConnectionString)
    {
        var configuration = BuildConfiguration(malformedConnectionString, _authority, _audience);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { ProductionConfigurationValidator.DatabaseConnectionStringKey }, malformed);
        // By name only: the offending VALUE is never part of the reported result (threat T7).
        Assert.DoesNotContain(malformed, key => key.Contains(malformedConnectionString, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("issuer.example.com")] // host only: relative, not absolute
    [InlineData("/realm/livecore")] // a relative path
    [InlineData("not a uri")] // not a URI at all
    [InlineData("ftp://issuer.example.com")] // absolute but not an http(s) issuer
    public void A_non_absolute_or_non_http_authority_is_reported_by_name_only(string malformedAuthority)
    {
        var configuration = BuildConfiguration(_connectionString, malformedAuthority, _audience);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { ProductionConfigurationValidator.OidcAuthorityKey }, malformed);
        Assert.DoesNotContain(malformed, key => key.Contains(malformedAuthority, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://app.example.com/with/a/path")] // a path
    [InlineData("https://*.example.com")] // a wildcard
    [InlineData("app.example.com")] // no scheme
    [InlineData("https://app.example.com?query=1")] // a query
    [InlineData("https://user:pass@app.example.com")] // userinfo
    [InlineData("ftp://app.example.com")] // a non-http scheme
    public void A_malformed_cors_origin_is_reported_by_name_only(string malformedOrigin)
    {
        // One well-formed origin plus one malformed: the key is reported once, by name, never by value.
        var configuration = BuildConfiguration(
            _connectionString,
            _authority,
            _audience,
            corsOrigins: [_wellFormedOrigin, malformedOrigin]);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { ProductionConfigurationValidator.CorsAllowedOriginsKey }, malformed);
        Assert.DoesNotContain(malformed, key => key.Contains(malformedOrigin, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.0.0.0/99")] // prefix length out of range
    [InlineData("300.0.0.0/8")] // not a valid address
    [InlineData("not-a-cidr")] // not a CIDR at all
    public void A_malformed_known_network_cidr_is_reported_by_name_only(string malformedCidr)
    {
        var configuration = BuildConfiguration(
            _connectionString,
            _authority,
            _audience,
            knownNetworks: ["10.0.0.0/8", malformedCidr]);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { ProductionConfigurationValidator.ForwardedHeadersKnownNetworksKey }, malformed);
        Assert.DoesNotContain(malformed, key => key.Contains(malformedCidr, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_malformed_critical_value_is_reported_by_name_in_declared_order()
    {
        var configuration = BuildConfiguration(
            "Host=db;Port=not-a-number",
            "issuer.example.com",
            _audience,
            corsOrigins: ["https://app.example.com/path"],
            knownNetworks: ["not-a-cidr"]);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(
            new[]
            {
                ProductionConfigurationValidator.DatabaseConnectionStringKey,
                ProductionConfigurationValidator.OidcAuthorityKey,
                ProductionConfigurationValidator.CorsAllowedOriginsKey,
                ProductionConfigurationValidator.ForwardedHeadersKnownNetworksKey,
            },
            malformed);
    }

    [Fact]
    public void A_missing_value_is_not_reported_as_malformed()
    {
        // A blank/absent value is the missing-value contract's concern, not the well-formedness check's: only a
        // PRESENT-but-malformed value is reported here, so the two diagnostics never double-report the same key.
        var configuration = BuildConfiguration(connectionString: null, authority: null, audience: null);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(malformed);
    }

    [Fact]
    public void Well_formedness_is_inert_outside_production_even_when_values_are_malformed()
    {
        // Local-development latitude: a Development run with garbled critical values still reports nothing
        // malformed, so the host starts and fails closed rather than being blocked.
        var configuration = BuildConfiguration(
            "Host=db;Port=not-a-number",
            "not a uri",
            _audience,
            corsOrigins: ["https://app.example.com/path"],
            knownNetworks: ["not-a-cidr"]);

        var malformed = ProductionConfigurationValidator.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: false);

        Assert.Empty(malformed);
    }

    [Fact]
    public void Well_formedness_keys_are_the_documented_critical_configuration_keys()
    {
        // The published contract (docs/13) names these keys; pin them so the docs and code stay in step.
        Assert.Equal("Cors:AllowedOrigins", ProductionConfigurationValidator.CorsAllowedOriginsKey);
        Assert.Equal(
            "ForwardedHeaders:KnownNetworks", ProductionConfigurationValidator.ForwardedHeadersKnownNetworksKey);
    }

    [Fact]
    public async Task Health_check_reports_unhealthy_when_a_critical_value_is_malformed()
    {
        var check = new MalformedConfigurationHealthCheck(hasMalformedConfiguration: true);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_check_reports_healthy_when_no_critical_value_is_malformed()
    {
        var check = new MalformedConfigurationHealthCheck(hasMalformedConfiguration: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
