// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.SmokeTests;

/// <summary>
/// Tests for the worker production configuration backstop (CORE-OBS-013), the worker counterpart of the API's
/// <c>ProductionConfigurationValidator</c> (CORE-OPS-008 / CORE-OPS-013). The worker holds no secret in source —
/// every required value is supplied from configuration — and these pin the pure decisions behind the loud
/// startup diagnostics: in a Production environment the database connection string that is absent (missing) or
/// present-but-unparseable (malformed) is reported BY NAME (never by value, so a secret is never logged; threat
/// T7), while outside Production both contracts are inert (local-development latitude). Unlike the API, the
/// worker requires NO OIDC and consumes no CORS/forwarded-headers configuration, so its required/critical set is
/// just the connection string.
/// </summary>
public class WorkerProductionConfigurationTests
{
    private const string _connectionString = "Host=db;Port=5432;Database=livecore;Username=livecore;Password=secret";

    private static IConfiguration BuildConfiguration(string? connectionString = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkerProductionConfiguration.DatabaseConnectionStringKey] = connectionString,
            })
            .Build();

    // ----- Required-value contract -----

    [Fact]
    public void The_connection_string_is_the_only_required_worker_production_setting()
    {
        // The worker serves no authenticated request, so it requires NO OIDC (unlike the API). Its one
        // always-required production value is the database connection string the loops are gated on; pin it so
        // the docs (.env.example, docs/13) and code stay in step.
        Assert.Equal(
            new[] { "ConnectionStrings:Database" },
            WorkerProductionConfiguration.RequiredProductionSettings);
    }

    [Fact]
    public void A_fully_configured_production_worker_reports_nothing_missing()
    {
        var configuration = BuildConfiguration(_connectionString);

        var missing = WorkerProductionConfiguration.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_connection_string_is_reported_by_name_in_production(string? connectionString)
    {
        var configuration = BuildConfiguration(connectionString);

        var missing = WorkerProductionConfiguration.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { WorkerProductionConfiguration.DatabaseConnectionStringKey }, missing);
    }

    [Fact]
    public void The_missing_value_contract_is_inert_outside_production()
    {
        // Local-development latitude: a Development worker with no database reports nothing missing, so it starts.
        var configuration = BuildConfiguration(connectionString: null);

        var missing = WorkerProductionConfiguration.FindMissingRequiredSettings(
            configuration, isProductionEnvironment: false);

        Assert.Empty(missing);
    }

    // ----- Well-formedness contract -----

    [Fact]
    public void A_well_formed_connection_string_reports_nothing_malformed()
    {
        var configuration = BuildConfiguration(_connectionString);

        var malformed = WorkerProductionConfiguration.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(malformed);
    }

    [Theory]
    [InlineData("Host=db;Port=not-a-number")] // a typed keyword (Port) that cannot be coerced
    [InlineData("NotARealKeyword=value")] // an unknown connection-string keyword
    public void A_malformed_connection_string_is_reported_by_name_only(string malformedConnectionString)
    {
        var configuration = BuildConfiguration(malformedConnectionString);

        var malformed = WorkerProductionConfiguration.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Equal(new[] { WorkerProductionConfiguration.DatabaseConnectionStringKey }, malformed);
        // By name only: the offending VALUE is never part of the reported result (threat T7).
        Assert.DoesNotContain(
            malformed, key => key.Contains(malformedConnectionString, StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_connection_string_is_not_reported_as_malformed()
    {
        // A blank/absent value is the missing-value contract's concern, not the well-formedness check's: only a
        // PRESENT-but-malformed value is reported here, so the two diagnostics never double-report the same key.
        var configuration = BuildConfiguration(connectionString: null);

        var malformed = WorkerProductionConfiguration.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: true);

        Assert.Empty(malformed);
    }

    [Fact]
    public void The_well_formedness_contract_is_inert_outside_production_even_when_malformed()
    {
        var configuration = BuildConfiguration("Host=db;Port=not-a-number");

        var malformed = WorkerProductionConfiguration.FindMalformedCriticalSettings(
            configuration, isProductionEnvironment: false);

        Assert.Empty(malformed);
    }

    [Fact]
    public async Task Malformed_health_check_reports_unhealthy_when_the_connection_string_is_malformed()
    {
        var check = new WorkerMalformedConfigurationHealthCheck(hasMalformedConfiguration: true);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Malformed_health_check_reports_healthy_when_the_connection_string_is_well_formed()
    {
        var check = new WorkerMalformedConfigurationHealthCheck(hasMalformedConfiguration: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
