// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Recaps;

/// <summary>
/// Configuration for the background recap generation job (CORE-JOB-001, the recap-generation story of the
/// "Worker Background Jobs" epic). The job produces a recap asynchronously for every session that needs one —
/// a session that has ENDED but has no recap yet — on a configurable cadence, idempotently
/// (docs/00_START_HERE.md: a Host can "produce Recaps"; docs/03_DOMAIN_LANGUAGE.md "Recap": a session summary
/// or structured continuation output). The job mirrors the asset cleanup job (CORE-AST-006): the worker host
/// owns the platform's async jobs (docs/02_ARCHITECTURE.md), and the generation LOGIC lives in the Recaps
/// module (<see cref="RecapGenerationService"/>); these options carry only timing/scoping policy.
///
/// The knobs are deployment policy, not per-request input, so — exactly like
/// <see cref="LiveCore.Api.Assets.AssetCleanupOptions"/> — they are read once from configuration (under
/// <see cref="ConfigurationSection"/>) with safe defaults, so the worker host still runs without any recap
/// configuration. No credentials or secrets live here (only a timing and a batch size); the values are
/// product-neutral (AGENTS.md).
///
/// <para>
/// <see cref="SweepInterval"/> is how often the worker runs a generation sweep (the configurable cadence in
/// the acceptance criteria), and <see cref="BatchSize"/> bounds how many eligible sessions a single sweep
/// processes so one run can never do unbounded work. There is no retention window: a session needs a recap as
/// soon as it has ended (eligibility is "ended and not yet recapped", evaluated by
/// <see cref="IRecapEligibleSessionReader"/>), so — unlike the cleanup job's grace window — no minimum age
/// gates generation.
/// </para>
/// </summary>
public sealed class RecapGenerationOptions
{
    /// <summary>Configuration section the recap generation settings are read from (<c>Recaps:Generation</c>).</summary>
    public const string ConfigurationSection = "Recaps:Generation";

    /// <summary>Default interval between recap generation sweeps (1 hour), matching the cleanup job's cadence.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>Default maximum number of eligible sessions processed in a single sweep.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>
    /// Creates recap generation options, validating that every value is sane. A non-positive sweep interval or
    /// a non-positive batch size is rejected, so a misconfiguration can never schedule a degenerate sweep.
    /// </summary>
    /// <param name="sweepInterval">How often a recap generation sweep runs.</param>
    /// <param name="batchSize">The maximum number of eligible sessions processed in one sweep.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public RecapGenerationOptions(TimeSpan sweepInterval, int batchSize)
    {
        if (sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepInterval), sweepInterval, "The sweep interval must be positive.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        SweepInterval = sweepInterval;
        BatchSize = batchSize;
    }

    /// <summary>How often the worker runs a recap generation sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of eligible sessions a single sweep processes.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the generation options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Recaps:Generation:SweepInterval</c> as a <see cref="TimeSpan"/> string,
    /// <c>Recaps:Generation:BatchSize</c> as an integer), falling back to the safe defaults when a value is
    /// absent or blank. A value that is PRESENT but cannot be parsed, or is out of range, is rejected at
    /// startup rather than silently falling back — a misconfigured timing is a startup error, never a
    /// degenerate sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static RecapGenerationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new RecapGenerationOptions(
            ParseTimeSpan(section["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]));
    }

    private static TimeSpan ParseTimeSpan(string? value, string name, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{name}' is not a valid TimeSpan.");
        }

        return parsed;
    }

    private static int ParseBatchSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBatchSize;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{nameof(BatchSize)}' is not a valid integer.");
        }

        return parsed;
    }
}
