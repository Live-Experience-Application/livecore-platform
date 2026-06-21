// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Visibility;

/// <summary>
/// EF Core implementation of <see cref="IScheduledRevealDueReader"/> (CORE-VSEAL-002). It runs a single bounded
/// read over <c>visibility_rules</c> backed by the partial index
/// <c>ix_visibility_rules_scheduled_reveal_at</c> (the rows that actually carry a schedule), projecting only the
/// coordinates the sweep needs — never the resolved resource content (threat T7).
/// </summary>
internal sealed class ScheduledRevealDueReader : IScheduledRevealDueReader
{
    private readonly LiveCoreDbContext _dbContext;

    public ScheduledRevealDueReader(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DueScheduledReveal>> ListDueRulesAsync(
        DateTimeOffset asOf,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // DUE = HIDDEN, carries a scheduled_reveal_at AT OR BEFORE the sweep time, and NOT sealed. The
        // translatable predicates run in the DATABASE: a rule that is already Visible (including one this sweep
        // just auto-revealed) is excluded by the Hidden predicate — the primary at-most-once mechanism — a SEALED
        // rule is excluded because its visibility can never change (the reveal command would refuse it anyway;
        // defence in depth), and the `scheduled_reveal_at IS NOT NULL` predicate matches the partial index
        // ix_visibility_rules_scheduled_reveal_at, so the periodic sweep touches only the few scheduled rows. The
        // order is the time-ordered surrogate id (UUIDv7, provider-independent — SQLite cannot ORDER BY or COMPARE
        // a DateTimeOffset, the same constraint the retention/recap system reads document), so the oldest
        // scheduled rules sort first and the bounded batch (Take(maxCount)) surfaces them, with repeated sweeps
        // covering the rest (threat T9). The "<= asOf" due comparison is therefore applied AFTER materialization
        // (client-side), exactly the established pattern, so a not-yet-due (future) rule is never returned. The
        // projection reads ONLY the coordinates the sweep needs — never any resolved content (threat T7). This is
        // a deliberate CROSS-TENANT system read; tenant safety is preserved by the caller driving each
        // auto-reveal scoped to the rule's own tenant/workspace/session (threat T5).
        var scheduledCandidates = await _dbContext.VisibilityRules
            .AsNoTracking()
            .Where(rule => rule.Visibility == VisibilityState.Hidden
                && rule.ScheduledRevealAt != null
                && !rule.Locked)
            .OrderBy(rule => rule.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return scheduledCandidates
            .Where(rule => rule.ScheduledRevealAt <= asOf)
            .Select(rule => new DueScheduledReveal(
                rule.Id,
                rule.OrganizationId,
                rule.WorkspaceId,
                rule.SessionId,
                rule.ResourceType,
                rule.ResourceId,
                rule.TargetParticipantId))
            .ToList();
    }
}
