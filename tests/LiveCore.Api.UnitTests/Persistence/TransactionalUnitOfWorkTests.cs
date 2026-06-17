// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="TransactionalUnitOfWork"/> (CORE-CONC-002): a multi-step handler's writes run as ONE
/// database transaction, so they commit together or a part-way failure rolls EVERYTHING back. They run
/// against an in-memory SQLite database (foreign keys ON), so the real transaction begin/commit/rollback over
/// the shared <see cref="LiveCoreDbContext"/> is exercised on every run without a database server; the same
/// transactional semantics hold against PostgreSQL in the deployment pipeline.
///
/// <see cref="UserProfile"/> is used as the write because it is the root identity aggregate with no foreign
/// keys, so a row can be inserted without seeding anything else — keeping these tests focused on the unit of
/// work itself, not on any one command. The command-level, end-to-end atomicity (reveal/hide and session
/// start/end/cancel, plus commit-then-publish) is covered by the integration suite
/// (<c>TransactionalCommandAtomicityTests</c>). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class TransactionalUnitOfWorkTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public TransactionalUnitOfWorkTests()
    {
        // One open connection keeps the private in-memory database alive while each context (the unit of
        // work's, and the verifying reader's) round-trips through it.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ExecuteAsync_commits_all_writes_and_returns_the_work_result()
    {
        await using var context = CreateContext();
        var unitOfWork = new TransactionalUnitOfWork(context);
        var first = NewUserProfile("subject-a");
        var second = NewUserProfile("subject-b");

        // Two writes (each its own SaveChanges) inside one unit of work — they must commit together.
        var count = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                context.UserProfiles.Add(first);
                await context.SaveChangesAsync(cancellationToken);
                context.UserProfiles.Add(second);
                await context.SaveChangesAsync(cancellationToken);
                return 2;
            },
            CancellationToken.None);

        Assert.Equal(2, count);

        // A fresh context over the same database sees both committed rows.
        await using var verify = CreateContext();
        Assert.Equal(2, await verify.UserProfiles.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_rolls_back_every_write_when_the_work_throws()
    {
        await using var context = CreateContext();
        var unitOfWork = new TransactionalUnitOfWork(context);
        var profile = NewUserProfile("subject-a");

        // The write is flushed (SaveChanges) but the work then throws BEFORE the commit, exactly like a
        // command failing after its rule/state change but before its event append.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unitOfWork.ExecuteAsync<int>(
                async cancellationToken =>
                {
                    context.UserProfiles.Add(profile);
                    await context.SaveChangesAsync(cancellationToken);
                    throw new InvalidOperationException("injected failure after the write, before the commit");
                },
                CancellationToken.None));

        Assert.Equal("injected failure after the write, before the commit", thrown.Message);

        // The flushed-but-uncommitted write rolled back: a fresh context sees nothing.
        await using var verify = CreateContext();
        Assert.Equal(0, await verify.UserProfiles.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_null_work_delegate()
    {
        await using var context = CreateContext();
        var unitOfWork = new TransactionalUnitOfWork(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => unitOfWork.ExecuteAsync<int>(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_retries_against_fresh_entity_state_after_a_transient_failure()
    {
        // CORE-CONC-005: under a RETRYING execution strategy (the production EnableRetryOnFailure posture), a
        // transient failure mid-delegate on the first attempt must roll back and re-run the whole unit of work
        // against FRESH entity state — not the first attempt's stale, tracked in-memory mutations. This pins the
        // story's failure mode: a session that started (Prepared -> Live) and an entity that was added on
        // attempt 1 must NOT survive into attempt 2 to throw InvalidSessionStateTransitionException or double-add.
        var sessionId = await SeedPreparedSessionAsync();

        // A context whose execution strategy retries the test's injected transient failure, exactly as the real
        // Npgsql retrying strategy retries a transient database disruption. Both the unit of work and the
        // verifying reader share the one open SQLite connection, so the rollback/retry round-trips the database.
        await using var context = CreateRetryingContext();
        var unitOfWork = new TransactionalUnitOfWork(context);
        const string addedSubject = "retry-added-subject";

        var attempts = 0;
        var finalStatus = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                attempts++;

                // RELOAD inside the delegate — the retry-safe pattern the session command now follows. On the
                // retry the unit of work has cleared the change tracker, so this query reads the rolled-back
                // Prepared row instead of returning attempt 1's already-Live tracked instance.
                var session = await context.Sessions
                    .FirstAsync(s => s.Id == sessionId, cancellationToken);

                // Would throw InvalidSessionStateTransitionException on the retry if attempt 1's Live mutation
                // had survived on the change tracker. The session is tracked (loaded just above), so the
                // mutation is persisted by SaveChanges without an explicit Update call.
                session.Start(_now);

                // A fresh row added inside the delegate — re-running the unit of work must NOT double-add it
                // (without the fix, attempt 2's Add collides with attempt 1's still-tracked Added instance).
                context.UserProfiles.Add(NewUserProfile(addedSubject));
                await context.SaveChangesAsync(cancellationToken);

                // Inject ONE transient failure, on the first attempt only, AFTER the writes (mid-delegate), so
                // the execution strategy rolls the attempt back and retries the whole unit of work once.
                if (attempts == 1)
                {
                    throw new TransientRetryTestException();
                }

                return session.Status;
            },
            CancellationToken.None);

        // The unit of work ran exactly twice (one retry) and ultimately SUCCEEDED with the correct state: the
        // session reached Live once, with no InvalidSessionStateTransitionException from a stale transition.
        Assert.Equal(2, attempts);
        Assert.Equal(SessionStatus.Live, finalStatus);

        // The committed database state is correct end-to-end: the session is Live, and the row added inside the
        // delegate exists EXACTLY ONCE — the rolled-back first attempt did not double-add it.
        await using var verify = CreateContext();
        Assert.Equal(SessionStatus.Live, (await verify.Sessions.SingleAsync(s => s.Id == sessionId)).Status);
        Assert.Equal(1, await verify.UserProfiles.CountAsync(u => u.SubjectId == addedSubject));
    }

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // A context configured with a RETRYING execution strategy (over the same shared SQLite connection) so the
    // execution-strategy retry path TransactionalUnitOfWork relies on is genuinely exercised. The production
    // suites otherwise use a plain non-retrying provider, so the delegate is never re-run there (the bug was
    // latent for exactly that reason).
    private LiveCoreDbContext CreateRetryingContext()
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection, sqlite => sqlite.ExecutionStrategy(
                dependencies => new TransientRetryingExecutionStrategy(dependencies)))
            .Options;
        var context = new LiveCoreDbContext(options);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // Seeds a Prepared session (with the organization and workspace its foreign keys require) and returns its
    // id, so the retry test can start it through the real Session state machine.
    private async Task<Guid> SeedPreparedSessionAsync()
    {
        await using var context = CreateContext();
        var organization = Organization.Create("retry-org", "Retry Org", _now);
        var workspace = Workspace.Create(organization.Id, "retry-ws", "Retry Workspace", _now);
        var session = Session.Create(organization.Id, workspace.Id, "Retry Session", _now);
        context.Organizations.Add(organization);
        context.Workspaces.Add(workspace);
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    // The root identity aggregate (keyed by OIDC issuer + subject) — no foreign keys, so it inserts without
    // seeding anything else. Each subject is unique so two profiles can be inserted in one unit of work.
    private static UserProfile NewUserProfile(string subject)
        => UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, "https://issuer.test", subject), _now);

    /// <summary>
    /// The transient failure the retry test injects mid-delegate. It models a routine, transient database
    /// disruption (a failover/restart/partition) — the kind the production retrying strategy retries — so the
    /// test exercises the same execution-strategy re-run path without needing a real PostgreSQL outage.
    /// </summary>
    private sealed class TransientRetryTestException : Exception
    {
        public TransientRetryTestException()
            : base("Injected transient failure (retried by the test execution strategy).")
        {
        }
    }

    /// <summary>
    /// A retrying <see cref="ExecutionStrategy"/> that retries ONLY the test's
    /// <see cref="TransientRetryTestException"/> — the test analogue of the production Npgsql
    /// <c>EnableRetryOnFailure</c> strategy (CORE-CONC-003). The first retry incurs no back-off delay (the EF
    /// Core formula yields zero for the first retry), and the test injects exactly one failure, so the retry is
    /// immediate and deterministic.
    /// </summary>
    private sealed class TransientRetryingExecutionStrategy : ExecutionStrategy
    {
        public TransientRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => exception is TransientRetryTestException;
    }
}
