// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// The per-tenant allocator counter that hands out the gap-free, strictly monotonic APPEND sequence numbers
/// stamped onto every <see cref="AuditLogEntry"/> to form the tenant's tamper-evident hash chain (CORE-SEC-003).
/// The Audit module owns the <c>audit_log_sequences</c> table — one row per organization holding the
/// <see cref="LastSequence"/> handed out so far — alongside the append-only <c>audit_logs</c> table it
/// sequences. It is the audit analogue of the Realtime <see cref="LiveCore.Api.Realtime.SessionEventSequence"/>
/// (CORE-RTC-001): the same proven allocator pattern, scoped to the tenant instead of the session.
///
/// WHY A COUNTER ROW (not <c>MAX(sequence)+1</c> nor the UUIDv7 id). The id is derived from the EVENT time and a
/// producer may record an action out of append order, so an id-ordered chain could not be linked
/// deterministically. Allocating the next number from a counter row the database increments atomically
/// (<see cref="AuditLogRepository.AppendAsync"/> issues a single <c>INSERT ... ON CONFLICT DO UPDATE</c>) takes
/// a row lock that SERIALIZES concurrent appends to the same tenant: a second concurrent append blocks on the
/// locked counter and then increments from the committed value rather than colliding, so the per-tenant sequence
/// is gap-free and strictly monotonic — and the hash chain therefore never forks — even under a race. Because
/// the increment runs in the command's unit-of-work transaction (CORE-CONC-002) together with the audit insert,
/// a rollback reclaims the number, so there is no gap.
///
/// The row is created lazily on a tenant's FIRST appended audit entry and is keyed by
/// <see cref="OrganizationId"/> (a foreign key into <c>organizations</c> that CASCADES on delete, mirroring the
/// audit log's tenant foreign key): when a tenant is torn down its counter goes with it. There is no CLR write
/// API: the counter is advanced only by the atomic upsert in the append path, never by mutating a tracked
/// instance, so it can never be set to an arbitrary value.
/// </summary>
internal sealed class AuditLogSequence
{
    /// <summary>Materialization constructor for the persistence layer (the row is created by the append-path upsert).</summary>
    private AuditLogSequence()
    {
    }

    /// <summary>The tenant whose audit chain this counter allocates for (the <c>organization_id</c> primary key and foreign key).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// The highest sequence number handed out for the tenant so far. The next appended entry takes
    /// <see cref="LastSequence"/> + 1; the first entry of a tenant takes 1.
    /// </summary>
    public long LastSequence { get; private set; }
}
