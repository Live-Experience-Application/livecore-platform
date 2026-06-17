// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Reflection;

namespace LiveCore.Api.SystemModule;

/// <summary>
/// The AGPL-3.0 section 13 source offer (CORE-CMP-001): the license this network
/// application is distributed under, the running build version, and the
/// revision-pinned location a remote user can obtain its Corresponding Source.
///
/// The platform is AGPL-3.0-or-later (LICENSE; docs/16_LICENSING.md) and
/// network-interactive (the SignalR hub plus the <c>/api/v1</c> surface), which
/// triggers AGPL section 13's obligation to OFFER remote users access to the
/// Corresponding Source of the EXACT version they are interacting with. This value is
/// the small, anonymous surface that satisfies that obligation; it is served by
/// <see cref="SourceOfferEndpoints"/> and carries no secret or PII (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// The offered <see cref="SourceUrl"/> is PINNED to the running revision rather than
/// the repository root (CORE-LIC-005): it is the repository pinned to the build
/// version's tag (<c>{repository}/tree/v{version}</c>), so a remote user fetches the
/// Corresponding Source of the version actually running, not whatever the default
/// branch later becomes. The version is the build the release pipeline stamps
/// (<see cref="ResolveBuildVersion"/>), so the pin tracks the deployed revision.
///
/// The repository is configuration-overridable on purpose: a hosted deployment that
/// MODIFIES the source must offer remote users ITS OWN Corresponding Source (the
/// modified version running there), not the canonical upstream — so an operator
/// running a fork points <see cref="RepositoryUrlConfigurationKey"/> at their
/// repository. With nothing configured the offer falls back to the canonical
/// upstream repository (<see cref="DefaultSourceUrl"/>). To stop a modified
/// deployment SILENTLY keeping the upstream offer, the configuration is validated
/// (<see cref="ForRunningBuild"/>): a deployment that declares modified source
/// (<see cref="ModifiedConfigurationKey"/>) but leaves the repository unset, or sets
/// a malformed repository URL, is refused (fail-closed) rather than offering a wrong
/// or non-fetchable source.
/// </summary>
/// <param name="License">The SPDX license identifier this application is distributed under.</param>
/// <param name="Version">The running build version (its Corresponding Source revision).</param>
/// <param name="SourceUrl">Where a remote user can obtain the Corresponding Source, pinned to <paramref name="Version"/>.</param>
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
    /// is a secret, and none other than the canonical default lives in source. When
    /// set it must be an absolute http(s) URL, validated by <see cref="ForRunningBuild"/>.
    /// </summary>
    public const string RepositoryUrlConfigurationKey = "SourceOffer:RepositoryUrl";

    /// <summary>
    /// Configuration key (env <c>SourceOffer__Modified</c>) a deployment sets to
    /// <c>true</c> to DECLARE that it runs modified Core source. The documented rule
    /// (docs/16_LICENSING.md): a modified deployment MUST also set
    /// <see cref="RepositoryUrlConfigurationKey"/> to its own Corresponding Source,
    /// because AGPL section 13 obliges it to offer the source actually running there,
    /// not the canonical upstream. <see cref="ForRunningBuild"/> validates this and
    /// refuses (fail-closed) when a modified deployment left the repository unset, so
    /// a modified deployment cannot SILENTLY keep offering the upstream source. The
    /// flag is opt-in: an unmodified deployment leaves it unset and falls back to the
    /// canonical upstream repository. It is not a secret and carries no value other
    /// than the boolean declaration.
    /// </summary>
    public const string ModifiedConfigurationKey = "SourceOffer:Modified";

    /// <summary>
    /// Builds the source offer for the currently running build: the canonical
    /// license, the assembly's build version and the revision-pinned Corresponding
    /// Source location (the configured or canonical-default repository pinned to the
    /// running version's tag, <c>{repository}/tree/v{version}</c>). It also VALIDATES
    /// the deployment's source-offer configuration against the documented section 13
    /// rule and is fail-closed: it refuses a configuration whose offer would be wrong
    /// or non-fetchable rather than serving it.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The section 13 source offer for this build.</returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured <see cref="RepositoryUrlConfigurationKey"/> is not an absolute
    /// http(s) URL, or the deployment declares modified source
    /// (<see cref="ModifiedConfigurationKey"/>) but left the repository unset — in
    /// which case the offer would silently point at the canonical upstream source
    /// rather than the modified Corresponding Source actually running.
    /// </exception>
    public static SourceOffer ForRunningBuild(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var version = ResolveBuildVersion();
        var repository = ResolveCorrespondingSourceRepository(configuration);

        return new SourceOffer(LicenseId, version, PinToRevision(repository, version));
    }

    /// <summary>
    /// Resolves the repository whose Corresponding Source the offer points at and
    /// validates the deployment's configuration against the documented section 13 rule
    /// (fail-closed). A configured <see cref="RepositoryUrlConfigurationKey"/> wins (a
    /// fork offering its own source) and must be an absolute http(s) URL; with none
    /// configured, a deployment that declares modified source
    /// (<see cref="ModifiedConfigurationKey"/>) is refused (it must name its own
    /// repository), and only an unmodified deployment falls back to the canonical
    /// upstream <see cref="DefaultSourceUrl"/>.
    /// </summary>
    private static string ResolveCorrespondingSourceRepository(IConfiguration configuration)
    {
        var configured = configuration[RepositoryUrlConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var repository = configured.Trim();
            if (!IsAbsoluteHttpUrl(repository))
            {
                throw new InvalidOperationException(
                    $"The source-offer repository configured under '{RepositoryUrlConfigurationKey}' " +
                    $"('{repository}') is not an absolute http(s) URL, so the AGPL section 13 offer could not " +
                    "resolve to a fetchable Corresponding Source. Set it to the absolute URL of the repository " +
                    "that serves this deployment's Corresponding Source.");
            }

            return repository;
        }

        // No override. A deployment that DECLARES modified source but left the repository unset would offer the
        // canonical upstream source rather than its own modified source, which AGPL section 13 forbids — so
        // refuse rather than silently keep offering upstream (fail-closed), mirroring the OIDC audience startup
        // guard's refuse-rather-than-serve-a-wrong-value posture (CORE-OPS-004).
        if (IsModifiedDeployment(configuration))
        {
            throw new InvalidOperationException(
                $"This deployment declares modified source ('{ModifiedConfigurationKey}'=true) but left " +
                $"'{RepositoryUrlConfigurationKey}' unset, so the AGPL section 13 source offer would point at " +
                "the canonical upstream repository rather than the modified Corresponding Source actually " +
                $"running. Set '{RepositoryUrlConfigurationKey}' to the repository that serves this " +
                "deployment's own Corresponding Source.");
        }

        return DefaultSourceUrl;
    }

    /// <summary>
    /// Whether the deployment DECLARES that it runs modified source
    /// (<see cref="ModifiedConfigurationKey"/> parses to <c>true</c>). Anything else —
    /// unset, blank or a non-boolean value — is treated as an unmodified deployment.
    /// </summary>
    private static bool IsModifiedDeployment(IConfiguration configuration) =>
        bool.TryParse(configuration[ModifiedConfigurationKey], out var modified) && modified;

    /// <summary>
    /// Whether <paramref name="value"/> is an absolute http(s) URL — the only shape a
    /// Corresponding Source repository can take so the pinned offer is a fetchable
    /// location (not a relative path, a non-web scheme such as <c>file:</c>, or junk).
    /// </summary>
    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Pins a Corresponding Source repository to the running revision so the offer
    /// resolves to the source of the EXACT version running (AGPL section 13), not the
    /// repository's default branch: it appends the Git host's <c>/tree/v{version}</c>
    /// tag path to the repository root (e.g.
    /// <c>https://…/livecore-platform/tree/v1.2.3</c>). The release pipeline tags each
    /// release <c>v{version}</c> with the same build version the offer reports, so the
    /// pin resolves to that tag's tree. A trailing slash on the repository root is
    /// trimmed so the pin is never doubled (<c>…//tree/…</c>).
    /// </summary>
    internal static string PinToRevision(string repository, string version) =>
        $"{repository.TrimEnd('/')}/tree/v{version}";

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
