using Microsoft.Extensions.Primitives;

namespace LiveCore.Api;

/// <summary>
/// The HTTP entity-tag surface of the optimistic-concurrency token (CORE-DX-002).
///
/// <para>
/// The mutable aggregates already carry a server-side optimistic-concurrency token
/// (the PostgreSQL <c>xmin</c> row version; see <see cref="Persistence.EntityConcurrencyToken"/>
/// and CORE-CONC-001/006). That token guards an in-request race at <c>SaveChanges</c>
/// (the <see cref="Persistence.ConcurrencyConflictMiddleware"/> turns the resulting
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> into a
/// <c>409</c>), but until CORE-DX-002 the token was never returned to a consumer and
/// no handler read it back, so a consumer doing GET-then-PUT across HTTP could only
/// ever see last-write-wins. This helper exposes the token as a WEAK
/// <see href="https://www.rfc-editor.org/rfc/rfc7232">RFC 7232</see> entity-tag on
/// reads and evaluates an <c>If-Match</c> precondition on mutations, so a consumer can
/// make a write conditional on the version they last read.
/// </para>
///
/// <para>
/// The tag is WEAK (the <c>W/</c> form) because <c>xmin</c> is a version stamp, not a
/// byte-for-byte hash of the representation. <see cref="EvaluateIfMatch"/> compares the
/// OPAQUE token value (the quoted body of the tag) and tolerates the optional weak
/// indicator and surrounding quotes, so a client may echo back either the exact
/// <c>W/"..."</c> header it received or the bare <c>version</c> field from the response
/// body — both resolve to the same token. The comparison leaks nothing: a precondition
/// failure names only the generic "the resource has changed" reason (threat T7).
/// </para>
/// </summary>
internal static class EntityTag
{
    /// <summary>The conditional-write request header a consumer sends to make a mutation version-conditional.</summary>
    public const string IfMatchHeader = "If-Match";

    /// <summary>The response header carrying the resource's current weak entity-tag.</summary>
    public const string ETagHeader = "ETag";

    /// <summary>
    /// Formats an opaque concurrency token as a WEAK entity-tag (<c>W/"&lt;token&gt;"</c>),
    /// the value of the <c>ETag</c> response header on a mutable resource.
    /// </summary>
    public static string Weak(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return $"W/\"{token}\"";
    }

    /// <summary>
    /// Evaluates the request's <c>If-Match</c> precondition against the resource's
    /// CURRENT concurrency token.
    ///
    /// <list type="bullet">
    ///   <item><see cref="IfMatchEvaluation.NotProvided"/> when the header is absent or
    ///   carries no usable entity-tag — the caller preserves its current
    ///   (unconditional) behavior, the optimistic-concurrency token still guards a
    ///   genuine in-request race at <c>SaveChanges</c>.</item>
    ///   <item><see cref="IfMatchEvaluation.Matched"/> when the header is the wildcard
    ///   <c>*</c> (any current representation) or any supplied entity-tag's opaque value
    ///   equals <paramref name="currentToken"/> — the precondition holds and the
    ///   mutation may proceed.</item>
    ///   <item><see cref="IfMatchEvaluation.Stale"/> when a tag was supplied but none
    ///   matched (including when <paramref name="currentToken"/> is <see langword="null"/>
    ///   because the provider maps no token, so the precondition cannot be confirmed) —
    ///   the caller returns <c>412 Precondition Failed</c>, fail-closed.</item>
    /// </list>
    /// </summary>
    public static IfMatchEvaluation EvaluateIfMatch(HttpRequest request, string? currentToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers[IfMatchHeader];
        if (StringValues.IsNullOrEmpty(header))
        {
            return IfMatchEvaluation.NotProvided;
        }

        // A header value may list several entity-tags (comma separated). A bare
        // "*" matches any current representation; otherwise any tag whose opaque
        // value equals the current token satisfies the precondition.
        var supplied = false;
        foreach (var candidate in header.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            supplied = true;

            if (candidate == "*")
            {
                return IfMatchEvaluation.Matched;
            }

            if (currentToken is not null
                && string.Equals(ExtractOpaqueValue(candidate), currentToken, StringComparison.Ordinal))
            {
                return IfMatchEvaluation.Matched;
            }
        }

        // A header that was present but held no usable entity-tag (for example only
        // commas) is treated as not provided rather than a spurious 412.
        return supplied ? IfMatchEvaluation.Stale : IfMatchEvaluation.NotProvided;
    }

    /// <summary>
    /// Reduces a single entity-tag to its opaque value: strips the optional weak
    /// indicator (<c>W/</c>) and the surrounding double quotes, so <c>W/"123"</c>,
    /// <c>"123"</c> and <c>123</c> all reduce to <c>123</c>.
    /// </summary>
    public static string ExtractOpaqueValue(string entityTag)
    {
        ArgumentNullException.ThrowIfNull(entityTag);

        var value = entityTag.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].Trim();
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value;
    }
}

/// <summary>The outcome of evaluating an <c>If-Match</c> precondition (CORE-DX-002).</summary>
internal enum IfMatchEvaluation
{
    /// <summary>No usable <c>If-Match</c> was sent; the mutation proceeds unconditionally (current behavior).</summary>
    NotProvided,

    /// <summary>The precondition holds (a matching tag or the <c>*</c> wildcard); the mutation may proceed.</summary>
    Matched,

    /// <summary>A tag was sent but did not match the current token; the mutation is refused with <c>412</c>.</summary>
    Stale,
}
