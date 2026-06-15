using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Store;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Model-shape tests for the optimistic-concurrency tokens (CORE-CONC-001, extended
/// to the remaining mutable aggregates by CORE-CONC-006). They
/// build the EF model offline (no connection is opened) and assert that every mutable
/// aggregate carries the PostgreSQL <c>xmin</c> row-version concurrency token on the
/// Npgsql provider — the mapping that makes a concurrent read-modify-write fail with a
/// <c>DbUpdateConcurrencyException</c> rather than silently overwriting — and that the
/// SQLite test provider carries NO such token (xmin is a PostgreSQL-only system
/// column, so mapping it on SQLite would break that provider's schema and inserts).
/// </summary>
public sealed class OptimisticConcurrencyTokenModelTests
{
    private static readonly Type[] _mutableAggregates =
    [
        // The first six carry the token from CORE-CONC-001.
        typeof(Session),
        typeof(VisibilityRule),
        typeof(Workspace),
        typeof(Participant),
        typeof(QuotaUsage),
        typeof(PurchaseTransaction),

        // The remaining eight gained the token in CORE-CONC-006: every other aggregate
        // that is updated in place by a read-modify-write command.
        typeof(ContentBlock),
        typeof(Entity),
        typeof(EntityType),
        typeof(Scene),
        typeof(Asset),
        typeof(SubjectEntitlement),
        typeof(ExportJob),
        typeof(UserProfile),
    ];

    [Fact]
    public void Postgres_model_maps_xmin_as_a_concurrency_token_on_every_mutable_aggregate()
    {
        using var context = NpgsqlContext();

        foreach (var aggregate in _mutableAggregates)
        {
            var entityType = context.Model.FindEntityType(aggregate);
            Assert.NotNull(entityType);

            var token = entityType!.FindProperty("xmin");
            Assert.NotNull(token);
            Assert.Equal("xmin", token!.GetColumnName());
            Assert.True(token.IsConcurrencyToken, $"xmin must be a concurrency token on {aggregate.Name}.");

            // IsRowVersion() maps the token read-only: the database (PostgreSQL) owns
            // the value and bumps it on every write, so EF never sends it on insert.
            Assert.Equal(ValueGenerated.OnAddOrUpdate, token.ValueGenerated);
        }
    }

    [Fact]
    public void Sqlite_model_carries_no_xmin_token_so_the_default_test_provider_is_unaffected()
    {
        using var context = SqliteContext();

        foreach (var aggregate in _mutableAggregates)
        {
            var entityType = context.Model.FindEntityType(aggregate);
            Assert.NotNull(entityType);
            Assert.Null(entityType!.FindProperty("xmin"));
        }
    }

    private static LiveCoreDbContext NpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql("Host=localhost;Database=livecore-concurrency-model-check")
            .Options;
        return new LiveCoreDbContext(options);
    }

    private static LiveCoreDbContext SqliteContext()
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new LiveCoreDbContext(options);
    }
}
