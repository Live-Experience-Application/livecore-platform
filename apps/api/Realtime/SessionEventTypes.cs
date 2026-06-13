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

    /// <summary>
    /// A host hid (un-revealed) a resource — the inverse of <see cref="ContentRevealed"/>, instructing
    /// recipients who could see the resource to stop showing it (docs/09_EVENT_CATALOG.md:
    /// "ContentHidden"). Emitted by the hide command (CORE-REV-001) when a hide actually changes
    /// visibility. Unlike a reveal, a hide event carries NO visibility subject: the resource is now hidden,
    /// so a subject-gated projection would (correctly, for a reveal) exclude the very audience that must be
    /// told to remove it; instead the event is routed by its coarse target — a selected-participant hide
    /// reaches only that participant (plus hosts), an audience-wide hide reaches the observers and every
    /// active participant — and carries resource IDENTIFIERS only, never content (threats T2/T3/T7).
    /// </summary>
    public const string ContentHidden = "ContentHidden";
}
