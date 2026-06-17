// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Hosting;

namespace LiveCore.Api.Observability;

/// <summary>
/// Resolves the single per-request correlation id (CORE-OBS-005) used BOTH as the <c>request_id</c> on every
/// log line (<see cref="RequestLogContextMiddleware"/>) AND as the <c>X-Request-Id</c> response header
/// (<see cref="CorrelationHeaderMiddleware"/>), so the value a caller reads off a failed response is exactly the
/// value its log lines carry — the correlation docs/15_OBSERVABILITY.md requires. It is resolved ONCE per
/// request and cached on <see cref="HttpContext.Items"/>, so the header and the log scope can never disagree
/// even though they are seeded by different middleware at different points in the pipeline.
///
/// <para>
/// The id is, in order: a well-formed inbound <c>X-Request-Id</c> the caller supplied
/// (docs/08_API_CONTRACTS.md documents it as an optional client-generated request header), else the current
/// distributed-trace id (so a log line correlates directly with the server's trace), else the framework's
/// per-request <see cref="HttpContext.TraceIdentifier"/>. Because the ASP.NET Core hosting layer adopts an
/// inbound W3C <c>traceparent</c> (CORE-OBS-005 auto-instrumentation), the trace id already reflects the
/// caller's trace when they propagate one, so the fallback id is correlatable end-to-end without trusting any
/// caller-supplied free text.
/// </para>
///
/// <para>
/// THREAT T7 / log forging. An inbound <c>X-Request-Id</c> is caller-controlled, so it is only honored when it
/// is SHORT and made of an unambiguous, log-safe character set (ASCII letters/digits and <c>-</c>/<c>.</c>/
/// <c>_</c>) — never whitespace, control or newline characters that could forge a second log line or smuggle
/// content. Anything else is ignored and the trusted trace id / framework id is used instead, so a correlation
/// token is always a non-sensitive identifier, never attacker-shaped content.
/// </para>
/// </summary>
internal static class RequestCorrelation
{
    /// <summary>The response/request header carrying the correlation id (the CORS-exposed <c>X-Request-Id</c>).</summary>
    public const string RequestIdHeaderName = CorsConfiguration.RequestIdHeaderName;

    /// <summary>The W3C Trace Context response header carrying the request's full trace context.</summary>
    public const string TraceParentHeaderName = CorsConfiguration.TraceParentHeaderName;

    /// <summary>The longest inbound <c>X-Request-Id</c> honored; longer values fall back to the trace id.</summary>
    private const int _maxInboundRequestIdLength = 128;

    private const string _itemsKey = "LiveCore.Observability.RequestCorrelationId";

    /// <summary>
    /// Returns the request's correlation id, computing it once and caching it on
    /// <see cref="HttpContext.Items"/> so every caller within the request sees the same value.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The non-empty correlation id for this request.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(_itemsKey, out var cached) && cached is string existing)
        {
            return existing;
        }

        var correlationId = Compute(context);
        context.Items[_itemsKey] = correlationId;
        return correlationId;
    }

    private static string Compute(HttpContext context)
    {
        // 1. A well-formed inbound client correlation id (docs/08_API_CONTRACTS.md "Common headers"), so a
        //    caller that generated its own id sees it echoed back and on the matching log lines.
        if (context.Request.Headers.TryGetValue(RequestIdHeaderName, out var inbound)
            && IsWellFormedInboundRequestId(inbound.ToString(), out var clientId))
        {
            return clientId;
        }

        // 2. The active distributed-trace id, so a log line correlates directly with the server's trace (and
        //    reflects an inbound traceparent the hosting layer already adopted).
        var activity = Activity.Current;
        if (activity is { IdFormat: ActivityIdFormat.W3C } && activity.TraceId != default)
        {
            return activity.TraceId.ToString();
        }

        // 3. The framework's per-request id (always present), so a request is never left uncorrelatable.
        return context.TraceIdentifier;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a short, log-safe caller-supplied correlation id (threat T7 / log
    /// forging). The trimmed candidate is returned through <paramref name="requestId"/> when accepted.
    /// </summary>
    private static bool IsWellFormedInboundRequestId(string value, out string requestId)
    {
        requestId = value.Trim();
        if (requestId.Length is 0 or > _maxInboundRequestIdLength)
        {
            return false;
        }

        foreach (var character in requestId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
