// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Tests for the shared Npgsql connection-resilience and per-command-timeout configuration (CORE-CONC-003,
/// CORE-RES-004, <see cref="LiveCoreNpgsqlOptions"/>). They prove the acceptance behaviours those stories require:
/// <list type="bullet">
///   <item>a <see cref="LiveCoreDbContext"/> configured for PostgreSQL through the shared options uses a
///   RETRYING execution strategy (rather than the default non-retrying one);</item>
///   <item>a SIMULATED transient failure is retried and then succeeds, so a routine database disruption does
///   not surface to the caller as an error;</item>
///   <item>the configured client-side <c>CommandTimeout</c> is applied (the default through <c>Configure</c>, an
///   explicit value through <see cref="LiveCoreNpgsqlOptions.UseLiveCoreNpgsql"/>), and the runtime registration
///   wires the server-side <c>statement_timeout</c> interceptor when a positive statement timeout is configured;</item>
///   <item>the bounded <c>Maximum Pool Size</c> is applied to the runtime connection when the connection string
///   sets none, and an explicit operator-supplied pool cap is honored unchanged (CORE-DEP-014).</item>
/// </list>
///
/// All run fully offline — building the model and the execution strategy never opens a connection — so they
/// need no database server (the same offline posture as the migration model-check tests). The retry behaviour
/// is exercised against the SAME execution strategy the API host, the worker jobs and the migrations factory
/// all build, because every one of those passes <c>LiveCoreNpgsqlOptions.Configure</c> to
/// <c>UseNpgsql</c> (the runtime hosts via <see cref="LiveCoreNpgsqlOptions.UseLiveCoreNpgsql"/>).
/// </summary>
public class LiveCoreNpgsqlOptionsTests
{
    private static LiveCoreDbContext CreateNpgsqlContext()
    {
        // A credential-free local connection string; no connection is ever opened (no secret in source).
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql("Host=localhost;Database=livecore-resilience-check", LiveCoreNpgsqlOptions.Configure)
            .Options;
        return new LiveCoreDbContext(options);
    }

    private static DbContextOptions<LiveCoreDbContext> BuildRuntimeOptions(LiveCorePersistenceOptions tuning)
    {
        // A credential-free local connection string; no connection is ever opened (no secret in source).
        var builder = new DbContextOptionsBuilder<LiveCoreDbContext>();
        builder.UseLiveCoreNpgsql("Host=localhost;Database=livecore-timeout-check", tuning);
        return builder.Options;
    }

    [Fact]
    public void Configured_db_context_uses_a_retrying_execution_strategy()
    {
        using var context = CreateNpgsqlContext();

        var strategy = context.Database.CreateExecutionStrategy();

        Assert.True(
            strategy.RetriesOnFailure,
            "The Npgsql DbContext must use a retrying execution strategy so transient failures are retried (CORE-CONC-003).");
    }

    [Fact]
    public async Task Retrying_execution_strategy_retries_a_transient_failure_then_succeeds()
    {
        using var context = CreateNpgsqlContext();
        var strategy = context.Database.CreateExecutionStrategy();

        var attempts = 0;
        var result = await strategy.ExecuteAsync(async () =>
        {
            attempts++;
            if (attempts == 1)
            {
                // A transient connection failure of the kind a failover/restart/brief partition produces:
                // Npgsql's transient-error detector treats an NpgsqlException wrapping a network I/O error as
                // transient, so the retrying strategy re-runs the operation rather than surfacing the failure.
                throw new NpgsqlException(
                    "simulated transient connection failure",
                    new IOException("connection reset by peer"));
            }

            await Task.Yield();
            return "succeeded";
        });

        // The first attempt failed transiently and the second succeeded — the failure never reached the caller.
        Assert.Equal(2, attempts);
        Assert.Equal("succeeded", result);
    }

    [Fact]
    public async Task Retrying_execution_strategy_does_not_retry_a_non_transient_failure()
    {
        // A non-transient failure (here a plain InvalidOperationException standing in for a constraint
        // violation or a logic bug) must NOT be retried — it fails immediately, exactly once, so resilience
        // never masks a real error.
        using var context = CreateNpgsqlContext();
        var strategy = context.Database.CreateExecutionStrategy();

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ExecuteAsync(async () =>
        {
            attempts++;
            await Task.Yield();
            throw new InvalidOperationException("not a transient failure");
        }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Configure_applies_the_default_command_timeout()
    {
        // The design-time/migrations factory and any caller of the bare Configure delegate get the DEFAULT
        // client-side command timeout, so a stuck command is bounded rather than running to the Npgsql default
        // (CORE-RES-004). GetCommandTimeout reports the value EF Core will apply to every command.
        using var context = CreateNpgsqlContext();

        Assert.Equal(
            (int)Math.Ceiling(LiveCorePersistenceOptions.DefaultCommandTimeout.TotalSeconds),
            context.Database.GetCommandTimeout());
    }

    [Fact]
    public void UseLiveCoreNpgsql_applies_the_configured_command_timeout_and_keeps_retry()
    {
        var tuning = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9));
        using var context = new LiveCoreDbContext(BuildRuntimeOptions(tuning));

        Assert.Equal(7, context.Database.GetCommandTimeout());

        // The retrying execution strategy CORE-CONC-003 turned on is preserved by the runtime registration.
        Assert.True(
            context.Database.CreateExecutionStrategy().RetriesOnFailure,
            "UseLiveCoreNpgsql must preserve the retrying execution strategy (CORE-CONC-003).");
    }

    [Fact]
    public void UseLiveCoreNpgsql_registers_the_statement_timeout_interceptor_with_the_configured_window()
    {
        var tuning = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9));
        var options = BuildRuntimeOptions(tuning);

        var interceptor = RegisteredInterceptors(options)
            .OfType<StatementTimeoutConnectionInterceptor>()
            .SingleOrDefault();

        Assert.NotNull(interceptor);
        Assert.Equal(TimeSpan.FromSeconds(9), interceptor!.StatementTimeout);
    }

    [Fact]
    public void UseLiveCoreNpgsql_omits_the_statement_timeout_interceptor_when_disabled()
    {
        // A zero statement timeout is an explicit operator choice to disable the server-side ceiling; the runtime
        // registration then wires no interceptor (the client CommandTimeout still bounds each command).
        var tuning = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(7), TimeSpan.Zero);
        var options = BuildRuntimeOptions(tuning);

        Assert.DoesNotContain(RegisteredInterceptors(options), i => i is StatementTimeoutConnectionInterceptor);
    }

    [Fact]
    public void WithBoundedMaxPoolSize_applies_the_bound_when_the_connection_string_omits_it()
    {
        // CORE-DEP-014: a connection string with no pool cap gets the bounded value applied, so the process can
        // never silently open the Npgsql default of 100 connections.
        var resolved = LiveCoreNpgsqlOptions.WithBoundedMaxPoolSize(
            "Host=localhost;Database=livecore-pool-check", maxPoolSize: 25);

        Assert.Equal(25, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Theory]
    [InlineData("Host=localhost;Database=db;Maximum Pool Size=40")]
    [InlineData("Host=localhost;Database=db;MaxPoolSize=40")]
    public void WithBoundedMaxPoolSize_honors_an_explicit_pool_size_in_the_connection_string(string connectionString)
    {
        // The resolved pool size HONORS the configured Maximum Pool Size: an explicit operator-supplied cap (either
        // recognised Npgsql synonym, the documented compose api/worker split) wins over the bounded default.
        var resolved = LiveCoreNpgsqlOptions.WithBoundedMaxPoolSize(connectionString, maxPoolSize: 25);

        Assert.Equal(40, new NpgsqlConnectionStringBuilder(resolved).MaxPoolSize);
    }

    [Fact]
    public void WithBoundedMaxPoolSize_rejects_a_non_positive_bound()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => LiveCoreNpgsqlOptions.WithBoundedMaxPoolSize("Host=localhost;Database=db", maxPoolSize: 0));

    [Fact]
    public void UseLiveCoreNpgsql_applies_the_configured_max_pool_size_to_the_context_connection()
    {
        // End-to-end (offline): a runtime context built from a connection string with no pool cap carries the
        // configured Maximum Pool Size, so the bound reaches every API and worker DbContext (CORE-DEP-014).
        var tuning = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9), maxPoolSize: 17);
        var builder = new DbContextOptionsBuilder<LiveCoreDbContext>();
        builder.UseLiveCoreNpgsql("Host=localhost;Database=livecore-pool-check", tuning);

        using var context = new LiveCoreDbContext(builder.Options);

        var resolved = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal(17, resolved.MaxPoolSize);
    }

    // The EF Core interceptors live on the (internal) core options extension; read them through the PUBLIC
    // Extensions enumeration and its public Interceptors property so the assertion does not depend on an internal
    // EF Core type name binding at compile time.
    private static IEnumerable<IInterceptor> RegisteredInterceptors(IDbContextOptions options)
    {
        foreach (var extension in options.Extensions)
        {
            var property = extension.GetType().GetProperty("Interceptors");
            if (property?.GetValue(extension) is IEnumerable<IInterceptor> interceptors)
            {
                foreach (var interceptor in interceptors)
                {
                    yield return interceptor;
                }
            }
        }
    }
}
