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
            store, store, options, new FixedTimeProvider(_now),
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

    private static RecapGenerationService CreateService(FakeRecapStore store)
        => new(
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

    /// <summary>
    /// A stateful in-memory fake that plays both the eligibility reader and the recap repository. Its
    /// eligibility read returns the configured ended sessions MINUS any that already have an appended recap —
    /// modeling the real anti-join, so the idempotency property emerges from the data exactly as it does in
    /// production. Only the members the generation service uses are implemented; the rest throw.
    /// </summary>
    private sealed class FakeRecapStore : IRecapEligibleSessionReader, IRecapRepository
    {
        private readonly List<RecapEligibleSession> _endedSessions;

        public FakeRecapStore(params RecapEligibleSession[] endedSessions) => _endedSessions = [.. endedSessions];

        public List<Recap> Appended { get; } = [];

        public HashSet<Guid> FailAppendFor { get; } = [];

        public int? LastMaxCount { get; private set; }

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

        public Task<Recap?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Recap>> ListBySessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each produced recap's timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
