// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="PurchaseVerificationRequest"/> (CORE-STORE-001) — the provider-neutral input to
/// verification. The security-relevant case is that the request keeps the submitted <see cref="PurchaseVerificationRequest.Proof"/>
/// (an unverified, credential-like token) OUT of its <c>ToString</c>, so it can never leak through a log line
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Generic Store vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseVerificationRequestTests
{
    [Fact]
    public void Create_keeps_the_proof_verbatim_and_trims_an_optional_product_reference()
    {
        var request = PurchaseVerificationRequest.Create(PurchaseProvider.Apple, "  signed.jws.proof  ", "  product.premium  ");

        Assert.Equal(PurchaseProvider.Apple, request.Provider);
        // Provider verification material is never mutated — it is stored exactly as submitted.
        Assert.Equal("  signed.jws.proof  ", request.Proof);
        Assert.Equal("product.premium", request.ProductReference);
    }

    [Fact]
    public void Create_allows_an_omitted_product_reference()
    {
        var request = PurchaseVerificationRequest.Create(PurchaseProvider.Google, "purchase-token");

        Assert.Null(request.ProductReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_proof(string? proof)
        => Assert.Throws<ArgumentException>(
            () => PurchaseVerificationRequest.Create(PurchaseProvider.Apple, proof!));

    [Fact]
    public void Create_rejects_an_oversized_proof()
    {
        var tooLong = new string('a', PurchaseVerificationRequest.MaxProofLength + 1);

        Assert.Throws<ArgumentException>(
            () => PurchaseVerificationRequest.Create(PurchaseProvider.Apple, tooLong));
    }

    [Fact]
    public void Create_rejects_a_blank_or_oversized_product_reference_when_supplied()
    {
        Assert.Throws<ArgumentException>(
            () => PurchaseVerificationRequest.Create(PurchaseProvider.Apple, "proof", "   "));

        var tooLong = new string('p', PurchaseVerificationRequest.MaxProductReferenceLength + 1);
        Assert.Throws<ArgumentException>(
            () => PurchaseVerificationRequest.Create(PurchaseProvider.Apple, "proof", tooLong));
    }

    [Fact]
    public void Create_rejects_an_undefined_provider()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => PurchaseVerificationRequest.Create((PurchaseProvider)999, "proof"));

    [Fact]
    public void ToString_excludes_the_proof_so_it_is_never_logged()
    {
        const string secretProof = "super-secret-signed-transaction-jws";
        var request = PurchaseVerificationRequest.Create(PurchaseProvider.Apple, secretProof, "product.premium");

        var text = request.ToString();

        Assert.DoesNotContain(secretProof, text, StringComparison.Ordinal);
        Assert.Contains("Apple", text, StringComparison.Ordinal);
        Assert.Contains("hasProductReference=True", text, StringComparison.Ordinal);
    }
}
