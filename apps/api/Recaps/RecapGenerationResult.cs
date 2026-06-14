namespace LiveCore.Api.Recaps;

/// <summary>
/// The outcome of one run of the background recap generation sweep (CORE-JOB-001). It records counts only —
/// never any session title or recap content — so the worker can log a sweep summary safely (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Examined">How many eligible (ended, not-yet-recapped) sessions the sweep looked at this run.</param>
/// <param name="Generated">
/// How many recaps were produced and persisted this run — one per examined session that did not fail. Because
/// the eligibility read excludes any session that already has a recap, a session is recapped at most once
/// across all sweeps (idempotent).
/// </param>
/// <param name="Deduplicated">
/// How many examined sessions already had a SYSTEM recap by the time this sweep tried to append one, so its
/// insert was a no-op (CORE-RCP-001). This is the concurrent-worker race outcome: an overlapping sweep or
/// another worker replica produced the recap first, the partial unique index <c>recaps(session_id) WHERE
/// generated_by IS NULL</c> rejected the duplicate, and the session converges onto the one recap. It is a
/// benign outcome, never a failure — counted separately only for observability.
/// </param>
/// <param name="Failed">
/// How many eligible sessions could not be recapped this run because persisting the recap failed (for example
/// the session was deleted between the eligibility read and the append). Those sessions stay eligible — no
/// recap was written — so the next sweep retries them; nothing is left in an inconsistent state.
/// </param>
public readonly record struct RecapGenerationResult(int Examined, int Generated, int Deduplicated, int Failed);
