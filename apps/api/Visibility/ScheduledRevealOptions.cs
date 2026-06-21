// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Configuration for the background scheduled-reveal sweep job (CORE-VSEAL-002, the "Scheduled and Sealed
/// Visibility" epic). The job automatically reveals every Hidden visibility rule whose
/// <see cref="VisibilityRule.ScheduledRevealAt"/> time has arrived, on a configurable cadence, idempotently and
/// tenant-safe — driving the SAME central reveal command as a live host reveal (the "WHEN" half of controlling
/// visibility, docs/01_PRODUCT_VISION_AND_SCOPE.md). It mirrors the recap-generation and store-reconciliation
/// jobs: the worker host owns the platform's async jobs (docs/02_ARCHITECTURE.md), the LOGIC lives in the
/// Visibility module (<see cref="ScheduledRevealService"/>), and these options carry only timing/enablement
/// policy.
///
/// <para>
/// OFF BY DEFAULT (the story's "off-by-default config" requirement). Server-enforced scheduled reveal is opt-in
/// per deployment: <see cref="Enabled"/> defaults to <see langword="false"/>, exactly like the billing-gated
/// store-reconciliation job, so a deployment that does not use scheduled reveals runs no sweep at all (and the
/// scheduledRevealAt field is simply inert metadata). A deployment turns it on with
/// <c>Visibility:ScheduledReveal:Enabled=true</c>.
/// </para>
///
/// <para>
/// <see cref="SweepInterval"/> is how often the worker runs a sweep (scheduled reveals are time-sensitive, so the
/// default is a short one-minute cadence), and <see cref="BatchSize"/> bounds how many due rules a single sweep
/// processes so one run can never do unbounded work (threat T9). The knobs are deployment policy, not per-request
/// input, so — exactly like <see cref="LiveCore.Api.Recaps.RecapGenerationOptions"/> — they are read once from
/// configuration (under <see cref="ConfigurationSection"/>) with safe defaults. No credentials or secrets live
/// here; the values are product-neutral (AGENTS.md).
/// </para>
/// </summary>
public sealed class ScheduledRevealOptions
{
    /// <summary>Configuration section the scheduled-reveal settings are read from (<c>Visibility:ScheduledReveal</c>).</summary>
    public const string ConfigurationSection = "Visibility:ScheduledReveal";

    /// <summary>Default interval between scheduled-reveal sweeps (1 minute — scheduled reveals are time-sensitive).</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Default maximum number of due rules processed in a single sweep.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>
    /// Creates scheduled-reveal options, validating that every value is sane. A non-positive sweep interval or a
    /// non-positive batch size is rejected, so a misconfiguration can never schedule a degenerate sweep.
    /// </summary>
    /// <param name="enabled">Whether the scheduled-reveal sweep is enabled for this deployment (off by default).</param>
    /// <param name="sweepInterval">How often a scheduled-reveal sweep runs.</param>
    /// <param name="batchSize">The maximum number of due rules processed in one sweep.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public ScheduledRevealOptions(bool enabled, TimeSpan sweepInterval, int batchSize)
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

        Enabled = enabled;
        SweepInterval = sweepInterval;
        BatchSize = batchSize;
    }

    /// <summary>Whether the scheduled-reveal sweep is enabled for this deployment (opt-in; off by default).</summary>
    public bool Enabled { get; }

    /// <summary>How often the worker runs a scheduled-reveal sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of due rules a single sweep processes.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// Reads the scheduled-reveal options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Visibility:ScheduledReveal:Enabled</c> as a boolean, <c>:SweepInterval</c> as a <see cref="TimeSpan"/>
    /// string, <c>:BatchSize</c> as an integer), falling back to the safe defaults (disabled, 1 minute, 50) when a
    /// value is absent or blank. A value that is PRESENT but cannot be parsed, or is out of range, is rejected at
    /// startup rather than silently falling back — a misconfigured timing is a startup error, never a degenerate
    /// sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static ScheduledRevealOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        return new ScheduledRevealOptions(
            ParseBool(section["Enabled"]),
            ParseTimeSpan(section["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(section["BatchSize"]));
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Off by default: a deployment opts IN to server-enforced scheduled reveal.
            return false;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:Enabled' is not a valid boolean.");
        }

        return parsed;
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
