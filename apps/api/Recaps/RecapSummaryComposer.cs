using System.Globalization;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Recaps;

/// <summary>
/// Composes the generic, product-neutral SYSTEM recap body from a concluded session's own append-only event
/// stream (CORE-RCP-002, "Generate recaps from the session event stream"). It is the piece that makes an
/// automatic recap REFLECT WHAT ACTUALLY HAPPENED in the session — derived from <c>session_events</c>, not just
/// the start/end timestamps (the acceptance criterion). The previous body was content-free boilerplate built
/// from the two timeline timestamps alone; this composer additionally summarizes the durable event stream the
/// session produced.
///
/// <para>
/// VISIBILITY-SAFE PROJECTION (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). The recap body is HOST
/// content — the <see cref="RecapProjection"/> already fails closed so the audience never receives it until a
/// separate reveal (docs/09_EVENT_CATALOG.md <c>RecapGenerated</c>: "participant recap requires separate
/// reveal") — so the host is entitled to a summary of the whole stream. To keep even that host-only body free
/// of resolved content, this composer reads ONLY each event's generic, product-neutral
/// <see cref="SessionEvent.EventType"/> name (docs/09_EVENT_CATALOG.md): never the server-composed
/// <see cref="SessionEvent.Payload"/>, the visibility-subject ids, the actor or the target. The body therefore
/// carries aggregate COUNTS of generic event kinds and the UTC timeline only — never a session title, a
/// resource id or any free-form content (threat T7), exactly as the rest of the Recaps module keeps content out
/// of logs and out of the audience view.
/// </para>
///
/// <para>
/// DETERMINISTIC AND SAFE ON EMPTY/PARTIAL STREAMS (the acceptance criterion). The output is a pure function of
/// the session's timeline timestamps and the multiset of event-type names: the per-kind tally is
/// order-independent, the breakdown is emitted in a FIXED category order, and every timestamp is normalized to
/// UTC and rendered with the invariant round-trip ("O") format, so a non-UTC offset or an edge timestamp yields
/// one canonical string. A zero-event session yields a safe, non-blank "no activity recorded" body, and a
/// partial stream (for example a defensively-null <see cref="RecapEligibleSession.StartedAt"/>) degrades to the
/// reduced timeline sentence rather than throwing. The body is always a valid <see cref="Recap"/> summary
/// (non-blank and well within <see cref="Recap.MaxSummaryLength"/>).
/// </para>
///
/// <para>
/// PRODUCT-NEUTRAL. Every category label is generic Core vocabulary (scene, content, participant, visibility,
/// session) and carries no vertical term (AGENTS.md, csv/forbidden_core_terms.csv); a vertical maps the recap
/// to its own wording in its UI.
/// </para>
/// </summary>
internal static class RecapSummaryComposer
{
    /// <summary>
    /// The fixed, ordered mapping from a generic <see cref="SessionEventTypes"/> name to its human-readable
    /// (singular, plural) recap label. The order is the presentation order of the breakdown, leading with the
    /// activity the acceptance criterion names (scene activations and content reveals). Any event whose type is
    /// not in this list is still counted, folded into the trailing "other event(s)" bucket so the breakdown
    /// always sums to the total — a future catalog event therefore never goes silently uncounted.
    /// </summary>
    private static readonly (string EventType, string Singular, string Plural)[] _categories =
    [
        (SessionEventTypes.SceneActivated, "scene activation", "scene activations"),
        (SessionEventTypes.ContentRevealed, "content reveal", "content reveals"),
        (SessionEventTypes.ContentHidden, "content hide", "content hides"),
        (SessionEventTypes.VisibilityRuleChanged, "visibility change", "visibility changes"),
        (SessionEventTypes.ParticipantJoined, "participant join", "participant joins"),
        (SessionEventTypes.ParticipantLeft, "participant departure", "participant departures"),
        (SessionEventTypes.SessionStarted, "session start", "session starts"),
        (SessionEventTypes.SessionEnded, "session end", "session ends"),
    ];

    /// <summary>
    /// Composes the deterministic, content-free recap body for the given concluded session from its append-only
    /// event stream. The <paramref name="events"/> are the session's durable events (any order — the tally is
    /// order-independent); only their generic <see cref="SessionEvent.EventType"/> names are read. A null or
    /// empty stream yields a safe "no activity recorded" body.
    /// </summary>
    /// <param name="session">The eligible session's coordinates and live-timeline timestamps.</param>
    /// <param name="events">The session's append-only event stream (may be empty; null is treated as empty).</param>
    /// <returns>A generic, product-neutral, valid recap summary body.</returns>
    public static string Compose(RecapEligibleSession session, IReadOnlyList<SessionEvent>? events)
    {
        var timeline = ComposeTimeline(session);
        var activity = ComposeActivity(events);
        return $"Automated recap for a concluded session. {timeline} {activity}";
    }

    /// <summary>
    /// The timeline sentence, derived from the session's own live-timeline timestamps normalized to UTC. A
    /// missing start (a defensive partial record) degrades to the reduced sentence rather than throwing.
    /// </summary>
    private static string ComposeTimeline(RecapEligibleSession session)
    {
        var startedAt = session.StartedAt?.ToUniversalTime();
        var endedAt = session.EndedAt?.ToUniversalTime();

        return (startedAt, endedAt) switch
        {
            ({ } start, { } end) => $"Live timeline ran from {Format(start)} to {Format(end)}.",
            (null, { } end) => $"Session ended at {Format(end)}.",
            _ => "The session has concluded.",
        };
    }

    /// <summary>
    /// The activity sentence: the total event count and a fixed-order breakdown by generic event kind. An empty
    /// stream yields the safe "no activity recorded" sentence (the deterministic empty recap).
    /// </summary>
    private static string ComposeActivity(IReadOnlyList<SessionEvent>? events)
    {
        if (events is null || events.Count == 0)
        {
            return "No session activity was recorded.";
        }

        // Tally by the generic event-type name ONLY (the visibility-safe projection): never the payload, the
        // visibility subject, the actor or the target, so no resolved content can reach the body (threat T7).
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sessionEvent in events)
        {
            counts.TryGetValue(sessionEvent.EventType, out var current);
            counts[sessionEvent.EventType] = current + 1;
        }

        var parts = new List<string>(_categories.Length + 1);
        var accountedFor = 0;
        foreach (var (eventType, singular, plural) in _categories)
        {
            if (counts.TryGetValue(eventType, out var count) && count > 0)
            {
                parts.Add(Quantify(count, singular, plural));
                accountedFor += count;
            }
        }

        // Any event whose type is not a known category is still counted (so the breakdown sums to the total).
        var other = events.Count - accountedFor;
        if (other > 0)
        {
            parts.Add(Quantify(other, "other event", "other events"));
        }

        var total = Quantify(events.Count, "event", "events");
        return parts.Count == 0
            ? $"The session recorded {total}."
            : $"The session recorded {total}: {string.Join(", ", parts)}.";
    }

    /// <summary>Renders a count with its singular/plural label, e.g. "1 scene activation" / "2 scene activations".</summary>
    private static string Quantify(int count, string singular, string plural)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>Renders a UTC timestamp in the invariant round-trip ("O") format, so the body is deterministic.</summary>
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
