// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetUploadConstraints"/> (CORE-AST-007) — the deployment's configurable MIME
/// allowlist and absolute per-object size ceiling the upload-intent command enforces before minting a signed
/// URL. They assert the policy reads configuration with safe, already-hardened curated defaults (so an
/// unconfigured deployment is not wide open), honors a configured allowlist/ceiling, matches the content type
/// case-insensitively, treats an explicitly empty allowlist as "no restriction", and rejects a non-positive
/// configured ceiling. Generic Asset vocabulary only (AGENTS.md).
/// </summary>
public class AssetUploadConstraintsTests
{
    [Fact]
    public void Unrestricted_allows_every_content_type_and_size()
    {
        var constraints = AssetUploadConstraints.Unrestricted;

        Assert.False(constraints.RestrictsContentType);
        Assert.Null(constraints.MaxObjectSizeBytes);
        Assert.True(constraints.IsContentTypeAllowed("anything/at-all"));
        Assert.True(constraints.IsWithinSizeCeiling(long.MaxValue));
    }

    [Fact]
    public void Constructor_enforces_the_allowlist_case_insensitively_and_rejects_blank()
    {
        var constraints = new AssetUploadConstraints(new[] { "image/png", "application/pdf" }, maxObjectSizeBytes: 1_000);

        Assert.True(constraints.RestrictsContentType);
        Assert.True(constraints.IsContentTypeAllowed("image/png"));
        Assert.True(constraints.IsContentTypeAllowed("IMAGE/PNG"));
        Assert.True(constraints.IsContentTypeAllowed("  application/pdf  "));
        Assert.False(constraints.IsContentTypeAllowed("application/zip"));
        Assert.False(constraints.IsContentTypeAllowed(""));
        Assert.False(constraints.IsContentTypeAllowed("   "));
    }

    [Fact]
    public void Constructor_treats_a_null_or_all_blank_allowlist_as_no_restriction()
    {
        Assert.False(new AssetUploadConstraints(allowedContentTypes: null, maxObjectSizeBytes: null).RestrictsContentType);
        Assert.False(new AssetUploadConstraints(Array.Empty<string>(), maxObjectSizeBytes: null).RestrictsContentType);
        Assert.False(new AssetUploadConstraints(new[] { "", "   " }, maxObjectSizeBytes: null).RestrictsContentType);
    }

    [Fact]
    public void IsWithinSizeCeiling_is_inclusive_of_the_ceiling()
    {
        var constraints = new AssetUploadConstraints(allowedContentTypes: null, maxObjectSizeBytes: 1_000);

        Assert.True(constraints.IsWithinSizeCeiling(1));
        Assert.True(constraints.IsWithinSizeCeiling(1_000));
        Assert.False(constraints.IsWithinSizeCeiling(1_001));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_a_non_positive_ceiling(long ceiling)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssetUploadConstraints(allowedContentTypes: null, ceiling));
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_hardened_curated_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var constraints = AssetUploadConstraints.FromConfiguration(configuration);

        Assert.True(constraints.RestrictsContentType);
        Assert.Equal(AssetUploadConstraints.DefaultMaxObjectSizeBytes, constraints.MaxObjectSizeBytes);
        // The curated default admits common safe media types but not an arbitrary one.
        Assert.True(constraints.IsContentTypeAllowed("image/png"));
        Assert.True(constraints.IsContentTypeAllowed("application/pdf"));
        Assert.False(constraints.IsContentTypeAllowed("application/x-msdownload"));
    }

    [Fact]
    public void FromConfiguration_reads_a_configured_allowlist_and_ceiling()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Upload:AllowedContentTypes:0"] = "image/jpeg",
                ["Assets:Upload:AllowedContentTypes:1"] = "video/mp4",
                ["Assets:Upload:MaxObjectSizeBytes"] = "2048",
            })
            .Build();

        var constraints = AssetUploadConstraints.FromConfiguration(configuration);

        Assert.Equal(2048, constraints.MaxObjectSizeBytes);
        Assert.True(constraints.IsContentTypeAllowed("image/jpeg"));
        Assert.True(constraints.IsContentTypeAllowed("video/mp4"));
        // A configured allowlist narrows to exactly the configured set: a default type no longer applies.
        Assert.False(constraints.IsContentTypeAllowed("image/png"));
    }

    [Fact]
    public void FromConfiguration_rejects_a_non_positive_configured_ceiling()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Upload:MaxObjectSizeBytes"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentException>(() => AssetUploadConstraints.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => AssetUploadConstraints.FromConfiguration(null!));
    }
}
