using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Migration consistency tests (CORE-ID-002; docs/10_DATABASE_SCHEMA.md:
/// every schema change requires a migration).
/// </summary>
public class LiveCoreDbContextTests
{
    [Fact]
    public void Model_has_no_changes_that_are_missing_from_the_checked_in_migrations()
    {
        // Builds the PostgreSQL model offline (no connection is opened) and
        // compares it with the checked-in migration snapshot. Fails when a
        // model change was made without scaffolding the matching migration.
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql("Host=localhost;Database=livecore-model-check")
            .Options;
        using var context = new LiveCoreDbContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF Core model differs from the migration snapshot; scaffold a migration for the change.");
    }
}
