using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Recaps;

/// <summary>
/// The background recap generation job's application service (CORE-JOB-001, the recap-generation story of the
/// "Worker Background Jobs" epic). It produces a recap asynchronously for every session that NEEDS one — a
/// session that has ENDED but has no recap yet — on the worker's configurable cadence, idempotently and
/// tenant-scoped (the acceptance criterion: "Recaps are produced asynchronously by the worker for sessions
/// that need them, on a configurable cadence, idempotently"). The scheduling host — the background worker —
/// invokes this service on an interval (docs/02_ARCHITECTURE.md: the worker owns async jobs), exactly as it
/// invokes the asset cleanup service; the service itself is host-agnostic and fully unit-testable.
///
/// <para>
/// It reuses the existing Recap aggregate and its repository (the story note: "Reuse the Recap
/// aggregate/repository"): each recap is produced through <see cref="Recap.GenerateBySystem"/> — a
/// SYSTEM-produced recap with no user (docs/09_EVENT_CATALOG.md: <c>RecapGenerated</c> source "System/Host")
/// — and appended through <see cref="IRecapRepository.TryAppendSystemRecapAsync"/>. The eligible sessions come
/// from <see cref="IRecapEligibleSessionReader"/>, which is the first idempotency layer: it never returns a
/// session that already has a recap, so a recap is produced AT MOST ONCE per session across sequential sweeps.
/// </para>
///
/// <para>
/// MULTI-WORKER CORRECTNESS (CORE-RCP-001). The eligibility read is a NOT EXISTS read decoupled from the
/// append, so two overlapping sweeps — in the same process or across worker replicas, none of which has a
/// single-instance guard — can both observe the same session as eligible and both try to append. The
/// database is the authoritative second layer: a partial unique index <c>recaps(session_id) WHERE generated_by
/// IS NULL</c> permits only one system recap per session, so the losing append is rejected and reported as
/// <see cref="RecapAppendResult.AlreadyExists"/> — a benign no-op, counted as
/// <see cref="RecapGenerationResult.Deduplicated"/>, never a duplicate and never a failure. So at most one
/// system recap exists per session regardless of how many replicas or overlapping sweeps run.
/// </para>
///
/// <para>
/// TENANT SCOPING (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The sweep spans all tenants on purpose (it
/// is a system job, not a tenant actor), but each <see cref="RecapEligibleSession"/> carries its own
/// session's organization, workspace and id, and the recap is produced with exactly those, so a session in
/// one tenant only ever receives a recap attributed to that same tenant/workspace/session — never another's.
/// </para>
///
/// <para>
/// RESILIENCE. The sweep is per-session resilient: producing or persisting a recap for one session that fails
/// (for example a foreign-key violation because the session was deleted between the eligibility read and the
/// append) is logged and counted, and that session is left eligible so the next sweep retries it, rather than
/// aborting the whole run. All logging is identifier-only (session and recap ids and counts, never the
/// session title or the recap body), so a sweep is safe to log (threat T7).
/// </para>
/// </summary>
public sealed class RecapGenerationService
{
    private readonly IRecapEligibleSessionReader _eligibleSessions;
    private readonly IRecapRepository _recaps;
    private readonly RecapGenerationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecapGenerationService> _logger;

    /// <summary>Creates the generation service over the eligibility reader, the recap repository and the policy.</summary>
    public RecapGenerationService(
        IRecapEligibleSessionReader eligibleSessions,
        IRecapRepository recaps,
        RecapGenerationOptions options,
        TimeProvider timeProvider,
        ILogger<RecapGenerationService> logger)
    {
        ArgumentNullException.ThrowIfNull(eligibleSessions);
        ArgumentNullException.ThrowIfNull(recaps);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _eligibleSessions = eligibleSessions;
        _recaps = recaps;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one generation sweep: finds up to <see cref="RecapGenerationOptions.BatchSize"/> sessions that
    /// need a recap (ended, not yet recapped), produces a system recap for each and appends it idempotently,
    /// and returns the run's count-only summary. Idempotent across sequential sweeps (a session with a recap is
    /// never eligible) AND under concurrent sweeps/replicas (the partial unique index permits one system recap
    /// per session, so a losing race converges onto the existing recap, counted as deduplicated — CORE-RCP-001),
    /// and tenant-scoped (each recap carries its own session's tenant/workspace/session). The sweep is
    /// resilient: a per-session failure is logged and that session is left for the next sweep to retry, rather
    /// than aborting the run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many sessions were examined, recapped, deduplicated and failed.</returns>
    public async Task<RecapGenerationResult> GenerateDueRecapsAsync(CancellationToken cancellationToken)
    {
        var candidates = await _eligibleSessions
            .ListSessionsNeedingRecapAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return new RecapGenerationResult(Examined: 0, Generated: 0, Deduplicated: 0, Failed: 0);
        }

        // One produced timestamp for the whole sweep, taken from the injected clock so the behavior is
        // deterministic under test (the same TimeProvider the rest of the platform uses).
        var generatedAt = _timeProvider.GetUtcNow();

        var examined = 0;
        var generated = 0;
        var deduplicated = 0;
        var failed = 0;

        foreach (var session in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            try
            {
                // SYSTEM-produced recap (no user): the platform, not a host, generated it
                // (docs/09_EVENT_CATALOG.md RecapGenerated source "System/Host"). The summary is a generic,
                // product-neutral body composed from the session's own live-timeline facts only (AGENTS.md).
                var recap = Recap.GenerateBySystem(
                    session.OrganizationId,
                    session.WorkspaceId,
                    session.SessionId,
                    ComposeSummary(session),
                    generatedAt);

                // Idempotent append (CORE-RCP-001): the partial unique index recaps(session_id) WHERE
                // generated_by IS NULL guarantees at most one system recap per session, so a concurrent sweep
                // or another worker replica that already produced this session's recap makes this a no-op
                // (AlreadyExists) rather than a duplicate or an error.
                var outcome = await _recaps.TryAppendSystemRecapAsync(recap, cancellationToken).ConfigureAwait(false);

                if (outcome == RecapAppendResult.Appended)
                {
                    generated++;
                    _logger.LogInformation(
                        "Generated system recap {RecapId} for ended session {SessionId}.",
                        recap.Id,
                        session.SessionId);
                }
                else
                {
                    deduplicated++;
                    _logger.LogInformation(
                        "A system recap already existed for ended session {SessionId} (produced by a concurrent sweep or another worker replica); skipping to avoid a duplicate.",
                        session.SessionId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown during the sweep is expected; stop cleanly and let the loop end.
                throw;
            }
            catch (DbUpdateException exception)
            {
                // The session/workspace/tenant may have been removed between the eligibility read and the
                // append (a foreign-key violation), or a transient persistence error occurred. Keep the
                // session eligible (no recap was written) so the next sweep retries it, and move on. The
                // message carries only the session id, never the title or the recap body (threat T7).
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to persist a system recap for ended session {SessionId}; it stays eligible and will be retried on the next sweep.",
                    session.SessionId);
            }
        }

        return new RecapGenerationResult(Examined: examined, Generated: generated, Deduplicated: deduplicated, Failed: failed);
    }

    /// <summary>
    /// Composes the generic, product-neutral system recap body from a session's own live-timeline facts. It is
    /// deterministic (driven only by the eligible session's timestamps), never carries vertical domain language
    /// (AGENTS.md) and never echoes the session title or any free-form content (threat T7). A vertical produces
    /// richer recaps through its own synchronous host path; Core's automatic recap is a minimal generic
    /// continuation record.
    /// </summary>
    private static string ComposeSummary(RecapEligibleSession session)
    {
        var startedAt = session.StartedAt?.ToUniversalTime();
        var endedAt = session.EndedAt?.ToUniversalTime();

        var timeline = (startedAt, endedAt) switch
        {
            ({ } start, { } end) =>
                $"Live timeline ran from {Format(start)} to {Format(end)}.",
            (null, { } end) =>
                $"Session ended at {Format(end)}.",
            _ => "The session has concluded.",
        };

        return $"Automated recap for a concluded session. {timeline}";
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
