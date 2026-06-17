using LiveCore.Api.Persistence;
using LiveCore.Api.SystemModule;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.SystemModule;

/// <summary>
/// Tests for the <see cref="IdempotencyKey"/> aggregate and the EF Core-backed
/// <see cref="IdempotencyKeyStore"/> (CORE-VIS-004). They run against an in-memory SQLite database, so
/// the real model mapping and — crucially — the UNIQUE <c>idempotency_keys(scope, key)</c> index that
/// IS the idempotency guarantee are exercised on every run.
///
/// Coverage: the aggregate's scope/key invariants; the store round-trip; that a second record of the
/// same (scope, key) is rejected as a <see cref="IdempotencyKeyAddResult.Duplicate"/> (the idempotency
/// signal) while the same key under a DIFFERENT scope is independent; the find/blank-guard behavior; and
/// the CORE-PRIV-006 retention purge (<see cref="IdempotencyKeyStore.DeleteCreatedBeforeAsync"/>) that
/// removes rows by age alone, bounded by the batch and idempotent when nothing is expired. All fixtures
/// are generic (AGENTS.md).
/// </summary>
public sealed class IdempotencyKeyStoreTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public IdempotencyKeyStoreTests()
    {
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

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // --- Aggregate invariants --------------------------------------------------

    [Fact]
    public void Create_sets_fields_and_trims()
    {
        var key = IdempotencyKey.Create("  reveal:org-a  ", "  key-1  ", _createdAt);

        Assert.NotEqual(Guid.Empty, key.Id);
        Assert.Equal("reveal:org-a", key.Scope);
        Assert.Equal("key-1", key.Key);
        Assert.Equal(_createdAt, key.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_scope(string? scope)
    {
        var exception = Assert.Throws<ArgumentException>(() => IdempotencyKey.Create(scope!, "key", _createdAt));
        Assert.Equal("scope", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_key(string? key)
    {
        var exception = Assert.Throws<ArgumentException>(() => IdempotencyKey.Create("scope", key!, _createdAt));
        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_an_over_long_key()
    {
        var key = new string('k', IdempotencyKey.MaxKeyLength + 1);
        var exception = Assert.Throws<ArgumentException>(() => IdempotencyKey.Create("scope", key, _createdAt));
        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void Create_rejects_a_control_character_in_the_key()
    {
        var exception = Assert.Throws<ArgumentException>(() => IdempotencyKey.Create("scope", "key\n1", _createdAt));
        Assert.Equal("key", exception.ParamName);
    }

    [Fact]
    public void Create_without_a_result_id_leaves_it_null()
    {
        // The state-idempotent reveal/hide commands record a key with no result (CORE-DX-004).
        var key = IdempotencyKey.Create("reveal:org-a", "key-1", _createdAt);
        Assert.Null(key.ResultId);
    }

    [Fact]
    public void Create_binds_a_supplied_result_id()
    {
        // A create or purchase-verification key binds the produced resource id (CORE-DX-004).
        var resultId = Guid.CreateVersion7();
        var key = IdempotencyKey.Create("session-create:org-a", "key-1", _createdAt, resultId);
        Assert.Equal(resultId, key.ResultId);
    }

    [Fact]
    public void Create_rejects_an_empty_result_id()
    {
        // An empty Guid never addresses a resource: passing one (rather than null) is a programming error.
        var exception = Assert.Throws<ArgumentException>(
            () => IdempotencyKey.Create("session-create:org-a", "key-1", _createdAt, Guid.Empty));
        Assert.Equal("resultId", exception.ParamName);
    }

    // --- Store -----------------------------------------------------------------

    [Fact]
    public async Task Add_then_find_round_trips()
    {
        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            var result = await store.AddAsync(
                IdempotencyKey.Create("reveal:org-a", "key-1", _createdAt), CancellationToken.None);
            Assert.Equal(IdempotencyKeyAddResult.Added, result);
        }

        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            var found = await store.FindAsync("reveal:org-a", "key-1", CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal("reveal:org-a", found.Scope);
            Assert.Equal("key-1", found.Key);
        }
    }

    [Fact]
    public async Task Add_then_find_round_trips_the_result_id()
    {
        // CORE-DX-004: a create/purchase-verification key persists the produced resource id so a retry can
        // re-load and return the original result.
        var resultId = Guid.CreateVersion7();
        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            await store.AddAsync(
                IdempotencyKey.Create("session-create:org-a", "key-1", _createdAt, resultId), CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            var found = await store.FindAsync("session-create:org-a", "key-1", CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal(resultId, found.ResultId);
        }
    }

    [Fact]
    public async Task Recording_the_same_scope_and_key_twice_is_a_duplicate()
    {
        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            Assert.Equal(
                IdempotencyKeyAddResult.Added,
                await store.AddAsync(IdempotencyKey.Create("reveal:org-a", "key-1", _createdAt), CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var store = new IdempotencyKeyStore(context);
            // A second record of the SAME (scope, key) is rejected by the unique index — the
            // idempotency signal.
            Assert.Equal(
                IdempotencyKeyAddResult.Duplicate,
                await store.AddAsync(IdempotencyKey.Create("reveal:org-a", "key-1", _createdAt), CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_same_key_under_a_different_scope_is_independent()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context);

        Assert.Equal(
            IdempotencyKeyAddResult.Added,
            await store.AddAsync(IdempotencyKey.Create("reveal:org-a", "key-1", _createdAt), CancellationToken.None));
        // The same client key value under another tenant's scope is a different idempotency record.
        Assert.Equal(
            IdempotencyKeyAddResult.Added,
            await store.AddAsync(IdempotencyKey.Create("reveal:org-b", "key-1", _createdAt), CancellationToken.None));
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_key()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context);

        Assert.Null(await store.FindAsync("reveal:org-a", "missing", CancellationToken.None));
    }

    [Fact]
    public async Task Find_rejects_blank_arguments()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context);

        await Assert.ThrowsAsync<ArgumentException>(() => store.FindAsync("", "key", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.FindAsync("scope", "  ", CancellationToken.None));
    }

    // --- Retention purge (CORE-PRIV-006) ---------------------------------------

    [Fact]
    public async Task Delete_created_before_removes_only_older_rows_and_returns_the_count()
    {
        var window = new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero);
        await SeedKeyAsync("reveal:org-a", "old-1", window - TimeSpan.FromDays(40));
        await SeedKeyAsync("reveal:org-a", "old-2", window - TimeSpan.FromDays(31));
        await SeedKeyAsync("reveal:org-a", "fresh", window - TimeSpan.FromHours(1));

        await using (var context = CreateContext())
        {
            // Bound by age alone: the two rows recorded before the window are removed; the recent one survives.
            var deleted = await new IdempotencyKeyStore(context)
                .DeleteCreatedBeforeAsync(window - TimeSpan.FromDays(30), maxCount: 100, CancellationToken.None);
            Assert.Equal(2, deleted);
        }

        await using (var context = CreateContext())
        {
            Assert.Null(await new IdempotencyKeyStore(context).FindAsync("reveal:org-a", "old-1", CancellationToken.None));
            Assert.Null(await new IdempotencyKeyStore(context).FindAsync("reveal:org-a", "old-2", CancellationToken.None));
            Assert.NotNull(await new IdempotencyKeyStore(context).FindAsync("reveal:org-a", "fresh", CancellationToken.None));
        }
    }

    [Fact]
    public async Task Delete_created_before_is_bounded_by_the_batch_size()
    {
        var window = new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await SeedKeyAsync("reveal:org-a", $"old-{i}", window - TimeSpan.FromDays(40));
        }

        await using (var context = CreateContext())
        {
            // A single sweep can never do unbounded work: only maxCount rows are removed this run.
            var deleted = await new IdempotencyKeyStore(context)
                .DeleteCreatedBeforeAsync(window - TimeSpan.FromDays(30), maxCount: 2, CancellationToken.None);
            Assert.Equal(2, deleted);
        }

        await using (var context = CreateContext())
        {
            // The remaining past-window row is covered by the next sweep (no starvation).
            var deleted = await new IdempotencyKeyStore(context)
                .DeleteCreatedBeforeAsync(window - TimeSpan.FromDays(30), maxCount: 2, CancellationToken.None);
            Assert.Equal(1, deleted);
        }
    }

    [Fact]
    public async Task Delete_created_before_is_idempotent_when_nothing_is_expired()
    {
        var window = new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero);
        await SeedKeyAsync("reveal:org-a", "fresh", window - TimeSpan.FromHours(1));

        await using var context = CreateContext();
        var deleted = await new IdempotencyKeyStore(context)
            .DeleteCreatedBeforeAsync(window - TimeSpan.FromDays(30), maxCount: 100, CancellationToken.None);

        // No rows past the window: a no-op that removes nothing and does not error.
        Assert.Equal(0, deleted);
        Assert.NotNull(await new IdempotencyKeyStore(context).FindAsync("reveal:org-a", "fresh", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_created_before_rejects_a_non_positive_batch_size()
    {
        await using var context = CreateContext();
        var store = new IdempotencyKeyStore(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.DeleteCreatedBeforeAsync(_createdAt, maxCount: 0, CancellationToken.None));
    }

    private async Task SeedKeyAsync(string scope, string key, DateTimeOffset createdAt)
    {
        await using var context = CreateContext();
        await new IdempotencyKeyStore(context).AddAsync(
            IdempotencyKey.Create(scope, key, createdAt), CancellationToken.None);
    }
}
