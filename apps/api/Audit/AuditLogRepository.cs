// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Audit;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogRepository"/> (CORE-VIS-006), backed by the
/// append-only <c>audit_logs</c> table mapped in <see cref="AuditLogConfiguration"/>. There is no
/// update or delete path: audit facts are immutable once written.
/// </summary>
internal sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public AuditLogRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // UNIT OF WORK (CORE-CONC-008). A chained append must hold the per-tenant audit_log_sequences row lock
        // from the allocation through the insert COMMIT. The lock is only held until its enclosing transaction
        // commits, so the allocation, the previous-hash read and the insert must all run inside ONE transaction;
        // otherwise a concurrent same-tenant append can allocate the next number, read a predecessor that has not
        // committed yet and seal off a FORKED chain — which the AuditLogChainVerifier (CORE-SEC-003) later reports
        // as tampering, a false positive that masks real detection.
        //
        // When a CORE-CONC-002 command already opened a transaction on this context — the reveal/hide, session,
        // deletion, retention and store handlers append from INSIDE their TransactionalUnitOfWork — this append
        // simply ENROLS in that ambient transaction, so its lock is already held until the command commits (the
        // behaviour before this story). When there is no ambient transaction — the previously direct appends:
        // organization/workspace member changes, the personal-data export, the quota-exceeded fact — it opens its
        // OWN unit of work so the same guarantee holds. Reusing the existing TransactionalUnitOfWork (CORE-CONC-002)
        // keeps the append correct under the retrying execution strategy (CORE-CONC-003) like every other write.
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await AppendCoreAsync(entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        await new TransactionalUnitOfWork(_dbContext)
            .ExecuteAsync(
                async transactionCancellationToken =>
                {
                    await AppendCoreAsync(entry, transactionCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the append itself on the (ambient or freshly opened) transaction set up by
    /// <see cref="AppendAsync"/>: a platform-level fact is inserted unsealed, while a tenant fact is sealed into
    /// the per-tenant hash chain and inserted. It never opens or commits a transaction of its own, so it is the
    /// single body run both when enrolling in a CORE-CONC-002 command's transaction and inside this repository's
    /// own unit of work (CORE-CONC-008).
    /// </summary>
    private async Task AppendCoreAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        // PLATFORM-LEVEL fact (CORE-SPEC-002): a deployment-spanning, not tenant-scoped audit fact (a purchase
        // grant/revocation, a purchase verification or a store notification, docs/21) carries no organization. The
        // per-tenant tamper-evident hash chain (CORE-SEC-003) is the per-TENANT append sequence, so a tenant-less
        // fact has no chain to join: it is appended unsealed (its append-only integrity is the same DB-level
        // REVOKE the established purchase_events monetization trail relies on, docs/13). It is never returned by the
        // tenant-scoped reads (which filter by a concrete organization id), so it never appears in a tenant's
        // verified chain.
        if (entry.OrganizationId is not { } organizationId)
        {
            _dbContext.AuditLogs.Add(entry);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // RETRY SAFETY (CORE-CONC-008): if a PRIOR attempt of this append already sealed the entry but its
        // transaction rolled back (a transient failure under the retrying strategy, CORE-CONC-003), discard that
        // stale linkage so this attempt re-seals with the freshly RE-allocated sequence and the predecessor
        // visible now. The first attempt's entry is unsealed, so this is a no-op then.
        if (entry.EntryHash is not null)
        {
            entry.ClearChainLinkageForReseal();
        }

        // TAMPER-EVIDENT CHAIN (CORE-SEC-003). Allocate the per-tenant, gap-free, strictly monotonic APPEND
        // sequence and seal the entry into the tenant's hash chain BEFORE the insert. The allocation takes a row
        // lock on the tenant's audit_log_sequences counter; because the whole append runs inside one transaction
        // (CORE-CONC-008) that lock is held until the insert commits, so two concurrent appends to the SAME tenant
        // serialize — the second blocks until the first commits and then chains off the just-committed entry rather
        // than forking the chain. The allocation and the insert run on the same context/transaction, so they commit
        // or roll back atomically: a rollback reclaims the number and the chain stays gap-free.
        var sequence = await AllocateNextSequenceAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var previousHash = await ReadPreviousHashAsync(organizationId, sequence, cancellationToken)
            .ConfigureAwait(false);
        entry.Seal(sequence, previousHash);

        _dbContext.AuditLogs.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocates the next per-tenant append sequence number from the <c>audit_log_sequences</c> counter
    /// (CORE-SEC-003). The increment is a SINGLE atomic <c>INSERT ... ON CONFLICT DO UPDATE</c>: it creates the
    /// counter at 1 on a tenant's first audit entry, or increments it by exactly one otherwise. The conflict-path
    /// UPDATE takes a row lock on the counter, so two concurrent appends to the SAME tenant serialize — the
    /// second blocks until the first commits and then increments from the committed value rather than colliding —
    /// yielding a gap-free, strictly monotonic sequence (and so a non-forking hash chain) even under a race. The
    /// just-allocated value is read back on the same context/transaction (it sees its own write). Mirrors the
    /// Realtime per-session sequence allocator (CORE-RTC-001).
    /// </summary>
    private async Task<long> AllocateNextSequenceAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        // The same provider-portable upsert pattern the per-session sequence and the atomic quota consume use
        // (works on PostgreSQL and on the SQLite test provider); every value is a parameter. organization_id is
        // the counter's primary key, so it is the conflict target.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO audit_log_sequences (organization_id, last_sequence)
               VALUES ({organizationId}, 1)
               ON CONFLICT (organization_id)
               DO UPDATE SET last_sequence = audit_log_sequences.last_sequence + 1",
            cancellationToken).ConfigureAwait(false);

        return await _dbContext.AuditLogSequences
            .AsNoTracking()
            .Where(counter => counter.OrganizationId == organizationId)
            .Select(counter => counter.LastSequence)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the chain hash of the entry that precedes the one being appended — the <c>entry_hash</c> of the
    /// tenant's entry at <paramref name="sequence"/> − 1 (CORE-SEC-003). Returns <see langword="null"/> for the
    /// genesis entry (sequence 1) and when the predecessor carries no hash (a legacy row written before this
    /// hardening), so the first chained entry after a legacy history correctly starts a fresh genesis. The
    /// allocator already serialized this append after the predecessor committed, so the predecessor is visible.
    /// </summary>
    private async Task<string?> ReadPreviousHashAsync(
        Guid organizationId,
        long sequence,
        CancellationToken cancellationToken)
    {
        if (sequence <= 1)
        {
            return null;
        }

        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId && entry.Sequence == sequence - 1)
            .Select(entry => entry.EntryHash)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored tenant's records, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        // The predicate matches organization_id (the leading column of the documented critical index
        // audit_logs(organization_id, created_at)), so the list is exactly tenant-scoped: another
        // tenant's records are never returned even when their ids would otherwise be addressable
        // (threat T5). The order is by the time-ordered surrogate id (UUIDv7), which is chronological
        // and — unlike ordering by the DateTimeOffset created_at — is supported by every provider
        // (SQLite cannot ORDER BY a DateTimeOffset); this matches the ordering convention of the other
        // repositories. The created_at column still backs future time-range audit queries (CORE-AUD-005).
        //
        // NON-TRACKED read (CORE-SEC-004): the audit log is TAMPER-PROOF in code, not only tamper-evident.
        // AsNoTracking returns detached entities, so an in-process caller can never load an audit row, mutate
        // it and have a later SaveChanges silently persist the change — there is nothing tracked to write back.
        // The AuditLogTamperProtectionInterceptor is the fail-closed backstop if a row is ever tracked anyway,
        // and the checked-in REVOKE migration is the DB-level prevention.
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListPageByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored tenant's records, so the lookup fails fast instead of
        // returning an arbitrary set of rows (mirrors ListByOrganizationAsync).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // Same tenant-scoped, chronologically ordered read as ListByOrganizationAsync — the predicate matches
        // organization_id (the leading column of the critical index audit_logs(organization_id, created_at)),
        // so a foreign tenant's records are never returned (threat T5) — but bounded by Skip/Take so an
        // unbounded log is never materialized. Ordering by the time-ordered surrogate id (UUIDv7) is
        // provider-independent (SQLite cannot ORDER BY a DateTimeOffset) and stable for an append-only log:
        // a new entry appends at the end (a higher id) and never shifts an already-read page. Skip/Take
        // translate to the provider's LIMIT/OFFSET. NON-TRACKED (CORE-SEC-004) like the other reads, so a read
        // entry can never be mutated and silently persisted back.
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLogEntry>> ListChainByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored tenant's records, so the lookup fails fast (mirrors the other
        // reads).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        // Tenant-scoped exactly like the other reads (predicate matches organization_id, threat T5), but ordered
        // by the per-tenant APPEND sequence (CORE-SEC-003) rather than the event-time-ordered id, because the
        // hash chain is linked in append order: a producer may record an action with an out-of-append-order
        // event time, so only the gap-free sequence walks the chain correctly. The unique
        // audit_logs(organization_id, sequence) index backs this ordering. NON-TRACKED (CORE-SEC-004): the
        // verifier only reads the chain, so detached entities are exactly right and a verification can never
        // accidentally write a row back.
        return await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
