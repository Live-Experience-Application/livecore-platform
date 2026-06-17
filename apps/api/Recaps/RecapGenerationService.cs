// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json;
using LiveCore.Api.Realtime;
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
/// RECAP CONTENT FROM THE EVENT STREAM (CORE-RCP-002). Each recap REFLECTS WHAT ACTUALLY HAPPENED in the
/// session — it is composed from the session's own append-only event stream (read through the existing
/// <see cref="ISessionEventRepository"/>), not just the start/end timestamps. The composition
/// (<see cref="RecapSummaryComposer"/>) is deterministic, safe on empty/partial streams and visibility-safe: it
/// reads only each event's generic type name, so the host-only body (gated from the audience by
/// <see cref="RecapProjection"/>, which fails closed) carries aggregate counts and the UTC timeline but no
/// resolved content (threats T2/T7).
/// </para>
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
/// EMITS RecapGenerated (CORE-EVT-004). When a recap is FRESHLY produced (not on a deduplicated race), the
/// job appends the durable, HOST-ONLY <see cref="SessionEventTypes.RecapGenerated"/> session event to that
/// session's own append-only stream — a SYSTEM event (no actor), with an identifier-only payload (the recap
/// and session ids, never the recap body; threat T7) — so the catalog event is real and a host learns on
/// reconnect/replay that a recap exists, while the audience never does until a separate reveal. The recap is
/// the durable source of truth and is committed FIRST (its NOT-EXISTS-then-append dedup, CORE-RCP-001, cannot
/// share an enclosing transaction without the partial-unique-index race aborting it), then the event is
/// appended in its own durable transaction; a failure to append the event leaves the recap intact and is
/// logged identifier-only rather than re-counting the recap as failed. The append routes itself: the
/// recipient resolver delivers a host-only event to the session hosts only (it never widens to the audience),
/// so no audience-widening can occur here.
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
    private readonly ISessionEventRepository _sessionEvents;
    private readonly RecapGenerationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecapGenerationService> _logger;

    /// <summary>
    /// Creates the generation service over the eligibility reader, the recap repository, the session event
    /// stream (CORE-RCP-002, the source the recap body is composed from), the policy and the clock.
    /// </summary>
    public RecapGenerationService(
        IRecapEligibleSessionReader eligibleSessions,
        IRecapRepository recaps,
        ISessionEventRepository sessionEvents,
        RecapGenerationOptions options,
        TimeProvider timeProvider,
        ILogger<RecapGenerationService> logger)
    {
        ArgumentNullException.ThrowIfNull(eligibleSessions);
        ArgumentNullException.ThrowIfNull(recaps);
        ArgumentNullException.ThrowIfNull(sessionEvents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _eligibleSessions = eligibleSessions;
        _recaps = recaps;
        _sessionEvents = sessionEvents;
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
                // Read the session's own append-only event stream so the recap REFLECTS WHAT ACTUALLY HAPPENED
                // (CORE-RCP-002), not just the start/end timestamps. The read is tenant- AND session-scoped (it
                // leads with the session's own organization id; threat T5) and returns the stream in append
                // order; RecapSummaryComposer reads only each event's generic type name to compose a
                // deterministic, content-free body (threat T7).
                var stream = await _sessionEvents
                    .ListBySessionAsync(session.OrganizationId, session.SessionId, cancellationToken)
                    .ConfigureAwait(false);

                // SYSTEM-produced recap (no user): the platform, not a host, generated it
                // (docs/09_EVENT_CATALOG.md RecapGenerated source "System/Host"). The summary is a generic,
                // product-neutral body composed from the session's live timeline and its event stream (AGENTS.md).
                var recap = Recap.GenerateBySystem(
                    session.OrganizationId,
                    session.WorkspaceId,
                    session.SessionId,
                    RecapSummaryComposer.Compose(session, stream),
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

                    // CORE-EVT-004: emit the durable, HOST-ONLY RecapGenerated session event for the recap just
                    // produced. Only on a FRESH recap (never a deduplicated race), so a session gets exactly one
                    // RecapGenerated event, matching its one system recap.
                    await EmitRecapGeneratedEventAsync(recap, generatedAt, cancellationToken).ConfigureAwait(false);
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
    /// Appends the durable, HOST-ONLY <see cref="SessionEventTypes.RecapGenerated"/> session event for a
    /// freshly produced recap (CORE-EVT-004). The event is a SYSTEM event (no actor — the platform produced
    /// it, docs/09_EVENT_CATALOG.md <c>RecapGenerated</c> source "System/Host"), carries no visibility subject
    /// and no selected participant, and its payload is identifier-only (the recap and session ids, never the
    /// recap body; threat T7). Because <see cref="SessionEventTypes.IsHostOnly"/> classifies it host-only, the
    /// recipient resolver delivers it to the session hosts only — the audience never receives it until a host
    /// reveals the recap (threats T2/T7) — so this append can never widen an event's audience.
    /// <para>
    /// The recap is already committed (the source of truth); this event is appended in its OWN transaction
    /// because the recap's NOT-EXISTS-then-append dedup (CORE-RCP-001) cannot share an enclosing transaction
    /// without the partial-unique-index race aborting it. A failure here leaves the recap intact, so it is
    /// logged identifier-only and swallowed rather than re-counting the already-produced recap as a failure or
    /// crashing the sweep; host shutdown (cancellation) still propagates.
    /// </para>
    /// </summary>
    private async Task EmitRecapGeneratedEventAsync(Recap recap, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new SessionEventPayloads.RecapGeneratedEventPayload(recap.Id, recap.SessionId));

            var sessionEvent = SessionEvent.Create(
                recap.OrganizationId,
                recap.WorkspaceId,
                recap.SessionId,
                SessionEventTypes.RecapGenerated,
                createdBy: null,
                targetParticipantId: null,
                payload,
                schemaVersion: 1,
                generatedAt);

            await _sessionEvents.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown during the append is expected; let it propagate so the sweep stops cleanly.
            throw;
        }
        catch (Exception exception)
        {
            // The recap is durable and already committed, so a failed event append degrades only the host-only
            // RecapGenerated notification, not the recap itself; log identifiers/counts only (threat T7) and
            // continue the sweep rather than marking the produced recap as failed.
            _logger.LogWarning(
                exception,
                "Generated system recap {RecapId} for session {SessionId} but failed to append its RecapGenerated event; the recap is durable and unaffected.",
                recap.Id,
                recap.SessionId);
        }
    }
}
