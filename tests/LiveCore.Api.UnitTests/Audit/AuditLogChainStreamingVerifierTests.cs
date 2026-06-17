// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Database-backed tests for STREAMED audit-log hash-chain verification (CORE-PERF-005). They prove the
/// acceptance criteria: chain verification reads the chain in ORDERED SEGMENTS via the
/// <c>(organization_id, sequence)</c> cursor instead of materializing a tenant's entire chain in memory, so
/// verification memory/time stay bounded as the audit log grows (the segmented reads are asserted through a
/// recording repository decorator), AND detection of a tampered/broken chain is unchanged — a clean chain verifies
/// and a tampered one is still detected at the FIRST broken entry, including a tamper that lies BEYOND the first
/// segment, with the read stopping at the break.
///
/// They run against in-memory SQLite (the same harness as <see cref="AuditLogChainVerifierTests"/>) so the real
/// model mapping, the per-tenant <c>audit_log_sequences</c> allocator and the chain columns are exercised, and use
/// the verifier's internal small-segment-size constructor (CORE-PERF-005) to cross segment boundaries over a small
/// chain rather than appending hundreds of rows. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogChainStreamingVerifierTests : IDisposable
{
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";
    private const int _segmentSize = 3;

    private static readonly DateTimeOffset _now = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AuditLogChainStreamingVerifierTests()
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

    private async Task<Organization> SeedOrganizationAsync(string slug = _slugA)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
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

    [Fact]
    public async Task Verification_reads_the_chain_in_bounded_ordered_segments()
    {
        // A chain of 7 with a segment size of 3 must be read as bounded windows 3 + 3 + 1, never in one
        // materializing read — this is the "segmented reads asserted, bounded memory" acceptance criterion.
        var organization = await SeedOrganizationAsync();
        await AppendChainAsync(organization.Id, count: 7);

        await using var context = CreateContext();
        var spy = new SegmentRecordingRepository(new AuditLogRepository(context));
        var verifier = new AuditLogChainVerifier(spy, _segmentSize);

        var result = await verifier.VerifyAsync(organization.Id, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(7, result.VerifiedCount);
        Assert.Equal(0, result.LegacyCount);

        // Only the bounded segment read was used — never the whole-chain materializing read.
        Assert.Empty(spy.FullChainReads);
        // Every read is capped at the segment size (bounded memory), the cursor advances by the
        // (organization_id, sequence) order, and the windows tile the chain with no gap or overlap (3 + 3 + 1).
        Assert.All(spy.SegmentReads, read => Assert.Equal(_segmentSize, read.Limit));
        Assert.Equal(new long?[] { null, 3L, 6L }, spy.SegmentReads.Select(read => read.AfterSequence).ToArray());
        Assert.Equal(new[] { 3, 3, 1 }, spy.SegmentReads.Select(read => read.ReturnedCount).ToArray());
    }

    [Fact]
    public async Task A_clean_chain_verifies_identically_to_the_materialized_read()
    {
        var organization = await SeedOrganizationAsync();
        await AppendChainAsync(organization.Id, count: 7);

        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context), _segmentSize);

        var result = await verifier.VerifyAsync(organization.Id, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(7, result.VerifiedCount);
        Assert.Null(result.FirstBrokenEntryId);
    }

    [Fact]
    public async Task A_tamper_beyond_the_first_segment_is_detected_at_the_first_broken_entry_and_stops_reading()
    {
        var organization = await SeedOrganizationAsync();
        var entries = await AppendChainAsync(organization.Id, count: 7);

        // The fifth entry (sequence 5) lives in the SECOND segment (sequences 4..6) for a segment size of 3, so
        // detection must work ACROSS a segment boundary. Alter its recorded action WITHOUT recomputing its stored
        // hash (the immutable append API offers no way to do this): a content-integrity break at that entry.
        var tampered = entries[4];
        Assert.Equal(5, tampered.Sequence);
        await using (var mutate = CreateContext())
        {
            await mutate.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE audit_logs SET action = 'SessionEnded' WHERE id = {tampered.Id}");
        }

        await using var context = CreateContext();
        var spy = new SegmentRecordingRepository(new AuditLogRepository(context));
        var verifier = new AuditLogChainVerifier(spy, _segmentSize);

        var result = await verifier.VerifyAsync(organization.Id, CancellationToken.None);

        // Detection is unchanged: the break is pinpointed at the first (and only) broken entry.
        Assert.False(result.IsValid);
        Assert.Equal(tampered.Id, result.FirstBrokenEntryId);
        Assert.Equal(tampered.Sequence, result.FirstBrokenSequence);
        Assert.NotNull(result.Reason);

        // Streaming stops at the break: it read the first segment (cursor null) and the second (cursor 3) where the
        // break lies, and did NOT read the third (cursor 6) — verification work is bounded and stops at the first
        // broken entry rather than scanning the rest of the chain.
        Assert.Equal(new long?[] { null, 3L }, spy.SegmentReads.Select(read => read.AfterSequence).ToArray());
    }

    [Fact]
    public async Task A_deletion_spanning_a_segment_boundary_is_still_detected()
    {
        var organization = await SeedOrganizationAsync();
        var entries = await AppendChainAsync(organization.Id, count: 7);

        // Delete the entry at the END of the first segment (sequence 3). The next entry (sequence 4) begins the
        // SECOND segment, so the dangling link / sequence gap must be detected across the boundary.
        var deleted = entries[2];
        Assert.Equal(3, deleted.Sequence);
        await using (var mutate = CreateContext())
        {
            await mutate.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM audit_logs WHERE id = {deleted.Id}");
        }

        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context), _segmentSize);

        var result = await verifier.VerifyAsync(organization.Id, CancellationToken.None);

        Assert.False(result.IsValid);
        // The break surfaces at the entry that followed the deleted one (its link/sequence no longer match).
        Assert.Equal(entries[3].Id, result.FirstBrokenEntryId);
    }

    [Fact]
    public async Task Segmented_verification_is_tenant_scoped_and_a_break_in_one_tenant_does_not_implicate_another()
    {
        // Mandatory negative tenant-isolation test (threat T5) for the SEGMENTED path: two tenants each hold a
        // multi-segment chain; tampering tenant B's chain must not affect tenant A's verification, and A's
        // segmented read must never return B's rows even as the cursor advances across segments.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        await AppendChainAsync(organizationA.Id, count: 7);
        var bEntries = await AppendChainAsync(organizationB.Id, count: 7);

        await using (var mutate = CreateContext())
        {
            await mutate.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE audit_logs SET action = 'SessionEnded' WHERE id = {bEntries[4].Id}");
        }

        await using var context = CreateContext();
        var resultA = await new AuditLogChainVerifier(new AuditLogRepository(context), _segmentSize)
            .VerifyAsync(organizationA.Id, CancellationToken.None);
        var resultB = await new AuditLogChainVerifier(new AuditLogRepository(context), _segmentSize)
            .VerifyAsync(organizationB.Id, CancellationToken.None);

        // Tenant A is intact and untouched by tenant B's tampering — all 7 of A's entries verified, none of B's
        // leaked across the tenant predicate as the segment cursor walked the chain.
        Assert.True(resultA.IsValid);
        Assert.Equal(7, resultA.VerifiedCount);
        // Tenant B's tampering is detected, scoped to tenant B.
        Assert.False(resultB.IsValid);
        Assert.Equal(bEntries[4].Id, resultB.FirstBrokenEntryId);
    }

    [Fact]
    public async Task The_streaming_verifier_rejects_an_empty_tenant_id()
    {
        await using var context = CreateContext();
        var verifier = new AuditLogChainVerifier(new AuditLogRepository(context), _segmentSize);

        await Assert.ThrowsAsync<ArgumentException>(
            () => verifier.VerifyAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public void The_verifier_rejects_a_non_positive_segment_size()
    {
        var repository = new AuditLogRepository(CreateContext());

        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditLogChainVerifier(repository, segmentSize: 0));
    }

    /// <summary>
    /// An <see cref="IAuditLogRepository"/> decorator that records every chain read the verifier performs, so a
    /// test can assert the verifier walks the chain in BOUNDED SEGMENTS via the <c>(organization_id, sequence)</c>
    /// cursor (CORE-PERF-005) and never falls back to the whole-chain materializing read. It delegates the actual
    /// reads to a real <see cref="AuditLogRepository"/> so the production SQL (the cursor predicate, the ordering
    /// and the cap) is exercised, not a stub.
    /// </summary>
    private sealed class SegmentRecordingRepository : IAuditLogRepository
    {
        private readonly IAuditLogRepository _inner;

        public SegmentRecordingRepository(IAuditLogRepository inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public List<(long? AfterSequence, int Limit, int ReturnedCount)> SegmentReads { get; } = new();

        public List<Guid> FullChainReads { get; } = new();

        public Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken)
            => _inner.AppendAsync(entry, cancellationToken);

        public Task<IReadOnlyList<AuditLogEntry>> ListByOrganizationAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
            => _inner.ListByOrganizationAsync(organizationId, cancellationToken);

        public Task<IReadOnlyList<AuditLogEntry>> ListPageByOrganizationAsync(
            Guid organizationId,
            int skip,
            int take,
            CancellationToken cancellationToken)
            => _inner.ListPageByOrganizationAsync(organizationId, skip, take, cancellationToken);

        public async Task<IReadOnlyList<AuditLogEntry>> ListChainByOrganizationAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            FullChainReads.Add(organizationId);
            return await _inner.ListChainByOrganizationAsync(organizationId, cancellationToken);
        }

        public async Task<IReadOnlyList<AuditLogEntry>> ListChainSegmentByOrganizationAsync(
            Guid organizationId,
            long? afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            var segment = await _inner
                .ListChainSegmentByOrganizationAsync(organizationId, afterSequence, limit, cancellationToken);
            SegmentReads.Add((afterSequence, limit, segment.Count));
            return segment;
        }
    }
}
