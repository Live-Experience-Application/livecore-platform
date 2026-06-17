// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="StoreNotificationParserResolver"/> (CORE-STORE-005) — the FAIL-CLOSED seam the
/// unauthenticated store notification endpoints use to validate an inbound notification. Because the routes carry
/// no OIDC token, this resolver's fail-closed default is the control that stops an unvalidated payload from ever
/// changing a purchase: with no adapter configured, every resolve throws.
/// </summary>
public sealed class StoreNotificationParserResolverTests
{
    [Theory]
    [InlineData(PurchaseProvider.Apple)]
    [InlineData(PurchaseProvider.Google)]
    public void Resolve_fails_closed_when_no_parser_is_configured(PurchaseProvider provider)
    {
        // Core registers no parser adapter (the deployment supplies them), so resolution fails closed.
        var resolver = new StoreNotificationParserResolver([]);

        var exception = Assert.Throws<StoreNotificationParserNotConfiguredException>(() => resolver.Resolve(provider));
        Assert.Equal(provider, exception.Provider);
    }

    [Fact]
    public void Resolve_returns_the_adapter_registered_for_the_provider()
    {
        var apple = new StubParser(PurchaseProvider.Apple);
        var google = new StubParser(PurchaseProvider.Google);
        var resolver = new StoreNotificationParserResolver([apple, google]);

        Assert.Same(apple, resolver.Resolve(PurchaseProvider.Apple));
        Assert.Same(google, resolver.Resolve(PurchaseProvider.Google));
    }

    [Fact]
    public void A_provider_with_no_adapter_still_fails_closed_when_another_is_configured()
    {
        // Only Apple is wired; a Google notification must still fail closed rather than fall back to Apple's parser.
        var resolver = new StoreNotificationParserResolver([new StubParser(PurchaseProvider.Apple)]);

        Assert.Throws<StoreNotificationParserNotConfiguredException>(() => resolver.Resolve(PurchaseProvider.Google));
    }

    [Fact]
    public void Two_adapters_for_the_same_provider_is_a_misconfiguration_rejected_at_construction()
    {
        // An ambiguous validator choice must never decide whether a notification is authentic, so a duplicate is
        // rejected fail-fast at construction (mirrors the verification provider resolver).
        Assert.Throws<ArgumentException>(() =>
            new StoreNotificationParserResolver([new StubParser(PurchaseProvider.Apple), new StubParser(PurchaseProvider.Apple)]));
    }

    private sealed class StubParser : IStoreNotificationParser
    {
        public StubParser(PurchaseProvider provider) => Provider = provider;

        public PurchaseProvider Provider { get; }

        public Task<StoreNotificationParseResult> ParseAsync(StoreNotificationEnvelope envelope, CancellationToken cancellationToken)
            => Task.FromResult(StoreNotificationParseResult.Ignored());
    }
}
