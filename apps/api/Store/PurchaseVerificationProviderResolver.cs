namespace LiveCore.Api.Store;

/// <summary>
/// Resolves the <see cref="IPurchaseVerificationProvider"/> for a given <see cref="PurchaseProvider"/>
/// (CORE-STORE-001) — the fail-closed seam Core domain logic uses to verify a purchase without knowing any
/// store's protocol. It is built from the set of provider adapters registered for the deployment (each tagged
/// with its <see cref="IPurchaseVerificationProvider.Provider"/>) and hands back the matching one, so the
/// caller selects a verifier by the generic <see cref="PurchaseProvider"/> enum alone — "Apple/Google provider
/// logic is isolated from Core domain logic" (the epic acceptance criterion).
///
/// FAIL-CLOSED BY DEFAULT. Core registers NO provider adapter (the concrete, credential-bearing adapters are
/// deployment-supplied; docs/13_SELF_HOSTING_REQUIREMENTS.md), so out of the box <see cref="Resolve"/> throws
/// <see cref="PurchaseProviderNotConfiguredException"/> for every provider. This is the purchase-verification
/// analogue of the fail-closed <c>UnconfiguredAssetStorage</c> (CORE-AST-002): with nothing configured Core
/// never trusts a client's unverified proof and never grants premium state, so "Never unlock limits before
/// server verification succeeds" (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md) holds even when no
/// provider is wired. A deployment registers an adapter per provider it supports, and that provider then
/// resolves to the real verifier.
/// </summary>
public sealed class PurchaseVerificationProviderResolver
{
    private readonly IReadOnlyDictionary<PurchaseProvider, IPurchaseVerificationProvider> _providers;

    /// <summary>
    /// Builds the resolver from the registered provider adapters. At most one adapter may serve a given
    /// provider: a duplicate is a deployment misconfiguration and is rejected at construction (fail fast) rather
    /// than silently picking one, because an ambiguous verifier choice must never decide premium state.
    /// </summary>
    /// <param name="providers">The registered provider adapters (empty when none is configured).</param>
    /// <exception cref="ArgumentNullException">The provider collection (or any element) is null.</exception>
    /// <exception cref="ArgumentException">More than one adapter is registered for the same provider.</exception>
    public PurchaseVerificationProviderResolver(IEnumerable<IPurchaseVerificationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byProvider = new Dictionary<PurchaseProvider, IPurchaseVerificationProvider>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (!byProvider.TryAdd(provider.Provider, provider))
            {
                throw new ArgumentException(
                    $"More than one purchase verification provider is registered for the {provider.Provider} store.",
                    nameof(providers));
            }
        }

        _providers = byProvider;
    }

    /// <summary>
    /// Returns the verification adapter for <paramref name="provider"/>. Fails closed: if no adapter is
    /// configured for the provider, it throws <see cref="PurchaseProviderNotConfiguredException"/> rather than
    /// returning any kind of permissive default, so verification can never be silently skipped.
    /// </summary>
    /// <param name="provider">The store to resolve a verifier for.</param>
    /// <returns>The configured adapter for the provider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not a defined value.</exception>
    /// <exception cref="PurchaseProviderNotConfiguredException">No adapter is configured for the provider (fail-closed).</exception>
    public IPurchaseVerificationProvider Resolve(PurchaseProvider provider)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (!_providers.TryGetValue(provider, out var adapter))
        {
            throw new PurchaseProviderNotConfiguredException(provider);
        }

        return adapter;
    }
}
