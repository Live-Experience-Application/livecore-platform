namespace LiveCore.Api.Realtime;

/// <summary>
/// The Core-level catalog of session event type names (CORE-RT-003), the product-neutral
/// <c>eventType</c> values of docs/09_EVENT_CATALOG.md. Names are generic Core events, never vertical
/// terms. Extensible: later stories add members as their commands begin emitting events (the
/// <c>SessionStarted</c>/<c>SessionEnded</c> deferred by CORE-SES-004 and the other catalog events).
/// </summary>
public static class SessionEventTypes
{
    /// <summary>
    /// A host revealed a resource to the audience or to a selected participant — the central
    /// participant-facing event (docs/09_EVENT_CATALOG.md: "ContentRevealed | Host/CoHost | selected
    /// recipients | yes | central event"). Emitted by the reveal command (CORE-VIS-004/005) when a
    /// reveal actually changes visibility.
    /// </summary>
    public const string ContentRevealed = "ContentRevealed";
}
