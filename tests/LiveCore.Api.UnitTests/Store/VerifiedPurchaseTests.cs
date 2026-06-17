// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="VerifiedPurchase"/> (CORE-STORE-001) — the provider-neutral, normalized identity a
/// provider adapter produces after a successful verification. It is the common shape Core domain logic consumes
/// instead of an Apple/Google-specific payload (the epic acceptance criterion). Generic Store vocabulary only
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class VerifiedPurchaseTests
{
    [Fact]
    public void Create_normalizes_identifiers_and_keeps_the_provider()
    {
        var purchase = VerifiedPurchase.Create(
            PurchaseProvider.Google,
            "  order-123  ",
            "  product.premium  ",
            PurchaseEnvironment.Production);

        Assert.Equal(PurchaseProvider.Google, purchase.Provider);
        Assert.Equal("order-123", purchase.ProviderTransactionId);
        Assert.Equal("product.premium", purchase.ProductReference);
        Assert.Equal(PurchaseEnvironment.Production, purchase.Environment);
    }

    [Fact]
    public void Create_defaults_the_environment_to_sandbox_fail_closed()
    {
        // CORE-MON-008: an adapter that omits the environment gets the fail-closed default (Sandbox), the value a
        // production deployment refuses to honor — so under-reporting can never silently unlock premium in prod.
        var purchase = VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", "product.premium");

        Assert.Equal(PurchaseEnvironment.Sandbox, purchase.Environment);
    }

    [Fact]
    public void Create_keeps_the_reported_environment()
    {
        var sandbox = VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", "product.premium", PurchaseEnvironment.Sandbox);
        var production = VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-2", "product.premium", PurchaseEnvironment.Production);

        Assert.Equal(PurchaseEnvironment.Sandbox, sandbox.Environment);
        Assert.Equal(PurchaseEnvironment.Production, production.Environment);
    }

    [Fact]
    public void Create_rejects_an_undefined_environment()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", "product.premium", (PurchaseEnvironment)999));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_transaction_id(string? transactionId)
        => Assert.Throws<ArgumentException>(
            () => VerifiedPurchase.Create(PurchaseProvider.Apple, transactionId!, "product.premium"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_product_reference(string? productReference)
        => Assert.Throws<ArgumentException>(
            () => VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", productReference!));

    [Fact]
    public void Create_rejects_an_oversized_identifier()
    {
        var tooLong = new string('x', VerifiedPurchase.MaxIdentifierLength + 1);

        Assert.Throws<ArgumentException>(
            () => VerifiedPurchase.Create(PurchaseProvider.Apple, tooLong, "product.premium"));
        Assert.Throws<ArgumentException>(
            () => VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", tooLong));
    }

    [Fact]
    public void Create_rejects_an_undefined_provider()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => VerifiedPurchase.Create((PurchaseProvider)999, "txn-1", "product.premium"));

    [Fact]
    public void ToString_carries_identifiers_for_logs()
    {
        var purchase = VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", "product.premium", PurchaseEnvironment.Production);

        var text = purchase.ToString();

        Assert.Contains("Apple", text, StringComparison.Ordinal);
        Assert.Contains("txn-1", text, StringComparison.Ordinal);
        Assert.Contains("product.premium", text, StringComparison.Ordinal);
        Assert.Contains("Production", text, StringComparison.Ordinal);
    }
}
