// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Skip-reason helper for the provider-gated integration tests (CORE-TST-011).
///
/// <para>
/// Some integration tests assert behaviour that only a real PostgreSQL provider (the
/// Npgsql <c>xmin</c> optimistic-concurrency token, the session-level advisory lock) or
/// a real Redis/Valkey backplane (cross-instance realtime fan-out) can exhibit. A
/// default local <c>dotnet test</c> runs on in-memory SQLite with neither configured
/// (<see cref="PostgresTestDatabase.IsConfigured"/>, <see cref="RedisBackplaneTestServer.IsConfigured"/>),
/// so those tests used to <b>early-return</b> — which a test runner reports as a silent
/// green PASS, hiding from a developer that the provider assertions never ran.
/// </para>
///
/// <para>
/// These helpers replace that early return with an explicit, dynamic skip: on a default
/// local run the gated test reports as <b>Skipped, with a reason</b> instead of Passed,
/// while on the CI <c>integration-postgres</c> leg (where the provider is configured) the
/// gate is a no-op and the test runs and asserts exactly as before. xunit v2 has no
/// <c>Assert.Skip</c> (an xunit v3 API), so the skip is raised through
/// <see cref="Skip.IfNot(bool, string)"/> from <c>Xunit.SkippableFact</c>; the calling
/// test must be marked <c>[SkippableFact]</c> for the skip to be reported.
/// </para>
/// </summary>
internal static class ProviderGate
{
    /// <summary>
    /// Skip reason reported when a test needs the real PostgreSQL provider but the run is
    /// the default in-memory SQLite one.
    /// </summary>
    public const string PostgresSkipReason =
        "Provider-gated (CORE-TST-011): needs the real PostgreSQL provider, which a default local " +
        "'dotnet test' (in-memory SQLite) does not supply. Set LIVECORE_TEST_DB_PROVIDER=Postgres and " +
        "LIVECORE_TEST_POSTGRES to run it locally; the CI integration-postgres leg runs and asserts it for real.";

    /// <summary>
    /// Skip reason reported when a test needs both the real PostgreSQL provider and a real
    /// Redis/Valkey backplane but the run has neither.
    /// </summary>
    public const string PostgresAndRedisBackplaneSkipReason =
        "Provider-gated (CORE-TST-011): needs the real PostgreSQL provider AND a Redis/Valkey backplane, which " +
        "a default local 'dotnet test' does not supply. Set LIVECORE_TEST_DB_PROVIDER=Postgres + " +
        "LIVECORE_TEST_POSTGRES and LIVECORE_TEST_REDIS to run it locally; the CI integration-postgres leg runs " +
        "and asserts it for real.";

    /// <summary>
    /// Skips the calling <c>[SkippableFact]</c> with <see cref="PostgresSkipReason"/> unless the
    /// suite is configured against a real PostgreSQL provider.
    /// </summary>
    public static void RequirePostgres() =>
        Skip.IfNot(PostgresTestDatabase.IsConfigured, PostgresSkipReason);

    /// <summary>
    /// Skips the calling <c>[SkippableFact]</c> with <see cref="PostgresAndRedisBackplaneSkipReason"/>
    /// unless the suite is configured against both a real PostgreSQL provider and a real
    /// Redis/Valkey backplane.
    /// </summary>
    public static void RequirePostgresAndRedisBackplane() =>
        Skip.IfNot(
            PostgresTestDatabase.IsConfigured && RedisBackplaneTestServer.IsConfigured,
            PostgresAndRedisBackplaneSkipReason);
}
