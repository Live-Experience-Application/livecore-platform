// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for the <see cref="PushSubscription"/> aggregate (CORE-PUSH-001). They pin the per-principal
/// invariants: a subscription is created for a non-empty principal with a valid absolute push endpoint and
/// non-empty bounded keys, its endpoint/owning principal are immutable while its keys can be refreshed in place,
/// and its <see cref="object.ToString"/> never leaks the endpoint or the encryption secret (threat T7).
/// </summary>
public class PushSubscriptionTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_creates_a_valid_subscription()
    {
        var userId = Guid.CreateVersion7();
        var subscription = PushSubscription.Register(
            userId, "https://push.example.test/sub/abc", "p256dh-key", "auth-secret", _now);

        Assert.NotEqual(Guid.Empty, subscription.Id);
        Assert.Equal(userId, subscription.UserProfileId);
        Assert.Equal("https://push.example.test/sub/abc", subscription.Endpoint);
        Assert.Equal("p256dh-key", subscription.P256dh);
        Assert.Equal("auth-secret", subscription.Auth);
        Assert.Equal(_now, subscription.CreatedAt);
        Assert.Equal(_now, subscription.UpdatedAt);
    }

    [Fact]
    public void Register_rejects_an_empty_principal()
        => Assert.Throws<ArgumentException>(() => PushSubscription.Register(
            Guid.Empty, "https://push.example.test/sub/abc", "p256dh", "auth", _now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://push.example.test/sub")]
    [InlineData("/relative/path")]
    public void Register_rejects_an_invalid_endpoint(string endpoint)
        => Assert.Throws<ArgumentException>(() => PushSubscription.Register(
            Guid.CreateVersion7(), endpoint, "p256dh", "auth", _now));

    [Theory]
    [InlineData("", "auth")]
    [InlineData("   ", "auth")]
    [InlineData("p256dh", "")]
    [InlineData("p256dh", "   ")]
    public void Register_rejects_a_blank_key(string p256dh, string auth)
        => Assert.Throws<ArgumentException>(() => PushSubscription.Register(
            Guid.CreateVersion7(), "https://push.example.test/sub/abc", p256dh, auth, _now));

    [Fact]
    public void Register_rejects_an_over_length_endpoint()
    {
        var endpoint = "https://push.example.test/sub/" + new string('a', PushSubscription.MaxEndpointLength);
        Assert.Throws<ArgumentException>(() => PushSubscription.Register(
            Guid.CreateVersion7(), endpoint, "p256dh", "auth", _now));
    }

    [Fact]
    public void RefreshKeys_rotates_the_keys_and_bumps_updated_at()
    {
        var subscription = PushSubscription.Register(
            Guid.CreateVersion7(), "https://push.example.test/sub/abc", "p256dh-v1", "auth-v1", _now);
        var later = _now.AddMinutes(5);

        subscription.RefreshKeys("p256dh-v2", "auth-v2", later);

        Assert.Equal("p256dh-v2", subscription.P256dh);
        Assert.Equal("auth-v2", subscription.Auth);
        Assert.Equal(later, subscription.UpdatedAt);
        // The endpoint and owning principal are immutable: a subscription is never reassigned (threat T5).
        Assert.Equal("https://push.example.test/sub/abc", subscription.Endpoint);
    }

    [Fact]
    public void RefreshKeys_rejects_a_blank_key()
    {
        var subscription = PushSubscription.Register(
            Guid.CreateVersion7(), "https://push.example.test/sub/abc", "p256dh", "auth", _now);

        Assert.Throws<ArgumentException>(() => subscription.RefreshKeys("", "auth", _now));
    }

    [Fact]
    public void ToString_leaks_neither_the_endpoint_nor_the_secret()
    {
        var subscription = PushSubscription.Register(
            Guid.CreateVersion7(), "https://push.example.test/sub/secret-endpoint", "p256dh", "auth-secret", _now);

        var text = subscription.ToString();

        Assert.DoesNotContain("secret-endpoint", text);
        Assert.DoesNotContain("auth-secret", text);
        Assert.Contains(subscription.Id.ToString(), text);
    }
}
