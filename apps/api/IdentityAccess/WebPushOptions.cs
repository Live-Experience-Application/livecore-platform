// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// The deployment's Web Push (VAPID) configuration for the closed-app push surface (CORE-PUSH-001, the
/// "Closed-App Push Notifications" epic). Today it carries ONLY the VAPID PUBLIC key — the value a browser
/// client needs to create a push subscription, exposed by <c>GET /api/v1/push/vapid-public-key</c>. The VAPID
/// PRIVATE key stays deployment-side and is never read here and never shipped to a client; the outbound,
/// signed delivery that needs it is a later story (CORE-PUSH-002).
///
/// FAIL-CLOSED / INERT WHEN UNCONFIGURED. <see cref="IsConfigured"/> is true only when a public key is present.
/// With no key configured the push surface is INERT: the public-key route reports no key and registration is
/// refused (no subscription is registrable), exactly the private-by-default posture the host holds when it runs
/// without object storage, an OIDC authority or a realtime backplane (the story's acceptance criterion).
///
/// The key is read from configuration ONLY (the <c>WebPush:Vapid:PublicKey</c> key, e.g. the environment
/// variable <c>WebPush__Vapid__PublicKey</c>), never hardcoded. The VAPID public key is not itself a secret —
/// it is published to clients by design — but no credential of any kind lives in this repository (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The value is generic transport configuration; it carries no vertical
/// domain language (AGENTS.md).
/// </summary>
internal sealed class WebPushOptions
{
    /// <summary>Configuration section the Web Push settings are read from (<c>WebPush:Vapid</c>).</summary>
    public const string ConfigurationSection = "WebPush:Vapid";

    /// <summary>
    /// Maximum accepted length of the configured VAPID public key. A VAPID application-server public key is a
    /// base64url-encoded uncompressed P-256 point (~87 characters); the bound is generous but rejects an absurd
    /// value at startup rather than serving it.
    /// </summary>
    public const int MaxPublicKeyLength = 512;

    private WebPushOptions(string? publicKey)
    {
        PublicKey = publicKey;
    }

    /// <summary>
    /// The configured VAPID public key, or <see langword="null"/> when none is configured (the inert surface).
    /// When present, it is the exact value the public-key route returns to clients.
    /// </summary>
    public string? PublicKey { get; }

    /// <summary>
    /// Whether the push surface is configured: a VAPID public key is present. When false the surface is inert —
    /// the public-key route reports no key and registration is refused (the story's "no subscription is
    /// registrable").
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey);

    /// <summary>
    /// Reads the Web Push settings from configuration under <see cref="ConfigurationSection"/>
    /// (<c>WebPush:Vapid:PublicKey</c>). The key is trimmed of surrounding whitespace; a blank value is treated
    /// as unconfigured (the inert surface). A configured key longer than <see cref="MaxPublicKeyLength"/> is
    /// rejected at startup rather than served.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">A configured public key exceeds the accepted length.</exception>
    public static WebPushOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var publicKey = configuration.GetSection(ConfigurationSection)["PublicKey"];
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return new WebPushOptions(null);
        }

        var trimmed = publicKey.Trim();
        if (trimmed.Length > MaxPublicKeyLength)
        {
            throw new ArgumentException(
                $"The configured VAPID public key must be at most {MaxPublicKeyLength} characters.",
                nameof(configuration));
        }

        return new WebPushOptions(trimmed);
    }
}
