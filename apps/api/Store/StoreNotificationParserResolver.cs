namespace LiveCore.Api.Store;

/// <summary>
/// Resolves the <see cref="IStoreNotificationParser"/> for a given <see cref="PurchaseProvider"/>
/// (CORE-STORE-005) — the fail-closed seam the notification endpoints use to validate and normalize an inbound
/// store notification without knowing any store's notification protocol. It is the notification analogue of
/// <see cref="PurchaseVerificationProviderResolver"/> (CORE-STORE-001): built from the set of parser adapters
/// registered for the deployment (each tagged with its <see cref="IStoreNotificationParser.Provider"/>), it hands
/// back the matching one, so the caller selects a parser by the generic <see cref="PurchaseProvider"/> enum alone.
///
/// FAIL-CLOSED BY DEFAULT. Core registers NO parser adapter (the concrete, credential-bearing validators are
/// deployment-supplied; docs/13_SELF_HOSTING_REQUIREMENTS.md), so out of the box <see cref="Resolve"/> throws
/// <see cref="StoreNotificationParserNotConfiguredException"/> for every provider. Because a notification endpoint
/// is unauthenticated (csv/mobile_store_api_routes.csv), this fail-closed default is what stops an unvalidated
/// payload from ever changing a purchase: with nothing configured, an inbound notification is 503 and nothing
/// happens. A deployment registers an adapter per provider it supports, and that provider then resolves to the
/// real validator.
/// </summary>
public sealed class StoreNotificationParserResolver
{
    private readonly IReadOnlyDictionary<PurchaseProvider, IStoreNotificationParser> _parsers;

    /// <summary>
    /// Builds the resolver from the registered parser adapters. At most one adapter may serve a given provider: a
    /// duplicate is a deployment misconfiguration and is rejected at construction (fail fast) rather than silently
    /// picking one, because an ambiguous validator choice must never decide whether a notification is authentic.
    /// </summary>
    /// <param name="parsers">The registered parser adapters (empty when none is configured).</param>
    /// <exception cref="ArgumentNullException">The parser collection (or any element) is null.</exception>
    /// <exception cref="ArgumentException">More than one adapter is registered for the same provider.</exception>
    public StoreNotificationParserResolver(IEnumerable<IStoreNotificationParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);

        var byProvider = new Dictionary<PurchaseProvider, IStoreNotificationParser>();
        foreach (var parser in parsers)
        {
            ArgumentNullException.ThrowIfNull(parser);

            if (!byProvider.TryAdd(parser.Provider, parser))
            {
                throw new ArgumentException(
                    $"More than one store notification parser is registered for the {parser.Provider} store.",
                    nameof(parsers));
            }
        }

        _parsers = byProvider;
    }

    /// <summary>
    /// Returns the parser adapter for <paramref name="provider"/>. Fails closed: if no adapter is configured for
    /// the provider, it throws <see cref="StoreNotificationParserNotConfiguredException"/> rather than returning
    /// any kind of permissive default, so validation can never be silently skipped.
    /// </summary>
    /// <param name="provider">The store to resolve a parser for.</param>
    /// <returns>The configured adapter for the provider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not a defined value.</exception>
    /// <exception cref="StoreNotificationParserNotConfiguredException">No adapter is configured for the provider (fail-closed).</exception>
    public IStoreNotificationParser Resolve(PurchaseProvider provider)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (!_parsers.TryGetValue(provider, out var adapter))
        {
            throw new StoreNotificationParserNotConfiguredException(provider);
        }

        return adapter;
    }
}
