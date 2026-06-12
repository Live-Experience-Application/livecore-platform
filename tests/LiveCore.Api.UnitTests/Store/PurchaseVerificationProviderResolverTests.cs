using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for the <see cref="PurchaseVerificationProviderResolver"/> (CORE-STORE-001) — the fail-closed
/// seam that selects an <see cref="IPurchaseVerificationProvider"/> by the generic <see cref="PurchaseProvider"/>
/// so Core never touches a store's protocol directly ("Apple/Google provider logic is isolated from Core domain
/// logic", the epic acceptance criterion). The security-relevant cases here are the FAIL-CLOSED ones: with no
/// adapter configured for a provider the resolver denies (throws) rather than returning a permissive default, so
/// Core never grants premium state without a real verification ("Never unlock limits before server verification
/// succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). Generic Store vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseVerificationProviderResolverTests
{
    [Fact]
    public void Resolve_returns_the_adapter_registered_for_the_requested_provider()
    {
        var apple = new StubProvider(PurchaseProvider.Apple);
        var google = new StubProvider(PurchaseProvider.Google);
        var resolver = new PurchaseVerificationProviderResolver([apple, google]);

        Assert.Same(apple, resolver.Resolve(PurchaseProvider.Apple));
        Assert.Same(google, resolver.Resolve(PurchaseProvider.Google));
    }

    [Fact]
    public void Resolve_fails_closed_when_no_adapter_is_configured_for_the_provider()
    {
        // Core registers no adapter by default (deployment supplies them). A provider with none configured must
        // DENY, not silently succeed: verification can never be skipped (docs/21).
        var resolver = new PurchaseVerificationProviderResolver([new StubProvider(PurchaseProvider.Apple)]);

        var exception = Assert.Throws<PurchaseProviderNotConfiguredException>(
            () => resolver.Resolve(PurchaseProvider.Google));

        Assert.Equal(PurchaseProvider.Google, exception.Provider);
    }

    [Fact]
    public void Resolve_fails_closed_when_no_adapters_are_configured_at_all()
    {
        // The default registration: an EMPTY resolver. Every provider fails closed — the purchase-verification
        // analogue of the fail-closed UnconfiguredAssetStorage (CORE-AST-002).
        var resolver = new PurchaseVerificationProviderResolver([]);

        Assert.Throws<PurchaseProviderNotConfiguredException>(() => resolver.Resolve(PurchaseProvider.Apple));
        Assert.Throws<PurchaseProviderNotConfiguredException>(() => resolver.Resolve(PurchaseProvider.Google));
    }

    [Fact]
    public void The_fail_closed_exception_message_leaks_no_secret_and_names_only_the_provider()
    {
        var resolver = new PurchaseVerificationProviderResolver([]);

        var exception = Assert.Throws<PurchaseProviderNotConfiguredException>(
            () => resolver.Resolve(PurchaseProvider.Apple));

        // Log-safe: the message names only the provider, never a proof or credential (threat T7).
        Assert.Contains("Apple", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
    }

    [Fact]
    public void Resolve_rejects_an_undefined_provider_value()
    {
        var resolver = new PurchaseVerificationProviderResolver([]);

        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve((PurchaseProvider)999));
    }

    [Fact]
    public void Constructing_with_two_adapters_for_the_same_provider_is_rejected()
    {
        // An ambiguous verifier choice must never decide premium state, so a duplicate registration fails fast
        // at construction rather than silently picking one.
        Assert.Throws<ArgumentException>(() => new PurchaseVerificationProviderResolver(
            [new StubProvider(PurchaseProvider.Apple), new StubProvider(PurchaseProvider.Apple)]));
    }

    [Fact]
    public void Constructing_rejects_a_null_collection_or_a_null_adapter()
    {
        Assert.Throws<ArgumentNullException>(() => new PurchaseVerificationProviderResolver(null!));
        Assert.Throws<ArgumentNullException>(() => new PurchaseVerificationProviderResolver([null!]));
    }

    /// <summary>
    /// A minimal <see cref="IPurchaseVerificationProvider"/> that only declares which provider it serves; its
    /// <see cref="VerifyAsync"/> is never exercised by these resolver tests (the contract tests cover verifying).
    /// </summary>
    private sealed class StubProvider(PurchaseProvider provider) : IPurchaseVerificationProvider
    {
        public PurchaseProvider Provider { get; } = provider;

        public Task<PurchaseVerificationResult> VerifyAsync(
            PurchaseVerificationRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The resolver tests never verify.");
    }
}
