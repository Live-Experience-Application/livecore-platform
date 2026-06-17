using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Exports;
using LiveCore.Api.Persistence;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Retention;

/// <summary>
/// The data-retention sweep's application service (CORE-PRIV-003, the "Privacy and Data Lifecycle" epic, GDPR
/// Art.5(1)(e) "storage limitation"). On each run it expires and PURGES terminal/old personal-data-bearing
/// records across four configurable families — completed/expired sessions (and, by the schema cascade, their
/// append-only session events, recaps and session-scoped visibility rules), generated recaps, completed export
/// artifacts (the row and any object-storage blob) and closed/expired/revoked invitations (their plaintext
/// invited email). The scheduling host — the background worker — invokes this service on an interval
/// (docs/02_ARCHITECTURE.md: the worker owns "cleanup" and async jobs), exactly as it invokes the asset cleanup
/// and recap generation services; the service itself is host-agnostic and fully unit-testable.
///
/// <para>
/// REUSE, NOT A PARALLEL ENGINE (the story note). Each family's candidates come from its OWNING module's
/// repository (<see cref="ISessionRepository.ListTerminalForRetentionAsync"/>,
/// <see cref="IRecapRepository.ListExpiredForRetentionAsync"/>,
/// <see cref="IExportJobRepository.ListCompletedForRetentionAsync"/>,
/// <see cref="IWorkspaceInvitationRepository.ListTerminalForRetentionAsync"/>), the export blob is deleted
/// through the existing <see cref="IAssetStorage"/> S3 delete path, each purge is audited through the existing
/// <see cref="IAuditLogRepository"/>, and the audit-append-plus-delete commits atomically through the existing
/// <see cref="TransactionalUnitOfWork"/> (CORE-CONC-002) — the same pattern the data-subject erasure and tenant
/// deletion commands use.
/// </para>
///
/// <para>
/// EACH FAMILY IS INDEPENDENTLY GATED (the acceptance criterion: "disabling a window stops purging"). A family
/// whose <see cref="RetentionWindowOptions.Enabled"/> is <see langword="false"/> is skipped entirely — its
/// candidate list is never even read — so a disabled window does no work. The three families whose deletion
/// would be surprising (sessions, recaps, exports) are DISABLED by default; the invitation-email purge is enabled
/// (<see cref="DataRetentionOptions"/>).
/// </para>
///
/// <para>
/// AUDITED BY ID (the acceptance criterion). Every purge appends a tenant-scoped
/// <see cref="AuditAction.RecordRetentionPurged"/> fact recording the tenant, workspace and the purged record by
/// its generic kind name and surrogate id — never the record's content, the recap body, the export blob or the
/// invited email (threat T7). Because the audit reference is a recorded fact, not a foreign key, the audit row
/// SURVIVES the purge, so the tamper-evident audit chain still verifies after a sweep (the same posture that
/// makes erasure/deletion reconcilable with the immutable audit log).
/// </para>
///
/// <para>
/// IDEMPOTENT and CONCURRENCY-SAFE (the acceptance criterion). The candidate read takes no lock; the actual
/// purge RE-LOADS the record tenant-scoped INSIDE its transaction, so two overlapping sweeps (or worker replicas)
/// converge: the loser re-loads a row the winner already deleted, finds it gone and skips it WITHOUT writing an
/// audit fact. Should two purges still race to the same live row, the second's delete affects zero rows and
/// raises a <see cref="DbUpdateConcurrencyException"/>; the whole transaction (audit append included) rolls back,
/// so a purge never double-deletes, never double-audits and never errors the loop. A re-run after a crash simply
/// re-lists and re-purges whatever is still past its window. The change tracker is cleared before each record so
/// one record's rolled-back changes never leak into the next.
/// </para>
///
/// <para>
/// EXPORT OBJECT-FIRST-THEN-ROW (threat T4). For a completed export that records an object-storage artifact, the
/// sweep deletes the blob BEFORE it removes the row, so a purged export never leaves an orphaned object; if the
/// blob delete fails transiently (or storage is unconfigured, fail-closed) the row is KEPT for the next sweep to
/// retry. All logging is identifier-only (kind names, ids and counts), so a sweep is safe to log (threat T7).
/// </para>
/// </summary>
public sealed class DataRetentionSweepService
{
    private const string _sessionResourceType = nameof(Session);
    private const string _recapResourceType = nameof(Recap);
    private const string _exportResourceType = nameof(ExportJob);
    private const string _invitationResourceType = nameof(WorkspaceInvitation);

    private readonly LiveCoreDbContext _dbContext;
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly ISessionRepository _sessions;
    private readonly IRecapRepository _recaps;
    private readonly IExportJobRepository _exports;
    private readonly IWorkspaceInvitationRepository _invitations;
    private readonly IAuditLogRepository _audit;
    private readonly IAssetStorage _storage;
    private readonly DataRetentionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataRetentionSweepService> _logger;

    /// <summary>Creates the sweep service over the four module repositories, the audit log, the storage adapter, the unit of work and the policy.</summary>
    public DataRetentionSweepService(
        LiveCoreDbContext dbContext,
        TransactionalUnitOfWork unitOfWork,
        ISessionRepository sessions,
        IRecapRepository recaps,
        IExportJobRepository exports,
        IWorkspaceInvitationRepository invitations,
        IAuditLogRepository audit,
        IAssetStorage storage,
        DataRetentionOptions options,
        TimeProvider timeProvider,
        ILogger<DataRetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(recaps);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _sessions = sessions;
        _recaps = recaps;
        _exports = exports;
        _invitations = invitations;
        _audit = audit;
        _storage = storage;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one retention sweep across all four families and returns the run's count-only summary. Each enabled
    /// family lists its candidates (terminal/old records past its window) and purges each one atomically (audit +
    /// delete), idempotently and concurrency-safely; a disabled family is skipped entirely. The sweep is
    /// resilient: a per-record failure is logged and that record is left for the next sweep to retry, rather than
    /// aborting the run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A per-family summary of how many records were examined, purged and failed.</returns>
    public async Task<DataRetentionSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var sessions = await SweepSessionsAsync(now, cancellationToken).ConfigureAwait(false);
        var recaps = await SweepRecapsAsync(now, cancellationToken).ConfigureAwait(false);
        var exports = await SweepExportsAsync(now, cancellationToken).ConfigureAwait(false);
        var invitations = await SweepInvitationsAsync(now, cancellationToken).ConfigureAwait(false);

        return new DataRetentionSweepResult(sessions, recaps, exports, invitations);
    }

    private async Task<DataRetentionFamilyResult> SweepSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.Sessions.Enabled)
        {
            return DataRetentionFamilyResult.None;
        }

        var createdBefore = now - _options.Sessions.RetentionWindow;
        var candidates = await _sessions
            .ListTerminalForRetentionAsync(createdBefore, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var examined = 0;
        var purged = 0;
        var failed = 0;

        foreach (var session in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            var outcome = await PurgeOneAsync(
                _sessionResourceType,
                session.OrganizationId,
                session.WorkspaceId,
                session.Id,
                reload: ct => _sessions.FindByIdAsync(session.OrganizationId, session.WorkspaceId, session.Id, ct),
                audit: loaded => AuditLogEntry.ForRetentionPurge(loaded.OrganizationId, loaded.WorkspaceId, _sessionResourceType, loaded.Id, now),
                delete: _sessions.DeleteAsync,
                cancellationToken).ConfigureAwait(false);

            Tally(outcome, ref purged, ref failed);
        }

        return new DataRetentionFamilyResult(examined, purged, failed);
    }

    private async Task<DataRetentionFamilyResult> SweepRecapsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.Recaps.Enabled)
        {
            return DataRetentionFamilyResult.None;
        }

        var generatedBefore = now - _options.Recaps.RetentionWindow;
        var candidates = await _recaps
            .ListExpiredForRetentionAsync(generatedBefore, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var examined = 0;
        var purged = 0;
        var failed = 0;

        foreach (var recap in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            var outcome = await PurgeOneAsync(
                _recapResourceType,
                recap.OrganizationId,
                recap.WorkspaceId,
                recap.Id,
                reload: ct => _recaps.FindByIdAsync(recap.OrganizationId, recap.WorkspaceId, recap.Id, ct),
                audit: loaded => AuditLogEntry.ForRetentionPurge(loaded.OrganizationId, loaded.WorkspaceId, _recapResourceType, loaded.Id, now),
                delete: _recaps.DeleteAsync,
                cancellationToken).ConfigureAwait(false);

            Tally(outcome, ref purged, ref failed);
        }

        return new DataRetentionFamilyResult(examined, purged, failed);
    }

    private async Task<DataRetentionFamilyResult> SweepExportsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.Exports.Enabled)
        {
            return DataRetentionFamilyResult.None;
        }

        var createdBefore = now - _options.Exports.RetentionWindow;
        var candidates = await _exports
            .ListCompletedForRetentionAsync(createdBefore, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var examined = 0;
        var purged = 0;
        var failed = 0;

        foreach (var export in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            // OBJECT FIRST, THEN ROW (threat T4): delete the export's object-storage artifact (if any) BEFORE the
            // row, so a purged export never leaves an orphaned object. A transient storage failure (or unconfigured
            // storage, fail-closed) leaves the row for the next sweep to retry; deletion is idempotent, so a retry
            // after the object is already gone is a harmless no-op.
            if (export.HasArtifact && !await TryDeleteExportArtifactAsync(export, cancellationToken).ConfigureAwait(false))
            {
                failed++;
                continue;
            }

            var outcome = await PurgeOneAsync(
                _exportResourceType,
                export.OrganizationId,
                export.WorkspaceId,
                export.Id,
                reload: ct => _exports.FindByIdAsync(export.OrganizationId, export.WorkspaceId, export.Id, ct),
                audit: loaded => AuditLogEntry.ForRetentionPurge(loaded.OrganizationId, loaded.WorkspaceId, _exportResourceType, loaded.Id, now),
                delete: _exports.DeleteAsync,
                cancellationToken).ConfigureAwait(false);

            Tally(outcome, ref purged, ref failed);
        }

        return new DataRetentionFamilyResult(examined, purged, failed);
    }

    private async Task<DataRetentionFamilyResult> SweepInvitationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.Invitations.Enabled)
        {
            return DataRetentionFamilyResult.None;
        }

        var createdBefore = now - _options.Invitations.RetentionWindow;
        var candidates = await _invitations
            .ListTerminalForRetentionAsync(createdBefore, now, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        var examined = 0;
        var purged = 0;
        var failed = 0;

        foreach (var invitation in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            var outcome = await PurgeOneAsync(
                _invitationResourceType,
                invitation.OrganizationId,
                invitation.WorkspaceId,
                invitation.Id,
                reload: ct => _invitations.FindByIdAsync(invitation.OrganizationId, invitation.WorkspaceId, invitation.Id, ct),
                audit: loaded => AuditLogEntry.ForRetentionPurge(loaded.OrganizationId, loaded.WorkspaceId, _invitationResourceType, loaded.Id, now),
                delete: _invitations.DeleteAsync,
                cancellationToken).ConfigureAwait(false);

            Tally(outcome, ref purged, ref failed);
        }

        return new DataRetentionFamilyResult(examined, purged, failed);
    }

    /// <summary>
    /// Deletes a completed export's object-storage artifact through the <see cref="IAssetStorage"/> S3 delete path.
    /// Returns whether the blob is gone (or never existed); a transient failure or unconfigured storage returns
    /// <see langword="false"/> so the caller keeps the export for the next sweep (never deleting a row whose object
    /// it could not delete). The coordinates and any failure are logged by id only — never a means to reach the
    /// object (threats T4/T7).
    /// </summary>
    private async Task<bool> TryDeleteExportArtifactAsync(ExportJob export, CancellationToken cancellationToken)
    {
        try
        {
            await _storage
                .DeleteObjectAsync(export.ArtifactBucket!, export.ArtifactObjectKey!, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (AssetStorageNotConfiguredException)
        {
            _logger.LogWarning(
                "Object storage is not configured; keeping export {ExportJobId} so its artifact is not orphaned.",
                export.Id);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete the object-storage artifact for export {ExportJobId}; keeping it for retry.",
                export.Id);
            return false;
        }
    }

    /// <summary>
    /// Purges one record atomically: clears the change tracker, then in ONE transaction re-loads the record
    /// tenant-scoped, appends its retention-purge audit fact and deletes it (the cascade follows). Returns
    /// <see cref="PurgeOutcome.Purged"/> on success, <see cref="PurgeOutcome.Skipped"/> when the record was
    /// already gone (a concurrent purge), or <see cref="PurgeOutcome.Failed"/> when a step failed (the
    /// transaction rolled back, audit included, so nothing partial is left).
    /// </summary>
    private async Task<PurgeOutcome> PurgeOneAsync<TRecord>(
        string resourceType,
        Guid organizationId,
        Guid workspaceId,
        Guid recordId,
        Func<CancellationToken, Task<TRecord?>> reload,
        Func<TRecord, AuditLogEntry> audit,
        Func<TRecord, CancellationToken, Task> delete,
        CancellationToken cancellationToken)
        where TRecord : class
    {
        // Start each purge from a clean change tracker so a previous record's committed entities — or a previous
        // record's rolled-back-but-still-tracked mutations after a failure — never leak into this transaction.
        _dbContext.ChangeTracker.Clear();

        try
        {
            var purged = await _unitOfWork.ExecuteAsync(
                async transactionCancellationToken =>
                {
                    // Re-load tenant-scoped INSIDE the transaction (the surrogate id is never trusted alone; threat
                    // T5/T1). A concurrent sweep may have already purged it — then this is null and we skip without
                    // writing an audit fact (idempotent).
                    var record = await reload(transactionCancellationToken).ConfigureAwait(false);
                    if (record is null)
                    {
                        return false;
                    }

                    // AUDIT FIRST, then delete, in the SAME transaction: the purge is recorded as a fact or it does
                    // not happen. The audit reference is a recorded fact (not a foreign key), so it outlives the
                    // deleted record and the tenant's tamper-evident chain still verifies.
                    await _audit.AppendAsync(audit(record), transactionCancellationToken).ConfigureAwait(false);
                    await delete(record, transactionCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (purged)
            {
                _logger.LogInformation(
                    "Purged {ResourceType} {ResourceId} past its retention window (org {OrganizationId}, workspace {WorkspaceId}).",
                    resourceType,
                    recordId,
                    organizationId,
                    workspaceId);
                return PurgeOutcome.Purged;
            }

            return PurgeOutcome.Skipped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown during a purge is expected; let the sweep end. The transaction rolled back, so nothing
            // partial is left.
            throw;
        }
        catch (DbUpdateException exception)
        {
            // A concurrent purge won the race (DbUpdateConcurrencyException: zero rows affected) or a transient
            // persistence error occurred. The transaction rolled back — no audit fact and no delete — so the record
            // is untouched and the next sweep retries it. Identifier-only log (threat T7).
            _logger.LogWarning(
                exception,
                "Could not purge {ResourceType} {ResourceId}; it stays for the next sweep to retry.",
                resourceType,
                recordId);
            return PurgeOutcome.Failed;
        }
    }

    private static void Tally(PurgeOutcome outcome, ref int purged, ref int failed)
    {
        switch (outcome)
        {
            case PurgeOutcome.Purged:
                purged++;
                break;
            case PurgeOutcome.Failed:
                failed++;
                break;
            case PurgeOutcome.Skipped:
            default:
                // Examined but neither purged nor failed (a concurrent sweep already removed it).
                break;
        }
    }

    /// <summary>The outcome of attempting to purge one record.</summary>
    private enum PurgeOutcome
    {
        /// <summary>The record was purged (audit appended and record removed, atomically).</summary>
        Purged,

        /// <summary>The record was already gone when re-loaded (a concurrent sweep purged it); nothing was written.</summary>
        Skipped,

        /// <summary>A step failed and the whole transaction rolled back; the record is untouched and will be retried.</summary>
        Failed,
    }
}
