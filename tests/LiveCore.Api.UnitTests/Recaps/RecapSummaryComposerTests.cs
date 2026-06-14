using System.Globalization;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Unit tests for <see cref="RecapSummaryComposer"/> (CORE-RCP-002) — the pure composition of a system recap
/// body from a concluded session's append-only event stream. They cover the story's required behaviors: a
/// session with reveals/scene activations produces a recap SUMMARIZING those events; a zero-event session
/// produces a SAFE, deterministic empty recap; non-UTC/edge timestamps are handled (normalized to UTC); and —
/// because the recap body is host content — the composer leaks NO resolved content (it reads only the generic
/// event type, never the payload or visibility subject; threat T7). All fixtures are generic Core language
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class RecapSummaryComposerTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _sessionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _startUtc = new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endUtc = new(2026, 6, 13, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Summarizes_reveals_and_scene_activations()
    {
        var session = Session(_startUtc, _endUtc);
        var events = new[]
        {
            Event(SessionEventTypes.SceneActivated),
            Event(SessionEventTypes.SceneActivated),
            Event(SessionEventTypes.SceneActivated),
            Event(SessionEventTypes.ContentRevealed),
            Event(SessionEventTypes.ContentHidden),
            Event(SessionEventTypes.ParticipantJoined),
        };

        var summary = RecapSummaryComposer.Compose(session, events);

        Assert.Contains("recorded 6 events", summary);
        Assert.Contains("3 scene activations", summary);
        Assert.Contains("1 content reveal", summary);
        Assert.Contains("1 content hide", summary);
        Assert.Contains("1 participant join", summary);
        // Scene activations and content reveals lead the breakdown (the activity the acceptance criterion names).
        Assert.True(
            summary.IndexOf("scene activations", StringComparison.Ordinal)
                < summary.IndexOf("participant join", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_event_is_rendered_in_the_singular()
    {
        var summary = RecapSummaryComposer.Compose(Session(_startUtc, _endUtc), [Event(SessionEventTypes.SceneActivated)]);

        Assert.Contains("recorded 1 event:", summary);
        Assert.Contains("1 scene activation", summary);
        Assert.DoesNotContain("1 scene activations", summary);
    }

    [Fact]
    public void A_null_event_stream_produces_a_safe_empty_recap()
    {
        var summary = RecapSummaryComposer.Compose(Session(_startUtc, _endUtc), events: null);

        Assert.Contains("No session activity was recorded", summary);
        Assert.DoesNotContain("recorded 0 events", summary);
        Assert.True(Recap.IsValidSummary(summary));
    }

    [Fact]
    public void An_empty_event_list_produces_a_safe_empty_recap()
    {
        var summary = RecapSummaryComposer.Compose(Session(_startUtc, _endUtc), []);

        Assert.Contains("No session activity was recorded", summary);
        Assert.True(Recap.IsValidSummary(summary));
    }

    [Fact]
    public void Non_utc_timestamps_are_normalized_to_utc_deterministically()
    {
        // The same instants expressed with a non-UTC offset must yield the byte-for-byte UTC body.
        var session = Session(
            _startUtc.ToOffset(TimeSpan.FromHours(5.5)),
            _endUtc.ToOffset(TimeSpan.FromHours(-8)));

        var summary = RecapSummaryComposer.Compose(session, [Event(SessionEventTypes.ContentRevealed)]);

        Assert.Contains($"Live timeline ran from {Format(_startUtc)} to {Format(_endUtc)}.", summary);
        Assert.Equal(
            RecapSummaryComposer.Compose(Session(_startUtc, _endUtc), [Event(SessionEventTypes.ContentRevealed)]),
            summary);
    }

    [Fact]
    public void A_null_start_degrades_to_the_reduced_timeline()
    {
        var summary = RecapSummaryComposer.Compose(Session(null, _endUtc), []);

        Assert.Contains($"Session ended at {Format(_endUtc)}.", summary);
        Assert.DoesNotContain("Live timeline ran", summary);
        Assert.True(Recap.IsValidSummary(summary));
    }

    [Fact]
    public void A_zero_length_session_renders_an_equal_bounded_timeline()
    {
        // Edge case (CORE-TST-005): a session whose live timeline started and ended at the SAME instant — a
        // zero-length session — still composes a valid, deterministic body whose timeline names that one instant
        // for BOTH bounds, rather than throwing or producing a malformed range.
        var summary = RecapSummaryComposer.Compose(
            Session(_startUtc, _startUtc),
            [Event(SessionEventTypes.SceneActivated)]);

        Assert.Contains($"Live timeline ran from {Format(_startUtc)} to {Format(_startUtc)}.", summary);
        Assert.Contains("1 scene activation", summary);
        Assert.True(Recap.IsValidSummary(summary));
    }

    [Fact]
    public void Both_timestamps_null_still_produces_a_valid_body()
    {
        var summary = RecapSummaryComposer.Compose(Session(null, null), [Event(SessionEventTypes.SceneActivated)]);

        Assert.Contains("The session has concluded.", summary);
        Assert.Contains("1 scene activation", summary);
        Assert.True(Recap.IsValidSummary(summary));
    }

    [Fact]
    public void The_body_never_echoes_event_payload_or_visibility_subject_content()
    {
        // Threat T7: the recap body is host content but must still carry no resolved content. The composer reads
        // only the generic event TYPE, so a payload string and a visibility-subject id never appear in the body.
        const string secretPayload = "{\"resourceTitle\":\"do-not-leak-me\"}";
        var subjectId = Guid.NewGuid();
        var sensitive = SessionEvent.Create(
            _organizationId,
            _workspaceId,
            _sessionId,
            SessionEventTypes.SceneActivated,
            createdBy: Guid.NewGuid(),
            targetParticipantId: Guid.NewGuid(),
            payload: secretPayload,
            schemaVersion: 1,
            createdAt: _endUtc,
            visibilitySubjectType: "Scene",
            visibilitySubjectId: subjectId);

        var summary = RecapSummaryComposer.Compose(Session(_startUtc, _endUtc), [sensitive]);

        Assert.DoesNotContain("do-not-leak-me", summary);
        Assert.DoesNotContain(secretPayload, summary);
        Assert.DoesNotContain(subjectId.ToString(), summary);
        Assert.Contains("1 scene activation", summary);
    }

    [Fact]
    public void Composition_is_deterministic_and_order_independent()
    {
        var session = Session(_startUtc, _endUtc);
        var forward = new[]
        {
            Event(SessionEventTypes.SceneActivated),
            Event(SessionEventTypes.ContentRevealed),
            Event(SessionEventTypes.ContentRevealed),
        };
        var reversed = new[]
        {
            Event(SessionEventTypes.ContentRevealed),
            Event(SessionEventTypes.ContentRevealed),
            Event(SessionEventTypes.SceneActivated),
        };

        // The same input twice yields an identical body, and a different append order yields the same counts.
        Assert.Equal(
            RecapSummaryComposer.Compose(session, forward),
            RecapSummaryComposer.Compose(session, forward));
        Assert.Equal(
            RecapSummaryComposer.Compose(session, forward),
            RecapSummaryComposer.Compose(session, reversed));
    }

    [Fact]
    public void An_unrecognized_event_type_is_still_counted_in_the_total_and_other_bucket()
    {
        // A future catalog event (a type the composer has no named category for) is still counted so the
        // breakdown sums to the total, rather than being silently dropped.
        var session = Session(_startUtc, _endUtc);
        var events = new[]
        {
            Event(SessionEventTypes.SceneActivated),
            Event("SessionNoteCreated"),
            Event("SessionNoteCreated"),
        };

        var summary = RecapSummaryComposer.Compose(session, events);

        Assert.Contains("recorded 3 events", summary);
        Assert.Contains("1 scene activation", summary);
        Assert.Contains("2 other events", summary);
    }

    [Fact]
    public void The_body_is_always_a_valid_recap_summary()
    {
        var session = Session(_startUtc, _endUtc);
        var events = Enumerable.Range(0, 25).Select(_ => Event(SessionEventTypes.ContentRevealed)).ToArray();

        var summary = RecapSummaryComposer.Compose(session, events);

        Assert.True(Recap.IsValidSummary(summary));
        Assert.True(summary.Length <= Recap.MaxSummaryLength);
    }

    private static RecapEligibleSession Session(DateTimeOffset? startedAt, DateTimeOffset? endedAt)
        => new(_organizationId, _workspaceId, _sessionId, startedAt, endedAt);

    private static SessionEvent Event(string eventType)
        => SessionEvent.Create(
            _organizationId,
            _workspaceId,
            _sessionId,
            eventType,
            createdBy: null,
            targetParticipantId: null,
            payload: "{}",
            schemaVersion: 1,
            createdAt: _endUtc);

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
