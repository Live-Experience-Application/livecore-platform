// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Database-backed tests for <see cref="AuditLogChainVerifier"/> and the sealed append path — the tamper-evident
/// audit-log hash chain (CORE-SEC-003, the "Security Hardening" epic). They run against an in-memory SQLite
/// database so the real model mapping, the per-tenant <c>audit_log_sequences</c> allocator and the chain columns
/// are exercised on every run without a database server (the relational semantics are shared with PostgreSQL;
/// provider-specific verification happens in the deployment pipeline).
///
/// They cover the story's required behavior — "the verifier passes on an untampered chain" and "tampering with a
/// persisted entry breaks chain verification" — by appending a chain through the production append path and then
/// mutating a PERSISTED row directly with raw SQL (standing in for a DB-level actor or a regression that bypasses
/// the immutable append API), plus the deletion case and the mandatory negative tenant-isolation case (threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md; AGENTS.md; docs/17_DEFINITION_OF_DONE.md): verifying one tenant never
/// reads another tenant's records, and a break in one tenant never implicates another. All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class AuditLogChainVerifierTests : IDisposable
{
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AuditLogChainVerifierTests()
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

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    /// <summary>Appends <paramref name="count"/> chained entries for the tenant (chronological event times).</summary>
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

    private async Task<AuditLogChainVerificationResult> VerifyAsync(Guid organizationId)
    {
        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context));
        return await verifier.VerifyAsync(organizationId, CancellationToken.None);
    }

    [Fact]
    public async Task The_verifier_passes_on_an_untampered_chain()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        await AppendChainAsync(organization.Id, count: 5);

        var result = await VerifyAsync(organization.Id);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedCount);
        Assert.Equal(0, result.LegacyCount);
        Assert.Null(result.FirstBrokenEntryId);
    }

    [Fact]
    public async Task The_verifier_passes_for_a_tenant_with_no_entries()
    {
        var organization = await SeedOrganizationAsync(_slugA);

        var result = await VerifyAsync(organization.Id);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedCount);
    }

    [Fact]
    public async Task Tampering_with_a_persisted_entry_breaks_chain_verification()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 4);
        var tampered = entries[1];

        // Stand in for a DB-level actor altering history directly: change a persisted entry's recorded action
        // WITHOUT recomputing its stored hash (the immutable append API offers no way to do this). The verifier
        // recomputes the hash over the altered content and finds it no longer matches the stored hash.
        await using (var context = CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE audit_logs SET action = 'SessionCancelled' WHERE id = {tampered.Id}");
        }

        var result = await VerifyAsync(organization.Id);

        Assert.False(result.IsValid);
        Assert.Equal(tampered.Id, result.FirstBrokenEntryId);
        Assert.Equal(tampered.Sequence, result.FirstBrokenSequence);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Deleting_a_persisted_entry_breaks_chain_verification()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entries = await AppendChainAsync(organization.Id, count: 4);
        var deleted = entries[1];

        // Deleting a row leaves the next entry's previous-hash link dangling and a gap in the gap-free sequence.
        await using (var context = CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM audit_logs WHERE id = {deleted.Id}");
        }

        var result = await VerifyAsync(organization.Id);

        Assert.False(result.IsValid);
        // The break surfaces at the entry that followed the deleted one (its link/sequence no longer match).
        Assert.Equal(entries[2].Id, result.FirstBrokenEntryId);
    }

    [Fact]
    public async Task Verification_is_tenant_scoped_and_a_break_in_one_tenant_does_not_implicate_another()
    {
        // Mandatory negative tenant-isolation test (threat T5): two tenants each hold a valid chain; tampering
        // tenant B's chain must not affect tenant A's verification, and A's verification must never read B's rows.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        await AppendChainAsync(organizationA.Id, count: 3);
        var bEntries = await AppendChainAsync(organizationB.Id, count: 3);

        await using (var context = CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE audit_logs SET action = 'SessionEnded' WHERE id = {bEntries[0].Id}");
        }

        var resultA = await VerifyAsync(organizationA.Id);
        var resultB = await VerifyAsync(organizationB.Id);

        // Tenant A is intact and untouched by tenant B's tampering.
        Assert.True(resultA.IsValid);
        Assert.Equal(3, resultA.VerifiedCount);
        // Tenant B's tampering is detected, scoped to tenant B.
        Assert.False(resultB.IsValid);
        Assert.Equal(bEntries[0].Id, resultB.FirstBrokenEntryId);
    }

    [Fact]
    public async Task The_verifier_rejects_an_empty_tenant_id()
    {
        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context));

        await Assert.ThrowsAsync<ArgumentException>(
            () => verifier.VerifyAsync(Guid.Empty, CancellationToken.None));
    }
}
