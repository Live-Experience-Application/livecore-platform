using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="SubjectEntitlementRepository"/> (CORE-ENTL-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, the SQL translation, the unique per-subject index, the value-kind/subject-type
/// string conversions and the entitlement-definition foreign key are exercised on every run without any database
/// server. The behaviors under test (round-trip, per-subject-scoped lookups, the active-only read, the
/// duplicate-assignment guard, cross-subject and cross-subject-type isolation, and the restricted definition
/// foreign key) are relational semantics shared with PostgreSQL.
///
/// A subject entitlement is a PER-SUBJECT assignment keyed by the (subject_type, subject_id) pair, not a
/// tenant-scoped row, so the fail-closed negative cases that matter here are NEGATIVE LOOKUP and ISOLATION: one
/// subject's premium state is never returned through another subject's id, a user subject and a workspace
/// subject that share a guid never collide, a revoked assignment is excluded from the active read, and a
/// duplicate assignment is rejected. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SubjectEntitlementRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _grantedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SubjectEntitlementRepositoryTests()
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

    private async Task<EntitlementDefinition> SeedDefinitionAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    private static EntitlementDefinition FlagDefinition(string key = "ads.disabled")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Ad-free experience", null, _grantedAt);

    private static EntitlementDefinition QuotaDefinition(string key = "workspace.active.max")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Active workspace limit", null, _grantedAt);

    // --- Round-trip -------------------------------------------------------------

    [Fact]
    public async Task Assignment_round_trips_through_the_database()
    {
        var definition = await SeedDefinitionAsync(QuotaDefinition());
        var subjectId = Guid.CreateVersion7();
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, subjectId, definition, limit: 4, sourcePlanDefinitionId: null, _grantedAt);

        await using (var context = CreateContext())
        {
            var repository = new SubjectEntitlementRepository(context);
            Assert.Equal(SubjectEntitlementAddResult.Added, await repository.AddAsync(assignment, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var repository = new SubjectEntitlementRepository(context);
            var loaded = await repository.FindBySubjectAndEntitlementAsync(
                EntitlementSubjectType.User, subjectId, definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(assignment.Id, loaded.Id);
            Assert.Equal(EntitlementSubjectType.User, loaded.SubjectType);
            Assert.Equal(subjectId, loaded.SubjectId);
            Assert.Equal(definition.Id, loaded.EntitlementDefinitionId);
            Assert.Equal("workspace.active.max", loaded.EntitlementKey);
            Assert.Equal(EntitlementValueKind.Quota, loaded.ValueKind);
            Assert.Equal(4, loaded.QuotaLimit);
            Assert.Null(loaded.FlagValue);
            Assert.Null(loaded.SourcePlanDefinitionId);
            Assert.True(loaded.IsActive);
            Assert.Equal(_grantedAt, loaded.GrantedAt);
        }
    }

    // --- Duplicate assignment ---------------------------------------------------

    [Fact]
    public async Task Adding_a_second_assignment_of_the_same_entitlement_to_the_same_subject_is_rejected()
    {
        var definition = await SeedDefinitionAsync(FlagDefinition());
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);
        Assert.Equal(
            SubjectEntitlementAddResult.Added,
            await repository.AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, definition, true, null, _grantedAt),
                CancellationToken.None));

        // A different value, same subject + same entitlement: the unique index rejects it fail-closed rather
        // than creating a second, conflicting premium grant.
        var result = await repository.AddAsync(
            SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, definition, false, null, _grantedAt),
            CancellationToken.None);

        Assert.Equal(SubjectEntitlementAddResult.DuplicateAssignment, result);
        Assert.Single(await repository.ListBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None));
    }

    // --- Cross-subject isolation (NEGATIVE lookup) ------------------------------

    [Fact]
    public async Task One_subjects_assignment_is_never_returned_through_another_subjects_id()
    {
        var definition = await SeedDefinitionAsync(FlagDefinition());
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);
        await repository.AddAsync(
            SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectA, definition, true, null, _grantedAt),
            CancellationToken.None);

        // Subject B never granted it: the lookup and the list are both empty for B (fail-closed).
        Assert.Null(await repository.FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType.User, subjectB, definition.Id, CancellationToken.None));
        Assert.Empty(await repository.ListBySubjectAsync(EntitlementSubjectType.User, subjectB, CancellationToken.None));
    }

    [Fact]
    public async Task A_user_subject_and_a_workspace_subject_with_the_same_guid_do_not_collide()
    {
        var definition = await SeedDefinitionAsync(FlagDefinition());
        var sharedId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);

        // The unique key includes subject_type, so the SAME guid as a user and as a workspace are two distinct
        // subjects: both assignments are accepted, each resolves only to its own.
        Assert.Equal(
            SubjectEntitlementAddResult.Added,
            await repository.AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, sharedId, definition, true, null, _grantedAt),
                CancellationToken.None));
        Assert.Equal(
            SubjectEntitlementAddResult.Added,
            await repository.AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.Workspace, sharedId, definition, false, null, _grantedAt),
                CancellationToken.None));

        var asUser = await repository.FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType.User, sharedId, definition.Id, CancellationToken.None);
        var asWorkspace = await repository.FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType.Workspace, sharedId, definition.Id, CancellationToken.None);

        Assert.NotNull(asUser);
        Assert.NotNull(asWorkspace);
        Assert.True(asUser.FlagValue);
        Assert.False(asWorkspace.FlagValue);
    }

    // --- Active-only read -------------------------------------------------------

    [Fact]
    public async Task ListActiveBySubject_excludes_a_revoked_assignment()
    {
        var activeDefinition = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        var revokedDefinition = await SeedDefinitionAsync(FlagDefinition("export.enabled"));
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);
        await repository.AddAsync(
            SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, activeDefinition, true, null, _grantedAt),
            CancellationToken.None);

        var revoked = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, subjectId, revokedDefinition, true, null, _grantedAt);
        revoked.Revoke(_grantedAt.AddHours(1));
        await repository.AddAsync(revoked, CancellationToken.None);

        var active = await repository.ListActiveBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None);
        var all = await repository.ListBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None);

        Assert.Equal(new[] { "ads.disabled" }, active.Select(a => a.EntitlementKey).ToArray());
        Assert.Equal(2, all.Count); // the revoked assignment is retained, just excluded from the active read
    }

    // --- Update path ------------------------------------------------------------

    [Fact]
    public async Task Update_persists_a_revocation_in_place()
    {
        var definition = await SeedDefinitionAsync(FlagDefinition());
        var subjectId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            var repository = new SubjectEntitlementRepository(context);
            await repository.AddAsync(
                SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, subjectId, definition, true, null, _grantedAt),
                CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SubjectEntitlementRepository(context);
            var loaded = await repository.FindBySubjectAndEntitlementAsync(
                EntitlementSubjectType.User, subjectId, definition.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.Revoke(_grantedAt.AddHours(1));
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SubjectEntitlementRepository(context);
            Assert.Empty(await repository.ListActiveBySubjectAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None));
        }
    }

    // --- Foreign key integrity --------------------------------------------------

    [Fact]
    public async Task Assigning_an_entitlement_whose_definition_does_not_exist_is_rejected()
    {
        // The definition is built but NEVER persisted, so the entitlement_definition_id foreign key has no
        // target: the restricted foreign key rejects the insert (and it is not a duplicate, so it surfaces).
        var orphanDefinition = QuotaDefinition();
        var subjectId = Guid.CreateVersion7();

        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, subjectId, orphanDefinition, 1, null, _grantedAt),
            CancellationToken.None));
    }

    // --- Guards -----------------------------------------------------------------

    [Fact]
    public async Task FindBySubjectAndEntitlement_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType.User, Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType.User, Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListBySubject_rejects_an_empty_subject_id()
    {
        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySubjectAsync(
            EntitlementSubjectType.User, Guid.Empty, CancellationToken.None));
    }
}
