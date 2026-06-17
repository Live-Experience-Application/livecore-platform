// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.SystemModule;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.SystemModule;

/// <summary>
/// Unit tests for <see cref="SourceOffer.ForRunningBuild"/> (CORE-CMP-001) — the
/// AGPL section 13 source offer built from configuration plus the running build.
/// They pin the canonical license, the fail-safe build version (always a non-empty
/// string), and the source-location resolution: the canonical upstream default when
/// unconfigured/blank and the deployment override when a fork sets it (a modified
/// deployment must offer its own Corresponding Source). The end-to-end HTTP behavior
/// (anonymous <c>GET /source</c>) is covered by the integration suite.
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
    public void ForRunningBuild_offers_the_canonical_source_url_when_unconfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        // Fail-safe default: with no override the offer points at the canonical
        // upstream repository.
        Assert.Equal(SourceOffer.DefaultSourceUrl, offer.SourceUrl);
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

        Assert.Equal(SourceOffer.DefaultSourceUrl, offer.SourceUrl);
    }

    [Fact]
    public void ForRunningBuild_offers_a_configured_deployments_own_source_url()
    {
        // A modified deployment must offer ITS OWN Corresponding Source (section 13),
        // and the configured value is trimmed.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SourceOffer.RepositoryUrlConfigurationKey] = "  https://git.example.test/operator/livecore-fork  ",
            })
            .Build();

        var offer = SourceOffer.ForRunningBuild(configuration);

        Assert.Equal("https://git.example.test/operator/livecore-fork", offer.SourceUrl);
    }

    [Fact]
    public void ForRunningBuild_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => SourceOffer.ForRunningBuild(null!));
    }
}
