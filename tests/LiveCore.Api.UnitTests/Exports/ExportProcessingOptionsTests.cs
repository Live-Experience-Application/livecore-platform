using LiveCore.Api.Exports;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for <see cref="ExportProcessingOptions"/> (CORE-JOB-002, CORE-RES-002) — the background export
/// processing job's deployment policy (sweep cadence, batch size, max-attempt/dead-letter bound). They assert
/// the configuration is read with safe defaults (so the worker runs without any export configuration), explicit
/// values are honored, every value is validated as positive (a misconfiguration can never schedule a degenerate
/// sweep nor disable the dead-letter bound), and a present-but-malformed value is rejected rather than silently
/// ignored. Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class ExportProcessingOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_values()
    {
        var options = new ExportProcessingOptions(TimeSpan.FromMinutes(30), 250, maxAttempts: 7);

        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(250, options.BatchSize);
        Assert.Equal(7, options.MaxAttempts);
    }

    [Fact]
    public void Constructor_defaults_the_max_attempts_when_unspecified()
        => Assert.Equal(
            ExportProcessingOptions.DefaultMaxAttempts,
            new ExportProcessingOptions(TimeSpan.FromHours(1), 50).MaxAttempts);

    [Fact]
    public void Constructor_rejects_a_non_positive_sweep_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExportProcessingOptions(TimeSpan.Zero, 50));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_a_non_positive_batch_size(int batchSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Constructor_rejects_a_non_positive_max_attempts(int maxAttempts)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExportProcessingOptions(TimeSpan.FromHours(1), 50, maxAttempts));

    [Fact]
    public void FromConfiguration_falls_back_to_the_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = ExportProcessingOptions.FromConfiguration(configuration);

        Assert.Equal(ExportProcessingOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(ExportProcessingOptions.DefaultBatchSize, options.BatchSize);
        Assert.Equal(ExportProcessingOptions.DefaultMaxAttempts, options.MaxAttempts);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:SweepInterval"] = "00:15:00",
                ["Exports:Processing:BatchSize"] = "500",
                ["Exports:Processing:MaxAttempts"] = "9",
            })
            .Build();

        var options = ExportProcessingOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromMinutes(15), options.SweepInterval);
        Assert.Equal(500, options.BatchSize);
        Assert.Equal(9, options.MaxAttempts);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_max_attempts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:MaxAttempts"] = "many",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ExportProcessingOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_non_positive_max_attempts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:MaxAttempts"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => ExportProcessingOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timespan()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:SweepInterval"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ExportProcessingOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_batch_size()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:BatchSize"] = "lots",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ExportProcessingOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_value_that_is_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Exports:Processing:BatchSize"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => ExportProcessingOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => ExportProcessingOptions.FromConfiguration(null!));
}
