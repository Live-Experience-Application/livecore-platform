// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Recaps;

/// <summary>
/// Persistence contract for the recap aggregate (CORE-AUD-004). The Recaps module owns the <c>recaps</c>
/// table; other modules access recaps only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations; docs/05_MODULE_CONTRACTS.md: the Recaps
/// module owns "session recaps").
///
/// Every lookup is explicitly scoped by BOTH tenant boundaries: the caller passes the organization id and the
/// workspace id, and a recap is only ever returned when it belongs to exactly that (organization, workspace)
/// pair. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a recap is never returned through a foreign
/// organization's id even when the workspace and ids are correct, and never through a foreign workspace's id
/// even when the organization and ids are correct. There is deliberately no lookup of a recap by id alone and
/// no list-everything read method, so one workspace's recap can never be read through another workspace's id
/// and a recap in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// A recap is write-once (the produced output of a session), so there is an append and tenant-scoped reads but
/// no UPDATE path (mirrors the write-once export manifest). The one removal path is the data-retention PURGE
/// (<see cref="DeleteAsync"/>, CORE-PRIV-003): the recap body is host content (a session summary, potentially
/// personal data), so a configurable retention window may expire and purge it — exactly the controlled
/// exception the right-to-erasure made for the user profile (CORE-PRIV-001). There is still no in-place edit.
/// </summary>
public interface IRecapRepository
{
    /// <summary>
    /// Appends a new recap. A recap is write-once, so there is no update path; the surrogate id is generated
    /// non-empty by the aggregate, so an insert simply persists the row. Foreign-key violations (a
    /// non-existent session, workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    ///
    /// <para>
    /// This is the unconditional append used for a HOST-produced recap, of which a session may have more than
    /// one. A SYSTEM recap (no producing user) must instead go through <see cref="TryAppendSystemRecapAsync"/>,
    /// which enforces the at-most-one-system-recap-per-session rule (CORE-RCP-001).
    /// </para>
    /// </summary>
    Task AppendAsync(Recap recap, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a SYSTEM-produced recap idempotently (CORE-RCP-001): at most one system recap can exist per
    /// session, so a second concurrent sweep — from an overlapping run or another worker replica — produces no
    /// duplicate. The guarantee is the partial unique index <c>recaps(session_id) WHERE generated_by IS NULL</c>:
    /// when a system recap already exists for the session the insert is rejected and this returns
    /// <see cref="RecapAppendResult.AlreadyExists"/> (a no-op), otherwise the recap is written and this returns
    /// <see cref="RecapAppendResult.Appended"/>. The duplicate is NOT surfaced as a fault — only a genuine
    /// persistence error (for example the session/workspace/tenant was deleted between the eligibility read and
    /// the append, a foreign-key violation) still surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> so the sweep can leave the session eligible
    /// and retry it.
    /// </summary>
    /// <param name="recap">
    /// The system recap to append. It must be a system recap (<see cref="Recap.GeneratedByUserProfileId"/> is
    /// <see langword="null"/>); the at-most-one rule applies only to system recaps.
    /// </param>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <exception cref="System.ArgumentNullException">The recap is null.</exception>
    /// <exception cref="System.ArgumentException">The recap carries a producing user (it is not a system recap).</exception>
    Task<RecapAppendResult> TryAppendSystemRecapAsync(Recap recap, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the recap with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such recap exists there. The organization and workspace both scope the
    /// lookup, so a recap that exists under another organization's or workspace's id is never returned, even
    /// when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or recap id is empty. An empty id can never address a stored recap,
    /// so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<Recap?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the recaps produced for exactly the given session WITHIN the given organization and workspace, in
    /// produced order (the time-ordered surrogate id). A session may have produced more than one recap over
    /// time, so this returns all of them (possibly empty). The lookup is tenant- and workspace-scoped, so a
    /// session's recaps under another organization's or workspace's id are never returned even when the
    /// session id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or session id is empty.
    /// </exception>
    Task<IReadOnlyList<Recap>> ListBySessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists up to <paramref name="maxCount"/> recaps generated before <paramref name="generatedBefore"/> — the
    /// candidates for the data-retention sweep (CORE-PRIV-003, GDPR Art.5(1)(e) storage limitation). This is a
    /// SYSTEM maintenance read that deliberately spans ALL tenants and workspaces (the retention sweep is a system
    /// job, not a tenant actor). The retention window is measured from the recap's <c>generated_at</c> time, which
    /// is also the time the time-ordered surrogate id (UUIDv7) encodes; ordering is by that id, oldest first, and
    /// the generation-age threshold is applied after materialization (SQLite cannot ORDER BY or compare a
    /// DateTimeOffset), so the bounded batch surfaces the oldest recaps and over repeated sweeps every past-window
    /// recap is covered without starvation. The caller (the retention sweep) then re-loads and purges each within
    /// its own transaction.
    /// </summary>
    /// <param name="generatedBefore">The exclusive generation-time threshold; only recaps generated before it are returned.</param>
    /// <param name="maxCount">The maximum number of recaps a single sweep examines (a positive batch size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    Task<IReadOnlyList<Recap>> ListExpiredForRetentionAsync(
        DateTimeOffset generatedBefore,
        int maxCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES a recap row as part of the data-retention sweep (CORE-PRIV-003) — the one removal path on the
    /// otherwise write-once recap. The recap body is host content that a configurable retention window may expire;
    /// the <c>audit_logs</c> references the recap as a recorded fact, not a foreign key, so the audit trail
    /// SURVIVES the purge. The caller must have re-loaded the recap through a tenant-scoped lookup inside the purge
    /// transaction; a concurrent purge of the same row surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> which the sweep treats as
    /// already-handled (concurrency-safe).
    /// </summary>
    Task DeleteAsync(Recap recap, CancellationToken cancellationToken);
}
