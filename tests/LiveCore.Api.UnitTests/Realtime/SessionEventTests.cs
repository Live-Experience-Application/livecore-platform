using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEvent"/> (CORE-RT-003) — the append-only session event aggregate. They
/// pin the structural invariants of the <see cref="SessionEvent.Create"/> factory (required ids
/// non-empty, optional ids either null or non-empty, bounded event type / payload, schema version at
/// least 1), UTC normalization, and that <see cref="SessionEvent.ToString"/> excludes the payload
/// (threat T7). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent CreateAudienceWide()
        => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            SessionEventTypes.ContentRevealed, Guid.NewGuid(), targetParticipantId: null,
            payload: "{}", schemaVersion: 1, createdAt: _now);

    [Fact]
    public void Create_sets_the_fields()
    {
        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var session = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var participant = Guid.NewGuid();

        var sessionEvent = SessionEvent.Create(
            org, ws, session, SessionEventTypes.ContentRevealed, actor, participant, "{\"a\":1}", 2, _now);

        Assert.NotEqual(Guid.Empty, sessionEvent.Id);
        Assert.Equal(org, sessionEvent.OrganizationId);
        Assert.Equal(ws, sessionEvent.WorkspaceId);
        Assert.Equal(session, sessionEvent.SessionId);
        Assert.Equal("ContentRevealed", sessionEvent.EventType);
        Assert.Equal(actor, sessionEvent.CreatedBy);
        Assert.Equal(participant, sessionEvent.TargetParticipantId);
        Assert.True(sessionEvent.IsForSelectedParticipant);
        Assert.Equal("{\"a\":1}", sessionEvent.Payload);
        Assert.Equal(2, sessionEvent.SchemaVersion);
        // The per-session sequence (CORE-RTC-001) is unassigned (0) until the append path stamps it.
        Assert.Equal(0, sessionEvent.Sequence);
    }

    // --- Per-session sequence (CORE-RTC-001) -----------------------------------

    [Fact]
    public void AssignSequence_stamps_the_per_session_sequence_once()
    {
        var sessionEvent = CreateAudienceWide();

        sessionEvent.AssignSequence(7);

        Assert.Equal(7, sessionEvent.Sequence);
    }

    [Fact]
    public void AssignSequence_rejects_a_second_assignment()
    {
        // The sequence is an immutable per-session position assigned exactly once at append time.
        var sessionEvent = CreateAudienceWide();
        sessionEvent.AssignSequence(1);

        Assert.Throws<InvalidOperationException>(() => sessionEvent.AssignSequence(2));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AssignSequence_rejects_a_non_positive_sequence(long sequence)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CreateAudienceWide().AssignSequence(sequence));

    [Fact]
    public void An_audience_wide_event_has_no_target_participant()
    {
        var sessionEvent = CreateAudienceWide();

        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.IsForSelectedParticipant);
    }

    [Fact]
    public void A_system_event_has_no_actor()
    {
        var sessionEvent = SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            SessionEventTypes.ContentRevealed, createdBy: null, targetParticipantId: null,
            payload: "{}", schemaVersion: 1, createdAt: _now);

        Assert.Null(sessionEvent.CreatedBy);
    }

    [Fact]
    public void CreatedAt_is_normalized_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var sessionEvent = SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            SessionEventTypes.ContentRevealed, null, null, "{}", 1, local);

        Assert.Equal(TimeSpan.Zero, sessionEvent.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, sessionEvent.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void Create_rejects_empty_required_ids()
    {
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null, "{}", 1, _now));
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null, "{}", 1, _now));
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, SessionEventTypes.ContentRevealed, null, null, "{}", 1, _now));
    }

    [Fact]
    public void Create_rejects_an_explicitly_empty_actor_or_target()
    {
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed,
            createdBy: Guid.Empty, targetParticipantId: null, payload: "{}", schemaVersion: 1, createdAt: _now));
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed,
            createdBy: null, targetParticipantId: Guid.Empty, payload: "{}", schemaVersion: 1, createdAt: _now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_event_type(string eventType)
        => Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), eventType, null, null, "{}", 1, _now));

    [Fact]
    public void Create_rejects_an_oversize_payload()
        => Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null,
            new string('x', SessionEvent.MaxPayloadLength + 1), 1, _now));

    [Fact]
    public void Create_rejects_a_schema_version_below_one()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null, "{}", 0, _now));

    // --- Visibility subject (CORE-RT-004) --------------------------------------

    [Fact]
    public void Create_without_a_subject_has_no_visibility_subject()
    {
        var sessionEvent = CreateAudienceWide();

        Assert.False(sessionEvent.HasVisibilitySubject);
        Assert.Null(sessionEvent.VisibilitySubjectType);
        Assert.Null(sessionEvent.VisibilitySubjectId);
    }

    [Fact]
    public void Create_with_a_subject_records_the_pair()
    {
        var subjectId = Guid.NewGuid();
        var sessionEvent = SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null,
            "{}", 1, _now, visibilitySubjectType: "Entity", visibilitySubjectId: subjectId);

        Assert.True(sessionEvent.HasVisibilitySubject);
        Assert.Equal("Entity", sessionEvent.VisibilitySubjectType);
        Assert.Equal(subjectId, sessionEvent.VisibilitySubjectId);
    }

    [Fact]
    public void Create_rejects_a_half_set_visibility_subject()
    {
        // The subject travels as a pair: a type without an id (or an id without a type) cannot gate
        // recipients and is rejected rather than silently degraded.
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null,
            "{}", 1, _now, visibilitySubjectType: "Entity", visibilitySubjectId: null));
        Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null,
            "{}", 1, _now, visibilitySubjectType: null, visibilitySubjectId: Guid.NewGuid()));
    }

    [Fact]
    public void Create_rejects_an_explicitly_empty_visibility_subject_id()
        => Assert.Throws<ArgumentException>(() => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed, null, null,
            "{}", 1, _now, visibilitySubjectType: "Entity", visibilitySubjectId: Guid.Empty));

    [Fact]
    public void ToString_excludes_the_payload()
    {
        var sessionEvent = SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            SessionEventTypes.ContentRevealed, null, null, "SECRET-PAYLOAD-CONTENT", 1, _now);

        var text = sessionEvent.ToString();

        Assert.DoesNotContain("SECRET-PAYLOAD-CONTENT", text, StringComparison.Ordinal);
        Assert.Contains(sessionEvent.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("ContentRevealed", text, StringComparison.Ordinal);
    }
}
