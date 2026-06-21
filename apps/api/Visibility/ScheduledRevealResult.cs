// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The count-only summary of one scheduled-reveal sweep (CORE-VSEAL-002) — the result
/// <see cref="ScheduledRevealService.RevealDueRulesAsync"/> returns and the worker loop records as SLI signals.
/// Every field is a count, never a tenant/principal/resource identifier or any content (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md), mirroring <c>RecapGenerationResult</c>.
/// </summary>
/// <param name="Examined">How many due rules the sweep examined (the observed backlog of the sweep).</param>
/// <param name="Revealed">How many rules were ACTUALLY auto-revealed (a real Hidden -&gt; Visible change).</param>
/// <param name="AlreadyApplied">
/// How many were a no-op idempotent retry — the deterministic reveal key was already recorded (a concurrent sweep
/// or another worker replica already auto-revealed the rule), so nothing was re-revealed (the at-most-once
/// backstop).
/// </param>
/// <param name="Failed">
/// How many failed transiently (left due so the next sweep retries them) — the sweep is per-rule resilient.
/// </param>
public sealed record ScheduledRevealResult(int Examined, int Revealed, int AlreadyApplied, int Failed);
