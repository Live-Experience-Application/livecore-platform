// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="SignedAssetUrl"/> (CORE-AST-002, the storage adapter interface). A signed
/// asset URL is the only access the storage adapter ever yields, and this type STRUCTURALLY enforces the
/// "private by default, accessed only through authorized signed URLs" guarantee (the epic acceptance
/// criterion; threat T4 "Asset leak" in docs/07_SECURITY_THREAT_MODEL.md): it cannot be constructed
/// without an absolute URL and a strictly positive, short (&lt;= <see cref="SignedAssetUrl.MaxLifetime"/>)
/// lifetime, so a long-lived or non-expiring "public" URL is unrepresentable. The log-safety test asserts
/// the secret URL never appears in <see cref="SignedAssetUrl.ToString"/> (threats T4/T7). No
/// vertical-specific terms appear anywhere — generic Asset vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class SignedAssetUrlTests
{
    private static readonly Uri _url = new("https://storage.example.com/livecore-private-assets/org/ws/asset-001.bin?sig=abc&expires=123");
    private static readonly DateTimeOffset _issuedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_url_operation_and_computes_expiry()
    {
        var lifetime = TimeSpan.FromMinutes(15);

        var signed = SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, lifetime);

        Assert.Equal(_url, signed.Url);
        Assert.Equal(AssetStorageOperation.Download, signed.Operation);
        Assert.Equal(_issuedAt + lifetime, signed.ExpiresAt);
    }

    [Fact]
    public void Create_normalizes_expiry_to_utc()
    {
        var localIssuedAt = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var signed = SignedAssetUrl.Create(_url, AssetStorageOperation.Upload, localIssuedAt, TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, signed.ExpiresAt.Offset);
        Assert.Equal((localIssuedAt + TimeSpan.FromMinutes(5)).ToUniversalTime(), signed.ExpiresAt);
    }

    [Fact]
    public void Create_allows_a_lifetime_at_the_maximum()
    {
        var signed = SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, SignedAssetUrl.MaxLifetime);

        Assert.Equal(_issuedAt + SignedAssetUrl.MaxLifetime, signed.ExpiresAt);
    }

    [Fact]
    public void Create_rejects_a_null_url()
        => Assert.Throws<ArgumentNullException>(
            () => SignedAssetUrl.Create(null!, AssetStorageOperation.Download, _issuedAt, TimeSpan.FromMinutes(15)));

    [Fact]
    public void Create_rejects_a_relative_url()
    {
        var relative = new Uri("/livecore-private-assets/org/ws/asset-001.bin", UriKind.Relative);

        Assert.Throws<ArgumentException>(
            () => SignedAssetUrl.Create(relative, AssetStorageOperation.Download, _issuedAt, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Create_rejects_an_undefined_operation()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SignedAssetUrl.Create(_url, (AssetStorageOperation)999, _issuedAt, TimeSpan.FromMinutes(15)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void Create_rejects_a_non_positive_lifetime(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Create_rejects_a_lifetime_above_the_maximum()
    {
        // A signed URL must be short-lived: just over the ceiling is rejected, so a long-lived or
        // effectively permanent URL is unrepresentable (threat T4; docs/12_STORAGE_ASSETS.md).
        var tooLong = SignedAssetUrl.MaxLifetime + TimeSpan.FromSeconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, tooLong));
    }

    [Fact]
    public void IsExpired_is_false_before_expiry_and_true_at_or_after()
    {
        var lifetime = TimeSpan.FromMinutes(15);
        var signed = SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, lifetime);

        Assert.False(signed.IsExpired(_issuedAt));
        Assert.False(signed.IsExpired(signed.ExpiresAt - TimeSpan.FromSeconds(1)));
        Assert.True(signed.IsExpired(signed.ExpiresAt));
        Assert.True(signed.IsExpired(signed.ExpiresAt + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ToString_is_log_safe_and_never_contains_the_secret_url()
    {
        var signed = SignedAssetUrl.Create(_url, AssetStorageOperation.Download, _issuedAt, TimeSpan.FromMinutes(15));

        var text = signed.ToString();

        // The signed URL embeds the object key and the signature and is itself a secret, so it must never
        // appear in a log line (threats T4/T7). The operation and expiry are safe identifiers.
        Assert.DoesNotContain("storage.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("asset-001.bin", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", text, StringComparison.Ordinal);
        Assert.Contains("Download", text, StringComparison.Ordinal);
    }
}
