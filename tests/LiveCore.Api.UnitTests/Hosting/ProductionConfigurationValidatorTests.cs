// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

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
        string? audience = null)
    {
        var values = new Dictionary<string, string?>
        {
            [ProductionConfigurationValidator.DatabaseConnectionStringKey] = connectionString,
            [ProductionConfigurationValidator.OidcAuthorityKey] = authority,
            [ProductionConfigurationValidator.OidcAudienceKey] = audience,
        };

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
}
