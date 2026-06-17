// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api;

/// <summary>
/// Shared bounded-pagination primitives for the Core list endpoints (CORE-DX-003, the "API Contract and
/// Consumer Ergonomics" epic). Every list endpoint that could otherwise materialize a whole table accepts an
/// optional <c>limit</c>/<c>offset</c> and returns a stable <see cref="PageResponse{T}"/> envelope, exactly
/// mirroring the audit-log read's <see cref="Audit.AuditLogPageResponse"/> shape and clamp: a request for more
/// than <see cref="MaxLimit"/> rows is clamped down, an absent <c>limit</c> uses <see cref="DefaultLimit"/>, and
/// a present-but-malformed value is rejected with a <c>400</c>. This is a consumability AND a DoS guard (threat
/// T9 in docs/07_SECURITY_THREAT_MODEL.md): no list can return an unbounded array, so a single read can never
/// materialize an arbitrarily large payload, layered under the per-principal rate limit (CORE-SEC-001).
///
/// The parsing rules match the audit read's rules byte-for-byte (a positive integer <c>limit</c> clamped to the
/// maximum; a non-negative integer <c>offset</c>), so the two paginated surfaces stay consistent. The bounds are
/// server-enforced and identical to <see cref="Audit.AuditLogPageResponse.DefaultLimit"/> /
/// <see cref="Audit.AuditLogPageResponse.MaxLimit"/>.
/// </summary>
public static class Page
{
    /// <summary>Query parameter carrying the optional page size.</summary>
    public const string LimitQuery = "limit";

    /// <summary>Query parameter carrying the optional zero-based page offset.</summary>
    public const string OffsetQuery = "offset";

    /// <summary>Default page size used when the request supplies no <c>limit</c> (matches the audit read).</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Hard server-side ceiling on the page size. A request for a larger <c>limit</c> is clamped to this, so a
    /// single read can never materialize an unbounded number of rows (a DoS/amplification guard layered under
    /// the per-principal rate limit, CORE-SEC-001). Matches the audit read's maximum.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// Resolves the requested page size: an absent value uses <see cref="DefaultLimit"/>; a present value must
    /// be a positive integer (a non-integer or a value below one is a 400) and is clamped down to
    /// <see cref="MaxLimit"/> so a single read can never materialize an unbounded number of rows. Identical to
    /// the audit read's rule.
    /// </summary>
    public static bool TryResolveLimit(string? raw, out int limit, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            limit = DefaultLimit;
            error = string.Empty;
            return true;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 1)
        {
            limit = 0;
            error = $"The '{LimitQuery}' value must be a positive integer.";
            return false;
        }

        limit = Math.Min(parsed, MaxLimit);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Resolves the requested zero-based page offset: an absent value is the first page (zero); a present value
    /// must be a non-negative integer (a non-integer or a negative value is a 400). Identical to the audit
    /// read's rule.
    /// </summary>
    public static bool TryResolveOffset(string? raw, out int offset, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            offset = 0;
            error = string.Empty;
            return true;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 0)
        {
            offset = 0;
            error = $"The '{OffsetQuery}' value must be a non-negative integer.";
            return false;
        }

        offset = parsed;
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Factory helpers for the generic <see cref="PageResponse{T}"/> envelope. A non-generic companion to the
/// generic record so a caller can build a page with type inference (<c>PageResponse.From(items, ...)</c>) and so
/// a role-projection projector can build the appropriately-typed page without naming the element type twice.
/// </summary>
public static class PageResponse
{
    /// <summary>
    /// Builds a <see cref="PageResponse{T}"/> from an already-bounded page of items. The caller has resolved the
    /// bounds (<paramref name="offset"/>, <paramref name="limit"/>) and computed <paramref name="hasMore"/> by
    /// over-fetching one extra row (the audit read's no-second-COUNT technique), then trimmed
    /// <paramref name="items"/> to at most <paramref name="limit"/> elements.
    /// </summary>
    public static PageResponse<T> From<T>(IReadOnlyList<T> items, int offset, int limit, bool hasMore)
        => new(offset, limit, hasMore, items);
}

/// <summary>
/// A stable, generic <c>items + hasMore</c> page envelope returned by every bounded Core list endpoint
/// (CORE-DX-003), modelled on the audit-log read's <see cref="Audit.AuditLogPageResponse"/>. It is the HTTP
/// shape a consumer pages through: at most <see cref="Limit"/> items starting at <see cref="Offset"/>, with
/// <see cref="HasMore"/> telling the client whether a further page exists (request the next page at
/// <c>offset = Offset + Items.Count</c>). The bounds are server-enforced: a request for more than
/// <see cref="Page.MaxLimit"/> is clamped, so a list can never return an unbounded array (threat T9).
/// </summary>
/// <typeparam name="T">The element shape (already role-projected by the endpoint where applicable).</typeparam>
/// <param name="Offset">The zero-based index of the first item in this page within the full result set.</param>
/// <param name="Limit">The effective (server-clamped) maximum number of items this page may contain.</param>
/// <param name="HasMore">Whether at least one further item exists after this page (request it at the next offset).</param>
/// <param name="Items">The page of items, in the endpoint's deterministic order; at most <see cref="Limit"/> of them.</param>
public sealed record PageResponse<T>(
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<T> Items);
