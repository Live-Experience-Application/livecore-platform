// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api;

/// <summary>
/// The single shared factory for every Core API RFC 7807 Problem Details response (CORE-DX-001).
///
/// <para>
/// All handlers, the concurrency-conflict middleware and the rate-limiter rejection funnel
/// through <see cref="Create"/> so that every error body carries a stable
/// <see cref="ProblemCodes.Member"><c>code</c></see> extension member drawn from the documented
/// <see cref="ProblemCodes"/> catalog. A consumer branches on that machine-readable code, never
/// on the human <c>title</c>/<c>detail</c> prose (which is free to change). The global Problem
/// Details exception handler (CORE-RES-001) reuses this same helper.
/// </para>
///
/// <para>
/// The helper is purely additive over <see cref="Results.Problem(string?,string?,int?,string?,string?,IDictionary{string,object?})"/>:
/// it sets the <c>statusCode</c>, <c>title</c> and <c>detail</c> exactly as the caller passes
/// them and attaches the <c>code</c> member. It does not interpolate or echo any caller-supplied
/// resource id, tenant or internal state — the caller owns a leak-free <c>detail</c>
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
internal static class CoreProblem
{
    /// <summary>
    /// Builds a Problem Details <see cref="IResult"/> for <paramref name="statusCode"/> carrying
    /// the stable <paramref name="code"/> from the <see cref="ProblemCodes"/> catalog.
    /// </summary>
    /// <param name="statusCode">The HTTP status code for this occurrence.</param>
    /// <param name="code">A stable code from <see cref="ProblemCodes.All"/>.</param>
    /// <param name="title">A short, human-readable summary of the problem type.</param>
    /// <param name="detail">
    /// A human-readable, leak-free explanation specific to this occurrence (threat T7).
    /// </param>
    public static IResult Create(int statusCode, string code, string title, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        if (!ProblemCodes.Set.Contains(code))
        {
            // Fail loud in development/tests if a handler invents a code outside the catalog;
            // every call site references a ProblemCodes constant, so this never trips in practice.
            throw new ArgumentException(
                $"'{code}' is not a code in the documented ProblemCodes catalog.", nameof(code));
        }

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ProblemCodes.Member] = code,
            });
    }
}
