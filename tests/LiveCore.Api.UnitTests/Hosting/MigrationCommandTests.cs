// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for the migration runner command (CORE-OPS-012, <see cref="MigrationCommand"/>): the
/// connection-string resolution (an explicit <c>--connection</c> wins, otherwise the
/// <c>ConnectionStrings:Database</c> environment variable, otherwise none) and the fail-closed exit code when no
/// database is configured. The locked apply itself — the session-level PostgreSQL advisory lock that serialises
/// concurrent runners — needs a real PostgreSQL database and is covered by the integration suite's
/// <c>MigrationAdvisoryLockTests</c> (the CI integration-postgres job).
/// </summary>
public sealed class MigrationCommandTests
{
    private const string _connectionEnvironmentVariable =
        LiveCoreDbContextFactory.ConnectionStringEnvironmentVariable;

    [Fact]
    public void ResolveConnectionString_prefers_the_explicit_connection_argument()
    {
        var resolved = MigrationCommand.ResolveConnectionString(
            ["migrate", "--connection", "Host=db;Database=livecore;Username=u;Password=p"]);

        Assert.Equal("Host=db;Database=livecore;Username=u;Password=p", resolved);
    }

    [Fact]
    public void ResolveConnectionString_explicit_argument_overrides_the_environment()
    {
        RunWithConnectionEnvironment("Host=from-env;Database=livecore;Username=u;Password=p", () =>
        {
            var resolved = MigrationCommand.ResolveConnectionString(
                ["migrate", "--connection", "Host=from-arg;Database=livecore;Username=u;Password=p"]);

            Assert.Equal("Host=from-arg;Database=livecore;Username=u;Password=p", resolved);
        });
    }

    [Fact]
    public void ResolveConnectionString_falls_back_to_the_environment_variable()
    {
        RunWithConnectionEnvironment("Host=from-env;Database=livecore;Username=u;Password=p", () =>
        {
            var resolved = MigrationCommand.ResolveConnectionString(["migrate"]);

            Assert.Equal("Host=from-env;Database=livecore;Username=u;Password=p", resolved);
        });
    }

    [Fact]
    public void ResolveConnectionString_returns_null_when_nothing_is_configured()
    {
        RunWithConnectionEnvironment(null, () =>
            Assert.Null(MigrationCommand.ResolveConnectionString(["migrate"])));
    }

    [Fact]
    public void ResolveConnectionString_treats_a_blank_explicit_argument_as_unset()
    {
        RunWithConnectionEnvironment(null, () =>
            Assert.Null(MigrationCommand.ResolveConnectionString(["migrate", "--connection", "   "])));
    }

    [Fact]
    public void Run_fails_closed_with_a_nonzero_exit_code_when_no_connection_is_configured()
    {
        RunWithConnectionEnvironment(null, () =>
        {
            // No environment connection string and no --connection argument: the runner must not connect to a
            // placeholder; it fails the migration step (non-zero) so a deploy gated on it aborts.
            var exitCode = MigrationCommand.Run(["migrate"]);

            Assert.NotEqual(0, exitCode);
        });
    }

    [Fact]
    public void Run_returns_nonzero_when_the_database_is_unreachable()
    {
        // A syntactically valid connection string pointed at a port nothing listens on: the apply cannot open a
        // connection, so the command reports failure rather than throwing — proving the failure is mapped to a
        // non-zero exit code (the deploy-gating contract) without needing a real database.
        var exitCode = MigrationCommand.Run(
            ["migrate", "--connection", "Host=127.0.0.1;Port=1;Database=livecore;Username=u;Password=p;Timeout=1;Command Timeout=1"]);

        Assert.NotEqual(0, exitCode);
    }

    private static void RunWithConnectionEnvironment(string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(_connectionEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(_connectionEnvironmentVariable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(_connectionEnvironmentVariable, original);
        }
    }
}
