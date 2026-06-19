// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Verifies the provider-gating helper itself (CORE-TST-011). On a default local run
/// (in-memory SQLite, with neither a real PostgreSQL provider nor a Redis/Valkey backplane
/// configured) the gate raises an <see cref="Xunit.SkipException"/> carrying a reason — so a
/// <c>[SkippableFact]</c> that calls it reports as Skipped-with-reason instead of a silent
/// green PASS. On the CI <c>integration-postgres</c> leg, where the provider IS configured,
/// the gate is a no-op and the gated test runs and asserts as before. This is the regression
/// guard for the story: it fails if a gate ever stops skipping locally (the silent-pass that
/// CORE-TST-011 removes) or stops being a no-op when the provider is present.
/// </summary>
public sealed class ProviderGateTests
{
    [Fact]
    public void RequirePostgres_skips_with_a_reason_locally_and_is_a_no_op_when_postgres_is_configured()
    {
        if (PostgresTestDatabase.IsConfigured)
        {
            // CI integration-postgres leg: the gate must let the test run — no skip is raised.
            ProviderGate.RequirePostgres();
            return;
        }

        // Default local run: the gate skips with the documented reason instead of passing silently.
        var skip = Assert.Throws<Xunit.SkipException>(ProviderGate.RequirePostgres);
        Assert.Equal(ProviderGate.PostgresSkipReason, skip.Message);
    }

    [Fact]
    public void RequirePostgresAndRedisBackplane_skips_with_a_reason_unless_both_providers_are_configured()
    {
        if (PostgresTestDatabase.IsConfigured && RedisBackplaneTestServer.IsConfigured)
        {
            // CI integration-postgres leg (Postgres + Valkey): the gate lets the test run — no skip is raised.
            ProviderGate.RequirePostgresAndRedisBackplane();
            return;
        }

        // Either provider absent (the default local run, or a Postgres-only run): skip with the reason.
        var skip = Assert.Throws<Xunit.SkipException>(ProviderGate.RequirePostgresAndRedisBackplane);
        Assert.Equal(ProviderGate.PostgresAndRedisBackplaneSkipReason, skip.Message);
    }
}
