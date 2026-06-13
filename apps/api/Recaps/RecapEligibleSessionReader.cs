using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Recaps;

/// <summary>
/// EF Core implementation of <see cref="IRecapEligibleSessionReader"/> (CORE-JOB-001). It runs a single
/// bounded anti-join: ENDED sessions for which no recap row exists, ordered by the time-ordered surrogate
/// session id. It reads from the <c>sessions</c> table (the Recaps module already depends on Sessions through
/// the recap's foreign key, so this is a read-only cross-module read, not a table-ownership violation — see
/// <see cref="IRecapEligibleSessionReader"/>) and correlates against its own <c>recaps</c> table.
/// </summary>
internal sealed class RecapEligibleSessionReader : IRecapEligibleSessionReader
{
    private readonly LiveCoreDbContext _dbContext;

    public RecapEligibleSessionReader(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecapEligibleSession>> ListSessionsNeedingRecapAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // Eligibility = ENDED and not yet recapped. The status predicate translates to a parameterized
        // equality on the string status column (Session.Status is persisted by name), so only concluded
        // sessions are considered — a Prepared, Live or Cancelled session never needs a recap. The correlated
        // NOT EXISTS against the recaps table is the IDEMPOTENCY mechanism: a session with any recap is
        // excluded, so a recap is produced at most once per session across any number of sweeps. The order is
        // the time-ordered surrogate session id (UUIDv7, chronological and provider-independent — SQLite
        // cannot ORDER BY a DateTimeOffset), so the oldest ended-but-unrecapped sessions sort first and the
        // bounded batch (Take(maxCount)) surfaces them, with repeated sweeps covering the rest. The projection
        // reads only the coordinates and live-timeline timestamps the generation service needs — never the
        // session title or any content (threat T7).
        return await _dbContext.Sessions
            .Where(session => session.Status == SessionStatus.Ended)
            .Where(session => !_dbContext.Recaps.Any(recap => recap.SessionId == session.Id))
            .OrderBy(session => session.Id)
            .Take(maxCount)
            .Select(session => new RecapEligibleSession(
                session.OrganizationId,
                session.WorkspaceId,
                session.Id,
                session.StartedAt,
                session.EndedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
