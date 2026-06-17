// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="StatementTimeoutConnectionInterceptor"/> (CORE-RES-004) — the interceptor that applies
/// the server-side PostgreSQL <c>statement_timeout</c> on every connection open so a stuck query is bounded at the
/// database. These run fully offline (constructing the interceptor opens no connection): they pin that it carries
/// the configured window and rejects a non-positive one (a disabled timeout is handled by NOT registering the
/// interceptor, never by constructing one with a zero/negative window). The end-to-end "a stuck query is actually
/// aborted" behaviour is proven against real PostgreSQL by the integration suite. Product-neutral vocabulary only
/// (AGENTS.md).
/// </summary>
public class StatementTimeoutConnectionInterceptorTests
{
    [Fact]
    public void Constructor_keeps_the_configured_statement_timeout()
    {
        var interceptor = new StatementTimeoutConnectionInterceptor(TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), interceptor.StatementTimeout);
    }

    [Fact]
    public void Constructor_rejects_a_zero_statement_timeout()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new StatementTimeoutConnectionInterceptor(TimeSpan.Zero));

    [Fact]
    public void Constructor_rejects_a_negative_statement_timeout()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new StatementTimeoutConnectionInterceptor(TimeSpan.FromSeconds(-1)));
}
