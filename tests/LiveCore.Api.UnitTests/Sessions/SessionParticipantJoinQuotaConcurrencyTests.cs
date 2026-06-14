using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Sessions;

/// <summary>
/// The concurrency proof for the server-side participant cap (CORE-MON-005) — the story's required
/// "concurrent joins at limit-1 admit exactly the remaining slots; concurrent joins cannot exceed the limit".
///
/// It runs N participant joins of the SAME session TRULY in parallel (each a DISTINCT active participant on its own
/// <see cref="LiveCoreDbContext"/> over its own connection to ONE shared temp-file SQLite database, so the
/// concurrent quota_usage upserts serialize through SQLite's normal busy-retry locking — the realistic stand-in for
/// the production PostgreSQL row lock the EF Npgsql integration job exercises). The only contention point is the
/// session's <c>session.participant.max</c> quota, consumed by the join service's atomic limit-guarded increment
/// (CORE-CONC-004), so exactly the remaining slots are admitted and the rest are quota-exceeded, and the recorded
/// usage never exceeds the cap. A UI-only cap, or a non-atomic check-then-record, would let several racing joins
/// all pass — exactly the bypass this story closes. All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class SessionParticipantJoinQuotaConcurrencyTests : IDisposable
{
    private const string _organizationSlug = "northwind-labs";
    private const string _workspaceSlug = "summer-show";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _time = new FixedTimeProvider(_seedTime);

    public SessionParticipantJoinQuotaConcurrencyTests()
    {
        // A temp-file database (not shared-cache in-memory) so independent connections contend through SQLite's
        // normal file locking, which retries on a busy writer rather than failing — the behavior a true parallel
        // join needs (the same setup as QuotaEnforcementConcurrencyTests).
        _databasePath = Path.Combine(Path.GetTempPath(), $"livecore-join-concurrency-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath}";
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task Concurrent_joins_at_limit_minus_one_admit_exactly_the_one_remaining_slot()
    {
        // The session is one below its cap (limit 4, usage 3), so exactly ONE slot remains. Eight distinct
        // participants race to take it: exactly one is admitted and seven are quota-exceeded, and the recorded
        // usage lands precisely at the cap — never above it.
        const int parallelism = 8;
        var (organizationId, workspaceId, sessionId) = await SeedSessionAsync();
        var participantIds = await SeedParticipantsAsync(organizationId, workspaceId, parallelism);
        var quotaId = await SeedParticipantQuotaAsync(sessionId, limit: 4, startingUsage: 3);

        var admitted = await JoinAllInParallelAsync(organizationId, workspaceId, sessionId, participantIds);

        Assert.Equal(1, admitted.Count(success => success));                 // exactly the one remaining slot
        Assert.Equal(parallelism - 1, admitted.Count(success => !success));  // the rest are quota-exceeded
        Assert.Equal(4, await ReadUsageAsync(sessionId, quotaId));           // the cap was never exceeded
    }

    [Fact]
    public async Task Concurrent_joins_admit_exactly_the_remaining_slots_under_a_higher_limit()
    {
        // Eight distinct participants race for an empty session with a cap of four: exactly four are admitted and
        // four are quota-exceeded, and the recorded usage lands precisely at the cap.
        const int parallelism = 8;
        const long limit = 4;
        var (organizationId, workspaceId, sessionId) = await SeedSessionAsync();
        var participantIds = await SeedParticipantsAsync(organizationId, workspaceId, parallelism);
        var quotaId = await SeedParticipantQuotaAsync(sessionId, limit, startingUsage: 0);

        var admitted = await JoinAllInParallelAsync(organizationId, workspaceId, sessionId, participantIds);

        Assert.Equal(limit, admitted.Count(success => success));
        Assert.Equal(parallelism - limit, admitted.Count(success => !success));
        Assert.Equal(limit, await ReadUsageAsync(sessionId, quotaId));
    }

    private async Task<bool[]> JoinAllInParallelAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        IReadOnlyList<Guid> participantIds)
    {
        // Fire every join at once and let them race for the session's participant slots.
        var tasks = participantIds
            .Select(participantId => Task.Run(async () =>
            {
                await using var context = new LiveCoreDbContext(_contextOptions);
                var service = new SessionParticipantJoinService(
                    new SessionRepository(context),
                    new ParticipantRepository(context),
                    new NoOpSessionEventPublisher(),
                    new QuotaEnforcementService(
                        new QuotaDefinitionRepository(context),
                        new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
                        new QuotaUsageRepository(context),
                        _time),
                    _time);
                var result = await service.JoinAsync(
                    organizationId, workspaceId, sessionId, participantId, CancellationToken.None);

                // Every denial on this path is the quota gate (the participants, session and tenant are all valid and
                // in scope), so a non-admission is exactly a quota-exceeded outcome.
                if (!result.Admitted)
                {
                    Assert.Equal(SessionJoinDenialReason.QuotaExceeded, result.Reason);
                }

                return result.Admitted;
            }))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedSessionAsync()
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var organization = Organization.Create(_organizationSlug, _organizationSlug, _seedTime);
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));

        var workspace = Workspace.Create(organization.Id, _workspaceSlug, _workspaceSlug, _seedTime);
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));

        // Start the session so it is Live (joinable) and the join's lifecycle gate passes.
        var session = Session.Create(organization.Id, workspace.Id, "Run", _seedTime);
        session.Start(_seedTime);
        Assert.Equal(SessionAddResult.Added, await new SessionRepository(context).AddAsync(session, CancellationToken.None));

        return (organization.Id, workspace.Id, session.Id);
    }

    private async Task<IReadOnlyList<Guid>> SeedParticipantsAsync(Guid organizationId, Guid workspaceId, int count)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var repository = new ParticipantRepository(context);
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            // Anonymous participants need no user profile, keeping the contention purely on the session quota.
            var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, $"Participant {i}", _seedTime);
            Assert.Equal(ParticipantAddResult.Added, await repository.AddAsync(participant, CancellationToken.None));
            ids.Add(participant.Id);
        }

        return ids;
    }

    private async Task<Guid> SeedParticipantQuotaAsync(Guid sessionId, long limit, long startingUsage)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var entitlement = EntitlementDefinition.Define(
            QuotaEntitlementKeys.SessionParticipantMax, EntitlementValueKind.Quota, "Quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.Session, QuotaUnit.Count, _seedTime);
        context.QuotaDefinitions.Add(quota);
        context.SubjectEntitlements.Add(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.Session, sessionId, entitlement, limit, null, _seedTime));

        if (startingUsage != 0)
        {
            var usage = QuotaUsage.Start(EntitlementSubjectType.Session, sessionId, quota, _seedTime);
            usage.Record(startingUsage, _seedTime);
            context.QuotaUsage.Add(usage);
        }

        await context.SaveChangesAsync();
        return quota.Id;
    }

    private async Task<long> ReadUsageAsync(Guid sessionId, Guid quotaDefinitionId)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var usage = await new QuotaUsageRepository(context).FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.Session, sessionId, quotaDefinitionId, CancellationToken.None);
        return usage?.UsedAmount ?? 0;
    }

    /// <summary>
    /// A no-op <see cref="ISessionEventPublisher"/> that records nothing and touches no database, so a parallel
    /// join contends only on the session's participant quota (the ParticipantJoined emission is exercised by the
    /// sequential join tests). The split append/deliver halves are not used on the join path.
    /// </summary>
    private sealed class NoOpSessionEventPublisher : ISessionEventPublisher
    {
        public Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sessionEvent);
            return Task.CompletedTask;
        }

        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeliverAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the recorded usage timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
