// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.SystemModule;

/// <summary>
/// Reads the client <c>Idempotency-Key</c> request header (docs/08_API_CONTRACTS.md) for the routes that
/// honor it OPTIONALLY (CORE-DX-004: the create and purchase-verification routes). Unlike the reveal/hide
/// commands — where the header is REQUIRED, so an absent header is a 400 — these routes accept the header when
/// present and behave exactly as before when it is absent, so a non-breaking <c>Absent</c> outcome is distinct
/// from a malformed <c>Invalid</c> one.
///
/// The reader trims the value and validates it against the same bounds the stored key enforces
/// (<see cref="IdempotencyKey.IsValidKey"/>: non-blank, within the length bound, free of control characters),
/// so an oversize or control-character key is rejected as <see cref="IdempotencyKeyHeaderResult.Invalid"/> (the
/// endpoint maps it to a 400) rather than reaching the store and throwing. The key value is a client-chosen
/// correlation token, never sensitive content (threat T7).
/// </summary>
internal static class IdempotencyKeyHeader
{
    /// <summary>The request header carrying the client idempotency key (docs/08_API_CONTRACTS.md).</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Reads the optional <c>Idempotency-Key</c> header. Returns <see cref="IdempotencyKeyHeaderResult.Absent"/>
    /// (with a null <paramref name="key"/>) when no value is supplied, <see cref="IdempotencyKeyHeaderResult.Valid"/>
    /// with the trimmed key when a well-formed value is supplied, and <see cref="IdempotencyKeyHeaderResult.Invalid"/>
    /// when a value is present but violates the key invariants.
    /// </summary>
    public static IdempotencyKeyHeaderResult Read(HttpContext httpContext, out string? key)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        key = null;

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return IdempotencyKeyHeaderResult.Absent;
        }

        var value = values.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            // A blank header is treated as not supplied: the route behaves as it did before the header existed.
            return IdempotencyKeyHeaderResult.Absent;
        }

        var trimmed = value.Trim();
        if (!IdempotencyKey.IsValidKey(trimmed))
        {
            return IdempotencyKeyHeaderResult.Invalid;
        }

        key = trimmed;
        return IdempotencyKeyHeaderResult.Valid;
    }
}

/// <summary>The outcome of reading the optional <c>Idempotency-Key</c> header (CORE-DX-004).</summary>
internal enum IdempotencyKeyHeaderResult
{
    /// <summary>No (or blank) header was supplied; the route runs unchanged (no idempotency).</summary>
    Absent = 1,

    /// <summary>A well-formed key was supplied; the route honors it for retry-safe replay.</summary>
    Valid = 2,

    /// <summary>A header was supplied but violates the key invariants; the endpoint returns 400.</summary>
    Invalid = 3,
}
