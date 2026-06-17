// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Reflection;

namespace LiveCore.Api.SystemModule;

/// <summary>
/// The AGPL-3.0 section 13 source offer (CORE-CMP-001): the license this network
/// application is distributed under, the running build version, and the location a
/// remote user can obtain its Corresponding Source.
///
/// The platform is AGPL-3.0-or-later (LICENSE; docs/16_LICENSING.md) and
/// network-interactive (the SignalR hub plus the <c>/api/v1</c> surface), which
/// triggers AGPL section 13's obligation to OFFER remote users access to the
/// Corresponding Source of the version they are interacting with. This value is the
/// small, anonymous surface that satisfies that obligation; it is served by
/// <see cref="SourceOfferEndpoints"/> and carries no secret or PII (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// The source URL is configuration-overridable on purpose: a hosted deployment that
/// MODIFIES the source must offer remote users ITS OWN Corresponding Source (the
/// modified version running there), not the canonical upstream — so an operator
/// running a fork points <see cref="RepositoryUrlConfigurationKey"/> at their
/// repository. With nothing configured the offer falls back to the canonical
/// upstream repository (<see cref="DefaultSourceUrl"/>).
/// </summary>
/// <param name="License">The SPDX license identifier this application is distributed under.</param>
/// <param name="Version">The running build version (its Corresponding Source revision).</param>
/// <param name="SourceUrl">Where a remote user can obtain the Corresponding Source.</param>
public sealed record SourceOffer(string License, string Version, string SourceUrl)
{
    /// <summary>
    /// The SPDX license identifier of this network application
    /// (LICENSE; docs/16_LICENSING.md).
    /// </summary>
    public const string LicenseId = "AGPL-3.0-or-later";

    /// <summary>
    /// The canonical upstream source repository, used when no deployment-specific
    /// override is configured. A deployment that ships modified source overrides
    /// this with <see cref="RepositoryUrlConfigurationKey"/> so the section 13 offer
    /// points at the source actually running.
    /// </summary>
    public const string DefaultSourceUrl = "https://github.com/Live-Experience-Application/livecore-platform";

    /// <summary>
    /// Configuration key a deployment sets to publish its own Corresponding Source
    /// location (a fork/modified deployment), read from configuration only — no URL
    /// is a secret, and none other than the canonical default lives in source.
    /// </summary>
    public const string RepositoryUrlConfigurationKey = "SourceOffer:RepositoryUrl";

    /// <summary>
    /// Builds the source offer for the currently running build: the canonical
    /// license, the assembly's build version and the configured (or canonical
    /// default) Corresponding Source location.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The section 13 source offer for this build.</returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static SourceOffer ForRunningBuild(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[RepositoryUrlConfigurationKey];
        var sourceUrl = string.IsNullOrWhiteSpace(configured) ? DefaultSourceUrl : configured.Trim();

        return new SourceOffer(LicenseId, ResolveBuildVersion(), sourceUrl);
    }

    /// <summary>
    /// Resolves the running build version from the API assembly so the offer reuses
    /// the single version the build stamps (the release pipeline sets it from the
    /// release tag) rather than a hardcoded literal. It prefers the informational
    /// version (the SemVer the build carries), drops any <c>+build.metadata</c>
    /// suffix, and falls back to the numeric assembly version, then to
    /// <c>0.0.0</c>, so the field is always a non-empty version string.
    /// </summary>
    internal static string ResolveBuildVersion()
    {
        var assembly = typeof(SourceOffer).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var version = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (version.Length > 0)
            {
                return version;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is not null ? assemblyVersion.ToString() : "0.0.0";
    }
}
