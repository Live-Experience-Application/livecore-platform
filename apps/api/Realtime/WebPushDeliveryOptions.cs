// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Deployment configuration for the closed-app Web Push DELIVERY (CORE-PUSH-002, the "Closed-App Push
/// Notifications" epic). It carries the opt-in flag, the VAPID signing key pair and subject the outbound sender
/// needs, and the worker's drain cadence/batch — everything required to fan a CONTENT-FREE push out to recipients
/// with registered subscriptions.
///
/// <para>
/// OPT-IN AND OFF BY DEFAULT (the story's headline requirement). The Core has no precedent for an OUTBOUND-HTTP
/// fan-out, so this capability is strictly opt-in: <see cref="IsActive"/> is true ONLY when the deployment both
/// enables it (<c>WebPush:Delivery:Enabled=true</c>) AND configures the full VAPID signing material (the
/// <c>WebPush:Vapid:PublicKey</c>/<c>:PrivateKey</c>/<c>:Subject</c> needed to sign a push). With either missing the
/// surface is INERT — no push is enqueued and no dispatch loop runs — exactly the private-by-default posture the host
/// holds without object storage, an OIDC authority or a realtime backplane. So the acceptance criterion "inert with
/// no VAPID key configured" holds, and a deployment that does not want closed-app push does nothing.
/// </para>
///
/// <para>
/// SECRET HANDLING (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The VAPID PRIVATE key is read from
/// configuration ONLY (<c>WebPush:Vapid:PrivateKey</c>, e.g. the environment variable
/// <c>WebPush__Vapid__PrivateKey</c>), never hardcoded and never logged (this type never appears in a log template,
/// and its <c>ToString</c> is not overridden to print it). The VAPID PUBLIC key and the subject are not secrets.
/// All values are generic transport configuration; none carry vertical domain language (AGENTS.md).
/// </para>
/// </summary>
public sealed class WebPushDeliveryOptions
{
    /// <summary>Configuration section the enablement/cadence settings are read from (<c>WebPush:Delivery</c>).</summary>
    public const string DeliveryConfigurationSection = "WebPush:Delivery";

    /// <summary>Configuration section the VAPID signing material is read from (<c>WebPush:Vapid</c>).</summary>
    public const string VapidConfigurationSection = "WebPush:Vapid";

    /// <summary>Default interval between push dispatch sweeps (1 minute — pushes are time-sensitive).</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Default maximum number of pending deliveries a single sweep drains.</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>Default time-to-live a push service is asked to hold an undelivered push (12 hours).</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(12);

    private WebPushDeliveryOptions(
        bool enabled,
        string? vapidPublicKey,
        string? vapidPrivateKey,
        string? vapidSubject,
        TimeSpan sweepInterval,
        int batchSize,
        TimeSpan timeToLive)
    {
        if (sweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepInterval), sweepInterval, "The sweep interval must be positive.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive), timeToLive, "The time-to-live must be positive.");
        }

        Enabled = enabled;
        VapidPublicKey = vapidPublicKey;
        VapidPrivateKey = vapidPrivateKey;
        VapidSubject = vapidSubject;
        SweepInterval = sweepInterval;
        BatchSize = batchSize;
        TimeToLive = timeToLive;
    }

    /// <summary>Whether closed-app push delivery is enabled for this deployment (opt-in; off by default).</summary>
    public bool Enabled { get; }

    /// <summary>The VAPID application-server PUBLIC key (base64url), or <see langword="null"/> when unconfigured.</summary>
    public string? VapidPublicKey { get; }

    /// <summary>The VAPID application-server PRIVATE key (base64url), or <see langword="null"/> when unconfigured. A secret.</summary>
    public string? VapidPrivateKey { get; }

    /// <summary>The VAPID <c>sub</c> contact (e.g. a <c>mailto:</c> URI), or <see langword="null"/> when unconfigured.</summary>
    public string? VapidSubject { get; }

    /// <summary>How often the worker runs a push dispatch sweep (the configurable cadence).</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>The maximum number of pending deliveries a single sweep drains.</summary>
    public int BatchSize { get; }

    /// <summary>The TTL a push service is asked to hold an undelivered push for.</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary>
    /// Whether the closed-app push surface is ACTIVE: delivery is enabled AND the full VAPID signing material (the
    /// public key, the private key and the subject) is present. False — inert — when the deployment has not opted in
    /// or has not configured signing, so no push is ever enqueued or sent (the story's "inert with no VAPID key
    /// configured"). Gating the enqueue on the SAME condition as the send means the outbox never fills with rows the
    /// worker could not deliver.
    /// </summary>
    public bool IsActive =>
        Enabled
        && !string.IsNullOrWhiteSpace(VapidPublicKey)
        && !string.IsNullOrWhiteSpace(VapidPrivateKey)
        && !string.IsNullOrWhiteSpace(VapidSubject);

    /// <summary>
    /// Reads the closed-app push delivery options from configuration: the opt-in/cadence under
    /// <see cref="DeliveryConfigurationSection"/> (<c>Enabled</c>, <c>SweepInterval</c>, <c>BatchSize</c>,
    /// <c>TimeToLive</c>) and the VAPID signing material under <see cref="VapidConfigurationSection"/>
    /// (<c>PublicKey</c>, <c>PrivateKey</c>, <c>Subject</c>). Absent/blank values fall back to the safe defaults
    /// (disabled, 1 minute, 100, 12 hours; no signing material). A value that is PRESENT but cannot be parsed, or is
    /// out of range, is rejected at startup rather than silently degraded — a misconfigured cadence is a startup
    /// error, never a degenerate sweep.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured value is out of range.</exception>
    public static WebPushDeliveryOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var delivery = configuration.GetSection(DeliveryConfigurationSection);
        var vapid = configuration.GetSection(VapidConfigurationSection);

        return new WebPushDeliveryOptions(
            ParseBool(delivery["Enabled"]),
            Trimmed(vapid["PublicKey"]),
            Trimmed(vapid["PrivateKey"]),
            Trimmed(vapid["Subject"]),
            ParseTimeSpan(delivery["SweepInterval"], nameof(SweepInterval), DefaultSweepInterval),
            ParseBatchSize(delivery["BatchSize"]),
            ParseTimeSpan(delivery["TimeToLive"], nameof(TimeToLive), DefaultTimeToLive));
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Off by default: a deployment opts IN to closed-app push delivery.
            return false;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{DeliveryConfigurationSection}:Enabled' is not a valid boolean.");
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
                $"Configuration value '{DeliveryConfigurationSection}:{name}' is not a valid TimeSpan.");
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
                $"Configuration value '{DeliveryConfigurationSection}:BatchSize' is not a valid integer.");
        }

        return parsed;
    }
}
