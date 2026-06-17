// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Tests that EVERY audit append runs inside a transactional unit of work (CORE-CONC-008): the per-tenant
/// <c>audit_log_sequences</c> allocation, the previous-hash read and the insert commit together, so the
/// sequence-counter row lock is held until the insert commits and the tamper-evident hash chain (CORE-SEC-003)
/// cannot fork.
///
/// These run against an in-memory SQLite database (one shared connection, a fresh context per step) exactly like
/// <see cref="AuditLogRepositoryTests"/> and <see cref="AuditLogChainVerifierTests"/>, so the real model mapping,
/// the per-tenant sequence allocator and the chain columns are exercised without a database server. The
/// single-connection SQLite provider serializes writes, so these cover the DETERMINISTIC, behaviour-level
/// guarantees — a long run of appends stays on one gap-free chain, a direct append opens and commits its OWN unit
/// of work, and an append inside a command's transaction ENROLS in it (rolling the command back removes the audit
/// row too). The TRUE multi-connection write race that exercises the row lock runs on the PostgreSQL CI leg
/// (the integration suite's <c>AuditChainAppendConcurrencyTests</c>). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogAppendUnitOfWorkTests : IDisposable
{
    private const string _slug = "northwind-labs";

    private static readonly DateTimeOffset _now = new(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AuditLogAppendUnitOfWorkTests()
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

    private async Task<Organization> SeedOrganizationAsync()
    {
        var organization = Organization.Create(_slug, _slug, _now);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private static AuditLogEntry GenericEntry(Guid organizationId, DateTimeOffset createdAt)
        => AuditLogEntry.Create(
            organizationId,
            workspaceId: Guid.NewGuid(),
            AuditAction.SessionStarted,
            actorUserProfileId: Guid.NewGuid(),
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);

    [Fact]
    public async Task Many_same_tenant_appends_form_a_single_unbroken_gap_free_chain()
    {
        var organization = await SeedOrganizationAsync();
        const int count = 25;

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            for (var i = 0; i < count; i++)
            {
                await repository.AppendAsync(GenericEntry(organization.Id, _now.AddSeconds(i)), CancellationToken.None);
            }
        }

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            var chain = await repository.ListChainByOrganizationAsync(organization.Id, CancellationToken.None);

            // Strictly monotonic, gap-free 1..count: no number skipped, none duplicated, none forked.
            Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), chain.Select(e => e.Sequence));

            // No fork and no false-positive tamper detection: the verifier reports a clean chain.
            var result = await new AuditLogChainVerifier(repository)
                .VerifyAsync(organization.Id, CancellationToken.None);
            Assert.True(result.IsValid);
            Assert.Equal(count, result.VerifiedCount);
            Assert.Equal(0, result.LegacyCount);
            Assert.Null(result.FirstBrokenEntryId);
        }
    }

    [Fact]
    public async Task An_append_outside_a_command_transaction_opens_and_commits_its_own_unit_of_work()
    {
        // The previously direct (non-ambient) appends now open their OWN unit of work (CORE-CONC-008): after a
        // direct append the entry is durably committed and sealed, and no transaction is left dangling.
        var organization = await SeedOrganizationAsync();
        var entry = GenericEntry(organization.Id, _now);

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            await repository.AppendAsync(entry, CancellationToken.None);

            // The self-opened unit of work committed and disposed its transaction — none is left open.
            Assert.Null(context.Database.CurrentTransaction);
        }

        await using (var context = CreateContext())
        {
            var stored = Assert.Single(await context.AuditLogs.AsNoTracking().ToListAsync());
            Assert.Equal(entry.Id, stored.Id);
            Assert.Equal(1, stored.Sequence);
            Assert.NotNull(stored.EntryHash);
        }
    }

    [Fact]
    public async Task An_append_inside_an_ambient_transaction_enrols_in_it_and_rolls_back_with_it()
    {
        // When a CORE-CONC-002 command already owns a transaction the append ENROLS in it instead of
        // self-committing, so rolling the command back removes the audit row too — it never committed
        // independently. This is the behaviour the reveal/hide, session, deletion, retention and store handlers
        // rely on (their audit append commits or rolls back atomically with the rest of the command).
        var organization = await SeedOrganizationAsync();
        var entry = GenericEntry(organization.Id, _now);

        await using (var context = CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var repository = new AuditLogRepository(context);
            await repository.AppendAsync(entry, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        await using (var context = CreateContext())
        {
            // The append enrolled in the rolled-back ambient transaction, so nothing was persisted — neither the
            // audit row nor its allocated sequence counter.
            Assert.Empty(await context.AuditLogs.AsNoTracking().ToListAsync());
            Assert.Empty(await context.AuditLogSequences.AsNoTracking().ToListAsync());
        }
    }
}
