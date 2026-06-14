using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // The root identity aggregate (keyed by OIDC issuer + subject) — no foreign keys, so it inserts without
    // seeding anything else. Each subject is unique so two profiles can be inserted in one unit of work.
    private static UserProfile NewUserProfile(string subject)
        => UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, "https://issuer.test", subject), _now);
}
