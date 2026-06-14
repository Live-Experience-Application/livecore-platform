using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// The multi-worker correctness proof for the recap generation job (CORE-RCP-001) — the story's required
/// "two concurrent recap sweeps over the same eligible session produce exactly one recap; a sequential
/// re-sweep is a no-op".
///
/// It drives the REAL <see cref="RecapGenerationService"/> over the REAL <see cref="RecapEligibleSessionReader"/>
/// and <see cref="RecapRepository"/> against a real database, so the actual eligibility NOT EXISTS read, the
/// insert path and — crucially — the partial unique index <c>recaps(session_id) WHERE generated_by IS NULL</c>
/// are exercised. Like the atomic-quota concurrency proof (CORE-CONC-004), the parallel test runs each sweep on
/// its OWN <see cref="LiveCoreDbContext"/> over its own connection to ONE shared temp-file SQLite database, so
/// multiple writers genuinely contend through SQLite's busy-retry locking (the realistic stand-in for the
/// PostgreSQL behaviour exercised by the EF Npgsql integration job). Each sweep mints a fresh UUIDv7 primary
/// key, so the primary key never deduplicates — only the partial unique index does, exactly as in production.
/// All fixtures are generic Core language (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class RecapGenerationConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endedAt = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 14, 11, 0, 0, TimeSpan.Zero);
    private static readonly RecapGenerationOptions _options = new(TimeSpan.FromHours(1), batchSize: 50);
    private readonly TimeProvider _time = new FixedTimeProvider(_generatedAt);

    private readonly string _databasePath;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public RecapGenerationConcurrencyTests()
    {
        // A temp-file database (not shared-cache in-memory) so independent connections contend through SQLite's
        // normal file locking, which retries on a busy writer rather than failing — the behaviour a true
        // parallel sweep needs.
        _databasePath = Path.Combine(Path.GetTempPath(), $"livecore-recap-concurrency-{Guid.NewGuid():N}.db");
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // Release pooled connections so the temp-file handle is closed before the file is removed.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Concurrent_sweeps_over_the_same_eligible_session_produce_exactly_one_recap(int parallelism)
    {
        var (organizationId, workspaceId, sessionId) = await SeedEndedSessionAsync();

        // Fire N sweeps at once. Each independently reads the session as eligible (the NOT EXISTS read is
        // decoupled from the insert) and tries to append a system recap; the partial unique index lets only one
        // insert win, and every loser converges onto it (AlreadyExists) — never a duplicate, never a failure.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => Task.Run(RunSweepAsync))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // Exactly one system recap exists for the session, regardless of how many overlapping sweeps ran.
        Assert.Equal(1, await CountSystemRecapsAsync(sessionId));

        // Exactly one sweep actually produced the recap (the unique index caps inserts at one); the session was
        // ended and unrecapped at the start, so at least one sweep observed it and committed.
        Assert.Equal(1, results.Sum(result => result.Generated));

        // No sweep recorded a failure: a losing insert is the benign AlreadyExists path, not an error.
        Assert.Equal(0, results.Sum(result => result.Failed));

        // The single recap is scoped to its own session's tenant/workspace (threat T5).
        await using var context = new LiveCoreDbContext(_contextOptions);
        var recap = Assert.Single(await new RecapRepository(context)
            .ListBySessionAsync(organizationId, workspaceId, sessionId, CancellationToken.None));
        Assert.Null(recap.GeneratedByUserProfileId);
        Assert.Equal(organizationId, recap.OrganizationId);
        Assert.Equal(workspaceId, recap.WorkspaceId);
    }

    [Fact]
    public async Task A_sequential_re_sweep_is_a_no_op()
    {
        var (organizationId, workspaceId, sessionId) = await SeedEndedSessionAsync();

        var first = await RunSweepAsync();
        var second = await RunSweepAsync();

        // The first sweep produces exactly one recap; the second finds the session no longer eligible (it now
        // has a recap), so it examines nothing and produces nothing — a true no-op.
        Assert.Equal(1, first.Examined);
        Assert.Equal(1, first.Generated);
        Assert.Equal(0, first.Deduplicated);
        Assert.Equal(0, first.Failed);

        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Generated);
        Assert.Equal(0, second.Deduplicated);
        Assert.Equal(0, second.Failed);

        Assert.Equal(1, await CountSystemRecapsAsync(sessionId));
    }

    private async Task<RecapGenerationResult> RunSweepAsync()
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var service = new RecapGenerationService(
            new RecapEligibleSessionReader(context),
            new RecapRepository(context),
            new SessionEventRepository(context),
            _options,
            _time,
            NullLogger<RecapGenerationService>.Instance);
        return await service.GenerateDueRecapsAsync(CancellationToken.None);
    }

    private async Task<int> CountSystemRecapsAsync(Guid sessionId)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        return await context.Recaps
            .CountAsync(recap => recap.SessionId == sessionId && recap.GeneratedByUserProfileId == null);
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedEndedSessionAsync()
    {
        var organization = Organization.Create("northwind-labs", "northwind-labs", _createdAt);
        var workspace = Workspace.Create(organization.Id, "summer-show", "summer-show", _createdAt);
        var session = Session.Create(organization.Id, workspace.Id, "Opening night", _createdAt);
        session.Start(_startedAt);
        session.End(_endedAt);

        await using var context = new LiveCoreDbContext(_contextOptions);
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        Assert.Equal(SessionAddResult.Added, await new SessionRepository(context).AddAsync(session, CancellationToken.None));
        return (organization.Id, workspace.Id, session.Id);
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so each produced recap's timestamp is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
