namespace LiveCore.Api;

/// <summary>
/// The documented, stable catalog of machine-readable error codes carried by every Core API
/// Problem Details response (CORE-DX-001, the "API Contract and Consumer Ergonomics" epic).
///
/// <para>
/// Every error the API returns is an RFC 7807 Problem Details body (docs/02_ARCHITECTURE.md
/// "Error format"; docs/08_API_CONTRACTS.md). Before this catalog those bodies carried only a
/// human <c>title</c>/<c>detail</c>, so a consumer could branch only on the prose — which is not
/// a contract and is free to change. Each response now additionally carries a stable
/// <c>code</c> extension member (<see cref="Member"/>) drawn from this catalog, so a vertical
/// app branches on the code, never on the human <c>detail</c> string. The same catalog is
/// published to consumers as the <c>ProblemCodes</c> enum in <c>@livecore/contracts</c>
/// (packages/contracts/src/problem-details.ts); a contract test asserts the two never drift.
/// </para>
///
/// <para>
/// The codes are stable identifiers, not status codes: several distinct conditions can share an
/// HTTP status yet need to be told apart. In particular the three structurally-different
/// <c>409 Conflict</c>s are each their own code — <see cref="QuotaExceeded"/> ("a server quota
/// would be exceeded, this may change as usage frees up"), <see cref="WorkspaceArchived"/> ("the
/// workspace is archived and read-only"), and <see cref="ConcurrencyConflict"/> ("the resource
/// changed underneath you, reload and retry") — so a consumer can react to each correctly
/// instead of pattern-matching the detail prose.
/// </para>
///
/// <para>
/// A code names only the generic class of problem; it never encodes a resource id, tenant,
/// principal or any internal state, so adding it leaks nothing (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The values are lower_snake_case ASCII and MUST remain
/// stable once published.
/// </para>
/// </summary>
internal static class ProblemCodes
{
    /// <summary>
    /// The Problem Details extension member that carries the stable code. RFC 7807 permits
    /// additional members and requires readers to tolerate them, so this is additive and
    /// backward compatible.
    /// </summary>
    public const string Member = "code";

    /// <summary>The request was malformed or failed input validation (HTTP 400).</summary>
    public const string ValidationError = "validation_error";

    /// <summary>Authentication is missing or invalid (HTTP 401).</summary>
    public const string AuthenticationRequired = "authentication_required";

    /// <summary>The caller is authenticated but not authorized for this action (HTTP 403).</summary>
    public const string PermissionDenied = "permission_denied";

    /// <summary>
    /// The resource was not found, or is intentionally hidden as a fail-closed denial (HTTP 404).
    /// A consumer must not infer existence from this code (threats T1/T5).
    /// </summary>
    public const string NotFound = "not_found";

    /// <summary>
    /// A generic state conflict: the command is not legal from the resource's current state
    /// (HTTP 409). The more specific 409 codes below are preferred where they apply.
    /// </summary>
    public const string Conflict = "conflict";

    /// <summary>
    /// A uniqueness constraint would be violated: a resource with the same key/slug already
    /// exists (HTTP 409).
    /// </summary>
    public const string DuplicateResource = "duplicate_resource";

    /// <summary>
    /// The command would exceed a server-enforced quota (HTTP 409). The limit, not the caller,
    /// is the reason; the outcome may change as usage frees up.
    /// </summary>
    public const string QuotaExceeded = "quota_exceeded";

    /// <summary>
    /// The target workspace is archived and therefore read-only, so the write is refused
    /// (HTTP 409; CORE-LIFE-009).
    /// </summary>
    public const string WorkspaceArchived = "workspace_archived";

    /// <summary>
    /// An optimistic-concurrency check failed: the resource changed underneath the caller, who
    /// should reload and retry (HTTP 409; CORE-CONC-001).
    /// </summary>
    public const string ConcurrencyConflict = "concurrency_conflict";

    /// <summary>The command was well-formed but is semantically invalid (HTTP 422).</summary>
    public const string UnprocessableEntity = "unprocessable_entity";

    /// <summary>The request body exceeds the accepted size cap (HTTP 413).</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>The request was rejected because a rate limit was exceeded (HTTP 429).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>
    /// An unexpected server error occurred (HTTP 500). Reserved for the global Problem Details
    /// exception handler; the body never leaks internal detail (threat T7).
    /// </summary>
    public const string InternalError = "internal_error";

    /// <summary>A required dependency (for example persistence) is not configured (HTTP 503).</summary>
    public const string ServiceUnavailable = "service_unavailable";

    /// <summary>
    /// The complete catalog, in a stable order. This is the authoritative server-side list the
    /// published <c>@livecore/contracts</c> <c>ProblemCodes</c> enum must match exactly
    /// (asserted by a contract test). Every value a Problem Details response carries in its
    /// <see cref="Member"/> member is one of these.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ValidationError,
        AuthenticationRequired,
        PermissionDenied,
        NotFound,
        Conflict,
        DuplicateResource,
        QuotaExceeded,
        WorkspaceArchived,
        ConcurrencyConflict,
        UnprocessableEntity,
        PayloadTooLarge,
        RateLimited,
        InternalError,
        ServiceUnavailable,
    };

    /// <summary>The catalog as a set, for O(1) membership checks.</summary>
    public static readonly IReadOnlySet<string> Set = All.ToHashSet(StringComparer.Ordinal);
}
