// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Data;
using System.Data.Common;
using System.Globalization;
using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the INDEX-BACKED tenant audit-log page read (CORE-PERF-007). The view-audit-log
/// page read (<see cref="AuditLogRepository.ListPageByOrganizationAsync"/>, reached live by
/// <c>GET /api/v1/audit-logs</c>) and the full id-ordered read (<see cref="AuditLogRepository.ListByOrganizationAsync"/>)
/// are <c>WHERE organization_id = X ORDER BY id</c> with <c>OFFSET</c>/<c>LIMIT</c>. Before this story the only
/// declared <c>audit_logs</c> indexes — <c>(organization_id, created_at)</c> and the unique
/// <c>(organization_id, sequence)</c> — led with the tenant but ordered by a DIFFERENT column, so neither
/// satisfied the id ordering and the database sorted the matched tenant prefix on every page. This story adds
/// the covering index <c>audit_logs(organization_id, id)</c> so the read is index-backed with no full sort,
/// independent of offset depth.
///
/// The story's required tests, over a LARGE seeded tenant log:
/// <list type="bullet">
///   <item>correct pages independent of offset depth (a shallow, a middle and a DEEP page each return exactly
///   the right slice in id order);</item>
///   <item>an index-backed ordering with NO full sort, independent of offset depth — asserted by inspecting the
///   query plan (PostgreSQL <c>EXPLAIN</c>: no <c>Sort</c> node and the covering index is used; the SQLite test
///   provider's <c>EXPLAIN QUERY PLAN</c>: the covering index is used and there is no <c>TEMP B-TREE</c> sort),
///   at both a shallow and a deep offset;</item>
///   <item>the chosen covering index exists (the migration's <c>CreateIndex</c> operation, and the index on the
///   live model the host runs against).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AuditLogPageIndexBackedReadTests
{
    private const string _orgSlug = "northwind-labs";

    // Enough entries that a "deep" page is meaningful and the PostgreSQL planner has real row statistics to
    // prefer the ordering index over a small-table sequential scan + sort.
    private const int _seededEntries = 1200;

    // Other tenants whose interleaved logs make `WHERE organization_id = target` SELECTIVE. With a single
    // tenant every row matches the filter, so PostgreSQL can serve `ORDER BY id` straight off the primary-key
    // (id) index and the (organization_id, id) covering index never appears in the plan; seeding several other
    // tenants makes the target a minority so the covering index is the planner's choice.
    private const int _noiseOrganizations = 4;

    private static readonly DateTimeOffset _t0 = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_paged_read_returns_correct_pages_independent_of_offset_depth()
    {
        await using var factory = new WorkspaceApiFactory();
        var seeded = await SeedLargeTenantLogAsync(factory, _seededEntries);

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        const int take = 10;

        // A shallow, a middle and a DEEP page each return exactly the expected slice of the append-ordered log
        // (ordered by the time-ordered surrogate id), so paging correctness does not degrade as the offset grows.
        await AssertPageAsync(repository, seeded, skip: 0, take);
        await AssertPageAsync(repository, seeded, skip: _seededEntries / 2, take);
        await AssertPageAsync(repository, seeded, skip: _seededEntries - take, take);

        // An offset past the end of the log returns nothing (still correct at the deepest possible offset).
        var pastEnd = await repository.ListPageByOrganizationAsync(
            seeded.OrganizationId, skip: _seededEntries, take, CancellationToken.None);
        Assert.Empty(pastEnd);
    }

    [Fact]
    public async Task The_paged_read_is_index_backed_with_no_full_sort_independent_of_offset_depth()
    {
        await using var factory = new WorkspaceApiFactory();
        var seeded = await SeedLargeTenantLogAsync(factory, _seededEntries);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        const int take = 10;
        var deepOffset = _seededEntries - (_seededEntries / 4); // a genuinely deep page, well past the start.

        // The SAME index-backed, no-full-sort plan whether the page is at the very start or deep into the log:
        // the covering index audit_logs(organization_id, id) satisfies both the tenant equality and the id
        // ordering, so the database never sorts the matched tenant prefix regardless of offset depth.
        await AssertIndexBackedNoSortAsync(context, seeded.OrganizationId, skip: 0, take);
        await AssertIndexBackedNoSortAsync(context, seeded.OrganizationId, skip: deepOffset, take);
    }

    [Fact]
    public void The_covering_index_is_created_by_a_migration()
    {
        // The chosen covering index audit_logs(organization_id, id) (CORE-PERF-007) is created by the
        // checked-in migration — asserted by inspecting the migration's Up operations directly.
        var migration = new LiveCore.Api.Persistence.Migrations.AddAuditLogOrganizationIdIndex();

        var createIndex = Assert.Single(
            migration.UpOperations.OfType<CreateIndexOperation>(),
            operation => operation.Name == "ix_audit_logs_organization_id_id");

        Assert.Equal("audit_logs", createIndex.Table);
        Assert.Equal(new[] { "organization_id", "id" }, createIndex.Columns);
        Assert.False(createIndex.IsUnique);
    }

    [Fact]
    public async Task The_covering_index_exists_in_the_running_schema()
    {
        // The same index is present on the live AuditLogEntry model the host runs against (the schema the
        // migration produces; the model<->migration consistency test ties the two together).
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var entityType = context.Model.FindEntityType(typeof(AuditLogEntry));
        Assert.NotNull(entityType);
        var index = entityType.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "ix_audit_logs_organization_id_id");

        Assert.NotNull(index);
        Assert.Equal(
            new[] { "OrganizationId", "Id" },
            index.Properties.Select(property => property.Name).ToArray());
        Assert.False(index.IsUnique);
    }

    // =====================================================================
    // Assertions.
    // =====================================================================

    private static async Task AssertPageAsync(IAuditLogRepository repository, SeededLog seeded, int skip, int take)
    {
        var page = await repository.ListPageByOrganizationAsync(seeded.OrganizationId, skip, take, CancellationToken.None);

        var expected = seeded.OrderedEntryIds.Skip(skip).Take(take).ToArray();
        Assert.Equal(expected, page.Select(entry => entry.Id).ToArray());
    }

    private static async Task AssertIndexBackedNoSortAsync(
        LiveCoreDbContext context, Guid organizationId, int skip, int take)
    {
        var plan = await ExplainPagedReadAsync(context, organizationId, skip, take);
        var planText = string.Join('\n', plan);

        Assert.True(
            planText.Contains("ix_audit_logs_organization_id_id", StringComparison.OrdinalIgnoreCase),
            $"The paged read at offset {skip} did not use the covering index. Plan:\n{planText}");

        // The whole point of the covering index: the matched tenant prefix is never sorted. PostgreSQL reports a
        // materializing sort as a "Sort" node; the SQLite test provider reports one as "USE TEMP B-TREE FOR
        // ORDER BY".
        var sortMarker = context.Database.IsSqlite() ? "TEMP B-TREE" : "Sort";
        Assert.False(
            planText.Contains(sortMarker, StringComparison.OrdinalIgnoreCase),
            $"The paged read at offset {skip} performed a full sort ('{sortMarker}'). Plan:\n{planText}");
    }

    // =====================================================================
    // Query-plan helper.
    // =====================================================================

    private static async Task<IReadOnlyList<string>> ExplainPagedReadAsync(
        LiveCoreDbContext context, Guid organizationId, int skip, int take)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        if (context.Database.IsNpgsql())
        {
            // Give the planner real row statistics so it reliably prefers the ordering index over a
            // sequential scan + sort.
            using var analyze = connection.CreateCommand();
            analyze.CommandText = "ANALYZE audit_logs;";
            await analyze.ExecuteNonQueryAsync();
        }

        // Mirror the repository's WHERE/ORDER BY/OFFSET/LIMIT shape exactly; the plan depends on those, not on
        // the projected columns or the literal values (EXPLAIN — without ANALYZE — only plans, never executes).
        var explainPrefix = context.Database.IsSqlite() ? "EXPLAIN QUERY PLAN " : "EXPLAIN ";
        using var command = connection.CreateCommand();
        command.CommandText = explainPrefix
            + "SELECT * FROM audit_logs WHERE organization_id = @org ORDER BY id LIMIT @take OFFSET @skip";
        AddParameter(command, "@org", organizationId);
        AddParameter(command, "@take", take);
        AddParameter(command, "@skip", skip);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            lines.Add(string.Join(' ', values));
        }

        return lines;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    /// <summary>
    /// Seeds the TARGET tenant with a LARGE append-only audit log (plus several other tenants' logs as noise so
    /// the target's read is selective - see <see cref="_noiseOrganizations"/>). Each entry is sealed with a distinct, strictly
    /// increasing append sequence (so the unique <c>audit_logs(organization_id, sequence)</c> index is
    /// satisfied) and a distinct, strictly increasing event time (so the time-ordered UUIDv7 surrogate ids
    /// order deterministically). The entries are inserted in one batch — the legitimate append (Added) write
    /// the tamper-protection interceptor allows (CORE-SEC-004) — so a large log is built cheaply.
    /// </summary>
    private static async Task<SeededLog> SeedLargeTenantLogAsync(WorkspaceApiFactory factory, int count)
    {
        var organizationId = Guid.Empty;
        var orderedIds = new List<Guid>(count);

        await factory.SeedAsync(async db =>
        {
            var organization = await db.AddOrganizationAsync(_orgSlug);
            organizationId = organization.Id;

            var entries = new List<AuditLogEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = AuditLogEntry.Create(
                    organization.Id,
                    workspaceId: Guid.CreateVersion7(),
                    AuditAction.SessionStarted,
                    actorUserProfileId: Guid.CreateVersion7(),
                    resourceType: null,
                    resourceId: null,
                    targetParticipantId: null,
                    previousState: null,
                    newState: null,
                    // Distinct milliseconds keep the UUIDv7 ids strictly increasing in seed order.
                    createdAt: _t0.AddMilliseconds(i));
                entry.Seal(i + 1, previousHash: null);
                entries.Add(entry);
                orderedIds.Add(entry.Id);
            }

            // NOISE from other tenants so `WHERE organization_id = target` is SELECTIVE (see _noiseOrganizations).
            // It is deliberately NOT added to orderedIds, so the page assertions still compare against the target
            // tenant's entries only; each other tenant gets its own per-tenant-unique sequence run.
            for (var n = 0; n < _noiseOrganizations; n++)
            {
                var otherOrganization = await db.AddOrganizationAsync($"{_orgSlug}-other-{n}");
                for (var i = 0; i < count; i++)
                {
                    var noise = AuditLogEntry.Create(
                        otherOrganization.Id,
                        workspaceId: Guid.CreateVersion7(),
                        AuditAction.SessionStarted,
                        actorUserProfileId: Guid.CreateVersion7(),
                        resourceType: null,
                        resourceId: null,
                        targetParticipantId: null,
                        previousState: null,
                        newState: null,
                        createdAt: _t0.AddMilliseconds(i));
                    noise.Seal(i + 1, previousHash: null);
                    entries.Add(noise);
                }
            }

            db.AuditLogs.AddRange(entries);
            await db.SaveChangesAsync();
        });

        // The seed order is already ascending id order (distinct, increasing event times), which is the order
        // the id-backed read returns.
        return new SeededLog(organizationId, orderedIds);
    }

    private sealed record SeededLog(Guid OrganizationId, IReadOnlyList<Guid> OrderedEntryIds);
}
