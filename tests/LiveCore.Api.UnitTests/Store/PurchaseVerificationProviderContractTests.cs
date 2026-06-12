using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Contract tests for the <see cref="IPurchaseVerificationProvider"/> port (CORE-STORE-001), exercised through
/// conforming in-memory fake adapters (the deployment supplies the real, provider-specific verifiers with their
/// SDKs and store credentials — docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7). They assert the properties
/// EVERY adapter must uphold and that the abstraction genuinely isolates provider differences: each adapter
/// reduces a provider-shaped outcome to the SAME provider-neutral <see cref="PurchaseVerificationResult"/>
/// (a normalized <see cref="VerifiedPurchase"/> on success, a generic rejection otherwise), and the
/// <see cref="PurchaseVerificationProviderResolver"/> lets the same calling code drive an Apple OR a Google
/// adapter without branching on the provider — "Apple/Google provider logic is isolated from Core domain logic"
/// (the epic acceptance criterion). Generic Store vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseVerificationProviderContractTests
{
    [Theory]
    [InlineData(PurchaseProvider.Apple)]
    [InlineData(PurchaseProvider.Google)]
    public async Task A_genuine_proof_verifies_to_a_normalized_provider_neutral_purchase(PurchaseProvider provider)
    {
        IPurchaseVerificationProvider adapter = new FakeProvider(provider);
        var request = PurchaseVerificationRequest.Create(provider, "genuine-proof", "product.premium");

        var result = await adapter.VerifyAsync(request, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(PurchaseVerificationStatus.Verified, result.Status);
        Assert.Null(result.RejectionReason);

        // The normalized result names the same provider and product, whichever store produced it — Core branches
        // on one neutral shape, never on a provider-specific payload.
        Assert.NotNull(result.Purchase);
        Assert.Equal(provider, result.Purchase!.Provider);
        Assert.Equal("product.premium", result.Purchase.ProductReference);
        Assert.False(string.IsNullOrWhiteSpace(result.Purchase.ProviderTransactionId));
    }

    [Theory]
    [InlineData(PurchaseProvider.Apple)]
    [InlineData(PurchaseProvider.Google)]
    public async Task A_forged_proof_is_rejected_with_a_generic_reason_and_no_purchase(PurchaseProvider provider)
    {
        // The crown-jewel negative: an unverifiable proof must NOT yield a purchase. No entitlement can be
        // granted off a rejected result ("Never trust client-side premium flags", docs/21).
        IPurchaseVerificationProvider adapter = new FakeProvider(provider);
        var request = PurchaseVerificationRequest.Create(provider, FakeProvider.ForgedProof, "product.premium");

        var result = await adapter.VerifyAsync(request, CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(PurchaseVerificationStatus.Rejected, result.Status);
        Assert.Null(result.Purchase);
        Assert.False(string.IsNullOrWhiteSpace(result.RejectionReason));

        // The generic rejection reason never echoes the submitted proof (threat T7).
        Assert.DoesNotContain(FakeProvider.ForgedProof, result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_resolver_drives_each_provider_through_the_same_calling_code()
    {
        // The same five lines verify against Apple and Google with no provider-specific branching: this is the
        // isolation the story delivers.
        var resolver = new PurchaseVerificationProviderResolver(
            [new FakeProvider(PurchaseProvider.Apple), new FakeProvider(PurchaseProvider.Google)]);

        foreach (var provider in new[] { PurchaseProvider.Apple, PurchaseProvider.Google })
        {
            var request = PurchaseVerificationRequest.Create(provider, "genuine-proof", "product.premium");
            var result = await resolver.Resolve(provider).VerifyAsync(request, CancellationToken.None);

            Assert.True(result.IsVerified);
            Assert.Equal(provider, result.Purchase!.Provider);
        }
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_null_request()
    {
        IPurchaseVerificationProvider adapter = new FakeProvider(PurchaseProvider.Apple);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => adapter.VerifyAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A deterministic in-memory <see cref="IPurchaseVerificationProvider"/> standing in for a real,
    /// deployment-supplied verifier. It "verifies" any proof except the sentinel <see cref="ForgedProof"/>,
    /// which it rejects — enough to exercise the port contract without contacting a real store API. It is NOT a
    /// production verifier.
    /// </summary>
    private sealed class FakeProvider(PurchaseProvider provider) : IPurchaseVerificationProvider
    {
        public const string ForgedProof = "forged-proof";

        public PurchaseProvider Provider { get; } = provider;

        public Task<PurchaseVerificationResult> VerifyAsync(
            PurchaseVerificationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.Equals(request.Proof, ForgedProof, StringComparison.Ordinal))
            {
                return Task.FromResult(PurchaseVerificationResult.Rejected("The proof could not be verified."));
            }

            var purchase = VerifiedPurchase.Create(
                Provider,
                $"{Provider}-txn-0001",
                request.ProductReference ?? "product.unknown");

            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }
}
