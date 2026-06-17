// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Tests the in-process TAMPER-PROOFING of the append-only audit log (CORE-SEC-004): the audit read paths return
/// NON-TRACKED entities, and the <see cref="AuditLogTamperProtectionInterceptor"/> blocks any <c>SaveChanges</c>
/// that would <c>UPDATE</c> or <c>DELETE</c> an <c>audit_logs</c> row, throwing fail-closed so nothing persists.
/// They run against an in-memory SQLite database with the interceptor wired exactly as a runtime context wires it
/// (the interceptor is provider-agnostic — it reads the change tracker, not SQL), so the real model mapping, the
/// append path and the chain are exercised on every run without a database server.
///
/// The suite covers the story's required behavior — a read of an audit entry followed by <c>SaveChanges</c> does
/// NOT persist a mutation (the interceptor blocks it), an attempted <c>UPDATE</c>/<c>DELETE</c> throws, the
/// append (<c>INSERT</c>) path still works, and the CORE-SEC-003 hash-chain verifier still verifies a clean chain
/// and still detects a tampered one — plus the mandatory tenant-scoping is inherited from the repository reads.
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogTamperProtectionTests : IDisposable
{
    private const string _slugA = "northwind-labs";

    private static readonly DateTimeOffset _now = new(2026, 6, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AuditLogTamperProtectionTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        // Wire the tamper-protection interceptor exactly as the runtime contexts do (UseLiveCoreNpgsql), so the
        // SQLite test path exercises the real guard rather than a stand-in.
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditLogTamperProtectionInterceptor())
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

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<IReadOnlyList<AuditLogEntry>> AppendChainAsync(Guid organizationId, int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(i => AuditLogEntry.Create(
                organizationId,
                workspaceId: Guid.NewGuid(),
                AuditAction.SessionStarted,
                actorUserProfileId: Guid.NewGuid(),
                resourceType: null,
                resourceId: null,
                targetParticipantId: null,
                previousState: null,
                newState: null,
                _now.AddSeconds(i)))
            .ToArray();

        await using var context = CreateContext();
        var repository = new AuditLogRepository(context);
        foreach (var entry in entries)
        {
            await repository.AppendAsync(entry, CancellationToken.None);
        }

        return entries;
    }

    [Fact]
    public async Task The_append_path_still_works_through_the_interceptor()
    {
        // The interceptor must allow the audit log's one legitimate write — an INSERT (EntityState.Added) — so the
        // sealed append path is untouched and the chain it builds verifies.
        var organization = await SeedOrganizationAsync(_slugA);
        await AppendChainAsync(organization.Id, count: 3);

        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context));
        var result = await verifier.VerifyAsync(organization.Id, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.VerifiedCount);
    }

    [Fact]
    public async Task The_repository_reads_return_non_tracked_entities()
    {
        // CORE-SEC-004: every audit read uses AsNoTracking, so a caller can never load a row, mutate it and have a
        // later SaveChanges silently write it back — the entity is detached.
        var organization = await SeedOrganizationAsync(_slugA);
        await AppendChainAsync(organization.Id, count: 2);

        await using var context = CreateContext();
        var repository = new AuditLogRepository(context);

        var list = await repository.ListByOrganizationAsync(organization.Id, CancellationToken.None);
        var page = await repository.ListPageByOrganizationAsync(organization.Id, skip: 0, take: 10, CancellationToken.None);
        var chain = await repository.ListChainByOrganizationAsync(organization.Id, CancellationToken.None);

        Assert.NotEmpty(list);
        Assert.NotEmpty(page);
        Assert.NotEmpty(chain);
        Assert.All(list, entry => Assert.Equal(EntityState.Detached, context.Entry(entry).State));
        Assert.All(page, entry => Assert.Equal(EntityState.Detached, context.Entry(entry).State));
        Assert.All(chain, entry => Assert.Equal(EntityState.Detached, context.Entry(entry).State));
    }

    [Fact]
    public async Task A_read_followed_by_a_mutation_and_save_changes_does_not_persist_the_mutation()
    {
        // The story's headline test: read an audit entry, then attempt to mutate it and SaveChanges. The
        // interceptor blocks it fail-closed and the persisted row is unchanged.
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 3);
        var target = entries[1];

        await using (var context = CreateContext())
        {
            // A tracking read (standing in for a regression that forgot AsNoTracking) plus a field mutation.
            var tracked = await context.AuditLogs.SingleAsync(e => e.Id == target.Id);
            context.Entry(tracked).Property(e => e.NewState).CurrentValue = "Tampered";

            var exception = await Assert.ThrowsAsync<AuditLogTamperException>(
                () => context.SaveChangesAsync(CancellationToken.None));
            Assert.Contains(target.Id, exception.EntryIds);
            Assert.Equal(nameof(EntityState.Modified), exception.RejectedState);
        }

        // The mutation never reached the database: the row reads back exactly as appended.
        await using (var verifyContext = CreateContext())
        {
            var persisted = await verifyContext.AuditLogs.AsNoTracking().SingleAsync(e => e.Id == target.Id);
            Assert.Null(persisted.NewState);
        }
    }

    [Fact]
    public async Task An_attempted_update_of_an_audit_row_throws_fail_closed()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 2);
        var target = entries[0];

        await using var context = CreateContext();
        var tracked = await context.AuditLogs.SingleAsync(e => e.Id == target.Id);
        // Force a full-row UPDATE without changing a field (an immutable entry exposes no setter); the interceptor
        // rejects the Modified state regardless.
        context.Entry(tracked).State = EntityState.Modified;

        await Assert.ThrowsAsync<AuditLogTamperException>(
            () => context.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_attempted_delete_of_an_audit_row_throws_fail_closed()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 2);
        var target = entries[0];

        await using (var context = CreateContext())
        {
            var tracked = await context.AuditLogs.SingleAsync(e => e.Id == target.Id);
            context.AuditLogs.Remove(tracked);

            var exception = await Assert.ThrowsAsync<AuditLogTamperException>(
                () => context.SaveChangesAsync(CancellationToken.None));
            Assert.Equal(nameof(EntityState.Deleted), exception.RejectedState);
            Assert.Contains(target.Id, exception.EntryIds);
        }

        // The deletion never reached the database: both rows are still present.
        await using (var verifyContext = CreateContext())
        {
            var count = await verifyContext.AuditLogs.AsNoTracking()
                .CountAsync(e => e.OrganizationId == organization.Id);
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public async Task The_chain_verifier_still_verifies_a_clean_chain_and_detects_a_tampered_one()
    {
        // The CORE-SEC-003 hash chain must keep working alongside the new tamper-proofing: a clean chain verifies,
        // and a row tampered OUT OF BAND (raw SQL, which does not pass through SaveChanges and so is intentionally
        // not caught by the interceptor) is still DETECTED by the verifier.
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 4);

        await using (var cleanContext = CreateContext())
        {
            var clean = await new AuditLogChainVerifier(new AuditLogRepository(cleanContext))
                .VerifyAsync(organization.Id, CancellationToken.None);
            Assert.True(clean.IsValid);
            Assert.Equal(4, clean.VerifiedCount);
        }

        var tampered = entries[2];
        await using (var rawContext = CreateContext())
        {
            await rawContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE audit_logs SET action = 'SessionEnded' WHERE id = {tampered.Id}");
        }

        await using (var verifyContext = CreateContext())
        {
            var result = await new AuditLogChainVerifier(new AuditLogRepository(verifyContext))
                .VerifyAsync(organization.Id, CancellationToken.None);
            Assert.False(result.IsValid);
            Assert.Equal(tampered.Id, result.FirstBrokenEntryId);
        }
    }
}
