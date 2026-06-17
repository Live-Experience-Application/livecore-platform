// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Unit tests for <see cref="RecapGenerationService"/> (CORE-JOB-001) — the background recap generation job's
/// application service, which produces a recap for every session that needs one (an ended session with no
/// recap yet) on the worker's cadence, idempotently and tenant-scoped. The service is driven over a stateful
/// in-memory fake that plays BOTH the eligibility reader and the recap repository, so the tests assert the
/// job's own behavior without persistence: the fake's eligibility read excludes any session that already has
/// an appended recap, modeling the real anti-join (<see cref="RecapEligibleSessionReader"/>, verified
/// separately against SQLite).
///
/// The acceptance-criteria invariants are exercised directly:
/// <list type="bullet">
///   <item>A recap is produced ONCE PER ELIGIBLE SESSION, scoped to that session's own tenant/workspace.</item>
///   <item>IDEMPOTENT: re-running the sweep produces nothing more once the eligible sessions are recapped.</item>
///   <item>TENANT-SCOPED (threat T5): each recap carries its own session's organization, workspace and id —
///   never another tenant's.</item>
///   <item>The configured BATCH SIZE bounds a single sweep.</item>
///   <item>RESILIENT: a per-session persistence failure is counted and the session stays eligible for the
///   next sweep, without aborting the run.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class RecapGenerationServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly RecapGenerationOptions _options =
        new(TimeSpan.FromHours(1), batchSize: 50);

    [Fact]
    public async Task Generates_one_recap_per_eligible_session()
    {
        var first = EndedSession();
        var second = EndedSession();
        var store = new FakeRecapStore(first, second);
        var service = CreateService(store);

        var result = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, result.Generated);
        Assert.Equal(0, result.Deduplicated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(
            new[] { first.SessionId, second.SessionId }.OrderBy(id => id),
            store.Appended.Select(recap => recap.SessionId).OrderBy(id => id));
        // A system-generated recap records no producing user (docs/09 RecapGenerated source "System/Host").
        Assert.All(store.Appended, recap => Assert.Null(recap.GeneratedByUserProfileId));
        // The produced timestamp comes from the injected clock.
        Assert.All(store.Appended, recap => Assert.Equal(_now, recap.GeneratedAt));
    }

    [Fact]
    public async Task Is_idempotent_a_second_sweep_produces_nothing_more()
    {
        var session = EndedSession();
        var store = new FakeRecapStore(session);
        var service = CreateService(store);

        var first = await service.GenerateDueRecapsAsync(CancellationToken.None);
        var second = await service.GenerateDueRecapsAsync(CancellationToken.None);

        // The first sweep produces exactly one recap; the second finds the session no longer eligible (it now
        // has a recap), so it produces nothing — exactly once per eligible session across any number of sweeps.
        Assert.Equal(1, first.Generated);
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Generated);
        Assert.Single(store.Appended);
    }

    [Fact]
    public async Task Produces_a_recap_scoped_to_each_sessions_own_tenant_and_workspace()
    {
        // Tenant isolation (threat T5): two sessions in two different tenants/workspaces. Each produced recap
        // must carry its OWN session's organization, workspace and id — never the other tenant's.
        var inTenantA = EndedSession();
        var inTenantB = EndedSession();
        var store = new FakeRecapStore(inTenantA, inTenantB);
        var service = CreateService(store);

        await service.GenerateDueRecapsAsync(CancellationToken.None);

        var recapA = Assert.Single(store.Appended, recap => recap.SessionId == inTenantA.SessionId);
        Assert.Equal(inTenantA.OrganizationId, recapA.OrganizationId);
        Assert.Equal(inTenantA.WorkspaceId, recapA.WorkspaceId);

        var recapB = Assert.Single(store.Appended, recap => recap.SessionId == inTenantB.SessionId);
        Assert.Equal(inTenantB.OrganizationId, recapB.OrganizationId);
        Assert.Equal(inTenantB.WorkspaceId, recapB.WorkspaceId);

        // No recap mixes one session's tenant with another session's id.
        Assert.DoesNotContain(
            store.Appended,
            recap => recap.OrganizationId == inTenantA.OrganizationId && recap.SessionId == inTenantB.SessionId);
    }

    [Fact]
    public async Task Passes_the_configured_batch_size_to_the_eligibility_read()
    {
        var store = new FakeRecapStore();
        var options = new RecapGenerationOptions(TimeSpan.FromHours(1), batchSize: 17);
        var service = new RecapGenerationService(
            store, store, store, options, new FixedTimeProvider(_now),
            NullLogger<RecapGenerationService>.Instance);

        await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(17, store.LastMaxCount);
    }

    [Fact]
    public async Task Does_nothing_when_no_session_needs_a_recap()
    {
        var store = new FakeRecapStore();
        var service = CreateService(store);

        var result = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Generated);
        Assert.Empty(store.Appended);
    }

    [Fact]
    public async Task Composes_the_recap_from_the_sessions_event_stream()
    {
        // CORE-RCP-002: the recap reflects what actually happened — reveals and scene activations — derived from
        // the session's own event stream, not just the start/end timestamps.
        var session = EndedSession();
        var store = new FakeRecapStore(session);
        store.SeedEvents(
            Event(session, SessionEventTypes.SceneActivated),
            Event(session, SessionEventTypes.SceneActivated),
            Event(session, SessionEventTypes.ContentRevealed),
            Event(session, SessionEventTypes.ParticipantJoined));
        var service = CreateService(store);

        await service.GenerateDueRecapsAsync(CancellationToken.None);

        var recap = Assert.Single(store.Appended);
        Assert.Contains("recorded 4 events", recap.Summary);
        Assert.Contains("2 scene activations", recap.Summary);
        Assert.Contains("1 content reveal", recap.Summary);
        Assert.Contains("1 participant join", recap.Summary);
    }

    [Fact]
    public async Task A_zero_event_session_produces_a_safe_empty_recap()
    {
        // CORE-RCP-002: an ended session whose stream is empty still produces a valid, deterministic recap that
        // safely reports no activity (never an exception or a blank body).
        var session = EndedSession();
        var store = new FakeRecapStore(session);
        var service = CreateService(store);

        var result = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(1, result.Generated);
        var recap = Assert.Single(store.Appended);
        Assert.Contains("No session activity was recorded", recap.Summary);
        Assert.True(Recap.IsValidSummary(recap.Summary));
    }

    [Fact]
    public async Task Composes_each_recap_from_only_its_own_sessions_events()
    {
        // Tenant/session scoping (threat T5): the event read is scoped to each session's own organization and
        // session, so one session's activity never bleeds into another session's recap.
        var withActivity = EndedSession();
        var withoutActivity = EndedSession();
        var store = new FakeRecapStore(withActivity, withoutActivity);
        store.SeedEvents(Event(withActivity, SessionEventTypes.ContentRevealed));
        var service = CreateService(store);

        await service.GenerateDueRecapsAsync(CancellationToken.None);

        var active = Assert.Single(store.Appended, recap => recap.SessionId == withActivity.SessionId);
        Assert.Contains("1 content reveal", active.Summary);

        var quiet = Assert.Single(store.Appended, recap => recap.SessionId == withoutActivity.SessionId);
        Assert.Contains("No session activity was recorded", quiet.Summary);
    }

    [Fact]
    public async Task Keeps_a_session_eligible_and_continues_when_persisting_its_recap_fails()
    {
        // A transient persistence failure for one session (e.g. the session was deleted between the eligibility
        // read and the append, an FK violation) must NOT abort the sweep and must leave that session eligible
        // so the next sweep retries it; the other session is still recapped.
        var failing = EndedSession();
        var succeeding = EndedSession();
        var store = new FakeRecapStore(failing, succeeding) { FailAppendFor = { failing.SessionId } };
        var service = CreateService(store);

        var result = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Generated);
        Assert.Equal(0, result.Deduplicated);
        Assert.Equal(1, result.Failed);
        // Only the succeeding session is recapped; the failing one wrote no recap, so it stays eligible.
        Assert.Equal(new[] { succeeding.SessionId }, store.Appended.Select(recap => recap.SessionId));

        // Clearing the fault and re-sweeping recaps the previously-failed session (retry), still exactly once.
        store.FailAppendFor.Clear();
        var retry = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(1, retry.Generated);
        Assert.Equal(
            new[] { failing.SessionId, succeeding.SessionId }.OrderBy(id => id),
            store.Appended.Select(recap => recap.SessionId).OrderBy(id => id));
    }

    [Fact]
    public async Task Emits_a_host_only_RecapGenerated_event_for_each_generated_recap()
    {
        // CORE-EVT-004: each freshly produced recap appends one durable RecapGenerated session event to its
        // own session's stream — a SYSTEM event (no actor), host-only and identifier-only.
        var first = EndedSession();
        var second = EndedSession();
        var store = new FakeRecapStore(first, second);
        var service = CreateService(store);

        var result = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(2, result.Generated);
        Assert.Equal(2, store.AppendedEvents.Count);
        foreach (var session in new[] { first, second })
        {
            var recap = Assert.Single(store.Appended, candidate => candidate.SessionId == session.SessionId);
            var sessionEvent = Assert.Single(store.AppendedEvents, candidate => candidate.SessionId == session.SessionId);

            // RecapGenerated, host-only, system-produced (no actor), subjectless and not selected — so the
            // recipient resolver routes it to the session hosts only (never the audience).
            Assert.Equal(SessionEventTypes.RecapGenerated, sessionEvent.EventType);
            Assert.True(SessionEventTypes.IsHostOnly(sessionEvent.EventType));
            Assert.Null(sessionEvent.CreatedBy);
            Assert.Null(sessionEvent.TargetParticipantId);
            Assert.False(sessionEvent.HasVisibilitySubject);

            // Scoped to its OWN session's tenant/workspace/session (threat T5), with an identifier-only
            // payload carrying the recap and session ids and never the recap body (threat T7).
            Assert.Equal(session.OrganizationId, sessionEvent.OrganizationId);
            Assert.Equal(session.WorkspaceId, sessionEvent.WorkspaceId);
            Assert.Contains(recap.Id.ToString(), sessionEvent.Payload, StringComparison.Ordinal);
            Assert.Contains(session.SessionId.ToString(), sessionEvent.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(recap.Summary, sessionEvent.Payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Emits_no_RecapGenerated_event_when_the_recap_is_deduplicated()
    {
        // A second sweep finds the session already recapped (idempotent), so it produces NO new recap and
        // therefore appends NO RecapGenerated event — exactly one event per session, matching its one recap.
        var session = EndedSession();
        var store = new FakeRecapStore(session);
        var service = CreateService(store);

        await service.GenerateDueRecapsAsync(CancellationToken.None);
        var second = await service.GenerateDueRecapsAsync(CancellationToken.None);

        Assert.Equal(0, second.Generated);
        Assert.Single(store.AppendedEvents);
    }

    private static RecapGenerationService CreateService(FakeRecapStore store)
        => new(
            store,
            store,
            store,
            _options,
            new FixedTimeProvider(_now),
            NullLogger<RecapGenerationService>.Instance);

    private static RecapEligibleSession EndedSession()
        => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            _now - TimeSpan.FromHours(2),
            _now - TimeSpan.FromHours(1));

    /// <summary>Builds a generic session event in the given session's tenant/workspace for the recap stream.</summary>
    private static SessionEvent Event(RecapEligibleSession session, string eventType)
        => SessionEvent.Create(
            session.OrganizationId,
            session.WorkspaceId,
            session.SessionId,
            eventType,
            createdBy: null,
            targetParticipantId: null,
            payload: "{}",
            schemaVersion: 1,
            createdAt: _now);

    /// <summary>
    /// A stateful in-memory fake that plays both the eligibility reader and the recap repository. Its
    /// eligibility read returns the configured ended sessions MINUS any that already have an appended recap —
    /// modeling the real anti-join, so the idempotency property emerges from the data exactly as it does in
    /// production. Only the members the generation service uses are implemented; the rest throw.
    /// </summary>
    private sealed class FakeRecapStore : IRecapEligibleSessionReader, IRecapRepository, ISessionEventRepository
    {
        private readonly List<RecapEligibleSession> _endedSessions;
        private readonly List<SessionEvent> _sessionEvents = [];

        public FakeRecapStore(params RecapEligibleSession[] endedSessions) => _endedSessions = [.. endedSessions];

        public List<Recap> Appended { get; } = [];

        /// <summary>The durable RecapGenerated session events the job appended (CORE-EVT-004).</summary>
        public List<SessionEvent> AppendedEvents { get; } = [];

        public HashSet<Guid> FailAppendFor { get; } = [];

        public int? LastMaxCount { get; private set; }

        /// <summary>Seeds the durable event stream the recap body is composed from (CORE-RCP-002).</summary>
        public void SeedEvents(params SessionEvent[] events) => _sessionEvents.AddRange(events);

        public Task<IReadOnlyList<RecapEligibleSession>> ListSessionsNeedingRecapAsync(
            int maxCount, CancellationToken cancellationToken)
        {
            LastMaxCount = maxCount;
            var recapped = Appended.Select(recap => recap.SessionId).ToHashSet();
            IReadOnlyList<RecapEligibleSession> due = _endedSessions
                .Where(session => !recapped.Contains(session.SessionId))
                .OrderBy(session => session.SessionId)
                .Take(maxCount)
                .ToList();
            return Task.FromResult(due);
        }

        public Task AppendAsync(Recap recap, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(recap);
            if (FailAppendFor.Contains(recap.SessionId))
            {
                throw new DbUpdateException($"simulated persistence failure for session {recap.SessionId}");
            }

            Appended.Add(recap);
            return Task.CompletedTask;
        }

        public Task<RecapAppendResult> TryAppendSystemRecapAsync(Recap recap, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(recap);
            if (recap.GeneratedByUserProfileId is not null)
            {
                throw new ArgumentException("Expected a system recap.", nameof(recap));
            }

            // A genuine persistence failure (e.g. an FK violation because the session was deleted) surfaces as a
            // DbUpdateException — the same contract the real repository rethrows when no duplicate exists.
            if (FailAppendFor.Contains(recap.SessionId))
            {
                throw new DbUpdateException($"simulated persistence failure for session {recap.SessionId}");
            }

            // Model the partial unique index recaps(session_id) WHERE generated_by IS NULL: a second system
            // recap for a session already recapped by the system is rejected and converges onto the existing
            // one (CORE-RCP-001), exactly as the real DB rejects the losing concurrent insert.
            if (Appended.Any(existing => existing.SessionId == recap.SessionId && existing.GeneratedByUserProfileId is null))
            {
                return Task.FromResult(RecapAppendResult.AlreadyExists);
            }

            Appended.Add(recap);
            return Task.FromResult(RecapAppendResult.Appended);
        }

        public Task<Recap?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Recap>> ListBySessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // The retention sweep (CORE-PRIV-003) is a different worker loop; the recap-generation tests never
        // exercise it, so its repository members are unsupported here as a regression guard.
        public Task<IReadOnlyList<Recap>> ListExpiredForRetentionAsync(DateTimeOffset generatedBefore, int maxCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(Recap recap, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // ISessionEventRepository — the recap body's source (CORE-RCP-002) AND, since CORE-EVT-004, the sink
        // for the durable RecapGenerated event the job appends on each freshly produced recap. Capture the
        // appended events so the tests can assert the host-only, identifier-only emission.
        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sessionEvent);
            AppendedEvents.Add(sessionEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(Guid organizationId, Guid sessionId, CancellationToken cancellationToken)
        {
            // Faithful to the real repository: tenant- AND session-scoped, leading with the organization id, so a
            // session's events are never returned through another tenant's or session's id (threat T5/T1).
            IReadOnlyList<SessionEvent> events = _sessionEvents
                .Where(sessionEvent => sessionEvent.OrganizationId == organizationId && sessionEvent.SessionId == sessionId)
                .ToList();
            return Task.FromResult(events);
        }
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each produced recap's timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
