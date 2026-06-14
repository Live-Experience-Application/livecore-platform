using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Translates an optimistic-concurrency failure into an HTTP <c>409 Conflict</c>
/// for every endpoint (CORE-CONC-001).
///
/// <para>
/// The mutable aggregates carry the PostgreSQL <c>xmin</c> row-version concurrency
/// token (see <see cref="LiveCoreDbContext"/>), so when two interleaved
/// read-modify-write commands race on one row, the SECOND <c>SaveChanges</c> throws
/// a <see cref="DbUpdateConcurrencyException"/> instead of silently overwriting the
/// first writer's update. That exception surfaces from the repository's
/// <c>UpdateAsync</c> while the endpoint handler is still running — before any
/// response has been written — so this middleware, wrapping the endpoint pipeline,
/// catches it and returns a fail-closed RFC 7807 <c>409</c> telling the caller to
/// reload and retry. The conflict is reported LOUDLY (a 409, never a lost update);
/// the alternative — last-write-wins — would silently discard a concurrent change.
/// </para>
///
/// <para>
/// Only <see cref="DbUpdateConcurrencyException"/> is handled here; every other
/// exception propagates unchanged so the host's existing error behavior is
/// untouched. The 409 detail names no resource, tenant or internal state — it
/// carries only the generic "modified by a concurrent request" reason (threat T7 in
/// <c>docs/07_SECURITY_THREAT_MODEL.md</c>). A 4xx is not counted as a server error
/// by the request metrics (only 5xx are), so a benign concurrency retry never
/// inflates the error-rate signal.
/// </para>
/// </summary>
internal sealed class ConcurrencyConflictMiddleware
{
    private readonly RequestDelegate _next;

    public ConcurrencyConflictMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The conflict is detected at SaveChanges, before the endpoint's result
            // is written, so the response has not started in practice. If it somehow
            // has, the status line is already on the wire and cannot be rewritten —
            // rethrow so the failure stays loud rather than being half-swallowed.
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            await Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: "The resource was modified by a concurrent request. Reload the resource and retry.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }
}
