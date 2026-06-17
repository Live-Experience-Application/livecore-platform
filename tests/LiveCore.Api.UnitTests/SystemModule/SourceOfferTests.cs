// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.SystemModule;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.SystemModule;

/// <summary>
/// Unit tests for <see cref="SourceOffer.ForRunningBuild"/> (CORE-CMP-001,
/// CORE-LIC-005) — the AGPL section 13 source offer built from configuration plus the
/// running build. They pin the canonical license, the fail-safe build version (always
/// a non-empty string), the REVISION-PINNED source URL (the repository pinned to the
/// running version's tag, never the bare repo root), and the source-location
/// resolution + validation: the canonical upstream default when unconfigured/blank,
/// the deployment override when a fork sets it (a modified deployment must offer its
/// own Corresponding Source), and the fail-closed refusals — a modified deployment
/// that left the repository unset, and a malformed override URL. The end-to-end HTTP
/// behavior (anonymous <c>GET /source</c>) and the startup guard are covered by the
/// integration and smoke suites.
/// </summary>
public class SourceOfferTests
{
    [Fact]
    public void ForRunningBuild_offers_the_canonical_license_and_a_build_version()
    {
        var configuration = new ConfigurationBuilder().Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        Assert.Equal(SourceOffer.LicenseId, offer.License);
        Assert.Equal("AGPL-3.0-or-later", offer.License);
        Assert.False(string.IsNullOrWhiteSpace(offer.Version));
    }

    [Fact]
    public void ForRunningBuild_pins_the_canonical_source_url_to_the_running_revision_when_unconfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        // Fail-safe default: with no override the offer points at the canonical
        // upstream repository — but PINNED to the running revision's tag (section 13
        // requires the Corresponding Source of the exact version running), never the
        // bare repository root.
        Assert.Equal($"{SourceOffer.DefaultSourceUrl}/tree/v{offer.Version}", offer.SourceUrl);
        Assert.Contains(offer.Version, offer.SourceUrl, StringComparison.Ordinal);
        Assert.NotEqual(SourceOffer.DefaultSourceUrl, offer.SourceUrl);
    }

    [Fact]
    public void ForRunningBuild_offer_url_matches_the_stamped_build_version()
    {
        var configuration = new ConfigurationBuilder().Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        // The offer reports, and pins to, the single stamped build version the build
        // carries (reused via ResolveBuildVersion), so the offer resolves to that
        // exact revision's Corresponding Source.
        Assert.Equal(SourceOffer.ResolveBuildVersion(), offer.Version);
        Assert.Equal(SourceOffer.PinToRevision(SourceOffer.DefaultSourceUrl, offer.Version), offer.SourceUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForRunningBuild_falls_back_to_the_canonical_source_url_for_a_blank_override(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.RepositoryUrlConfigurationKey] = configured,
            })
            .Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        Assert.Equal($"{SourceOffer.DefaultSourceUrl}/tree/v{offer.Version}", offer.SourceUrl);
    }

    [Fact]
    public void ForRunningBuild_offers_a_configured_deployments_own_source_url_pinned_to_the_revision()
    {
        // A modified deployment must offer ITS OWN Corresponding Source (section 13),
        // the configured value is trimmed, and the offer is pinned to the running
        // revision's tag on that repository (a trailing slash is not doubled).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.RepositoryUrlConfigurationKey] = "  https://git.example.test/operator/livecore-fork/  ",
            })
            .Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        Assert.Equal($"https://git.example.test/operator/livecore-fork/tree/v{offer.Version}", offer.SourceUrl);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void ForRunningBuild_refuses_a_modified_deployment_that_left_the_repository_unset(string modified)
    {
        // The documented section 13 rule (docs/16_LICENSING.md): a deployment that
        // declares modified source MUST name its own Corresponding Source. Leaving the
        // repository unset would silently keep offering the canonical UPSTREAM source,
        // so the offer is refused (fail-closed) rather than built.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.ModifiedConfigurationKey] = modified,
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => SourceOffer.ForRunningBuild(configuration));
        Assert.Contains(SourceOffer.RepositoryUrlConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForRunningBuild_offers_a_modified_deployments_own_source_when_the_repository_is_set()
    {
        // A modified deployment that DOES name its own repository is valid: the offer
        // resolves to that repository, pinned to the running revision.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.ModifiedConfigurationKey] = "true",
                [SourceOffer.RepositoryUrlConfigurationKey] = "https://git.example.test/operator/livecore-fork",
            })
            .Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        Assert.Equal($"https://git.example.test/operator/livecore-fork/tree/v{offer.Version}", offer.SourceUrl);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.test/repo")]
    [InlineData("file:///etc/passwd")]
    public void ForRunningBuild_refuses_a_malformed_override_url(string configured)
    {
        // The override must be an absolute http(s) URL so the pinned offer is a
        // fetchable location; a non-absolute or non-web value is refused (fail-closed)
        // rather than offering a source no remote user can obtain.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.RepositoryUrlConfigurationKey] = configured,
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => SourceOffer.ForRunningBuild(configuration));
        Assert.Contains(SourceOffer.RepositoryUrlConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForRunningBuild_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => SourceOffer.ForRunningBuild(null!));
    }
}
