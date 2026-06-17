// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using LiveCore.Api.Retention;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Retention;

/// <summary>
/// Tests the persistence-gated registration of the data-retention sweep (CORE-PRIV-003). Like every worker job,
/// it is registered only when a database connection string is configured; with none it registers nothing and
/// returns <see langword="false"/>, so the worker runs (with no retention loop) rather than failing. When a
/// database IS configured, the sweep service and the collaborators it composes (the module repositories, the
/// audit log, the transactional unit of work) are wired so the worker can schedule the loop. Generic vocabulary
/// only (AGENTS.md).
/// </summary>
public class DataRetentionServiceCollectionExtensionsTests
{
    private const string _connectionString = "Host=localhost;Database=livecore;Username=app;Password=secret";

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value))
            .Build();

    [Fact]
    public void Not_registered_when_no_database_is_configured()
    {
        var services = new ServiceCollection();

        var registered = services.AddDataRetention(Configuration());

        Assert.False(registered);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DataRetentionSweepService));
    }

    [Fact]
    public void Registered_when_a_database_is_configured()
    {
        var services = new ServiceCollection();

        var registered = services.AddDataRetention(Configuration(("ConnectionStrings:Database", _connectionString)));

        Assert.True(registered);
        // The sweep service and the collaborators it composes are wired so the worker can schedule the loop.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(DataRetentionSweepService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(DataRetentionOptions));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TransactionalUnitOfWork));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISessionRepository));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuditLogRepository));
        // The idempotency-key store whose bulk purge bounds the idempotency_keys table (CORE-PRIV-006).
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IIdempotencyKeyStore));
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => DataRetentionServiceCollectionExtensions.AddDataRetention(null!, Configuration()));
        Assert.Throws<ArgumentNullException>(() => services.AddDataRetention(null!));
    }
}
