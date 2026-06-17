// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Recaps;

/// <summary>
/// System read for the background recap generation job (CORE-JOB-001): it returns the sessions that NEED a
/// recap — sessions that have ENDED but have no recap yet. It is the eligibility half of the job, separate
/// from the tenant-scoped <see cref="IRecapRepository"/> (which is a write-once append plus per-tenant reads),
/// because this is a SYSTEM sweep that deliberately spans all tenants and workspaces, exactly as the asset
/// cleanup job's <c>ListExpiredPendingAsync</c> is a cross-tenant system read. It is the idempotency mechanism
/// of the job: because a session that already has a recap is never returned, a recap is produced AT MOST ONCE
/// per session no matter how many sweeps run (the acceptance criterion "idempotently").
///
/// <para>
/// MODULE BOUNDARY. The eligibility decision needs both the Recaps module's own <c>recaps</c> table and the
/// Sessions module's <c>sessions</c> table (an ended session with no recap). It lives in the Recaps module
/// because the Recaps module already depends on the Sessions module (a recap has a foreign key into
/// <c>sessions</c>; see <see cref="RecapConfiguration"/>) — the dependency runs Recaps → Sessions, never the
/// reverse, so reading session rows to decide which need a recap is a Recaps-module read, not a Sessions-module
/// concern. It only READS session coordinates (it never writes the <c>sessions</c> table), so it is the
/// "explicit contract" the architecture wants for a cross-module read rather than a table-ownership violation
/// (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md). The actual <c>RecapGenerated</c> realtime event and
/// audit fact, and any recap HTTP route, remain later stories (this job only produces and persists the durable
/// recap; CORE-AUD-004 deferred the event/route the same way).
/// </para>
///
/// TENANT SAFETY. The sweep returns sessions across every tenant on purpose (it is a system job, not a tenant
/// actor), but every returned <see cref="RecapEligibleSession"/> carries its OWN session's organization,
/// workspace and id, so the recap the service produces is scoped to exactly that tenant/workspace/session and
/// can never be attributed to another tenant (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The read returns
/// only generic identifiers and timestamps — never the session title or any content (threat T7).
/// </summary>
public interface IRecapEligibleSessionReader
{
    /// <summary>
    /// Lists up to <paramref name="maxCount"/> sessions that need a recap — ENDED sessions that have no recap
    /// yet — in a deterministic order (by the time-ordered surrogate session id, UUIDv7, which is
    /// chronological and provider-independent), so the OLDEST ended-but-unrecapped sessions are produced
    /// first and repeated sweeps eventually cover them all. The list spans every tenant and workspace (a
    /// system sweep), but each entry carries its own tenant/workspace/session coordinates.
    /// </summary>
    /// <param name="maxCount">The maximum number of eligible sessions to return (the sweep batch size).</param>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <exception cref="System.ArgumentOutOfRangeException">The batch size is not positive.</exception>
    Task<IReadOnlyList<RecapEligibleSession>> ListSessionsNeedingRecapAsync(
        int maxCount,
        CancellationToken cancellationToken);
}
