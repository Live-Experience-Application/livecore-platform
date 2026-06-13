using LiveCore.Api.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Tests the FAIL-CLOSED registration gate of the store-notification reconciliation job (CORE-JOB-003) — the
/// security-relevant "only runs when billing is configured" guarantee. The reconciliation job re-derives premium
/// (entitlement) state, so it must NOT run on a deployment that has not configured store billing; it is registered
/// only when BOTH a database connection string is configured AND reconciliation is explicitly enabled
/// (<c>Store:Reconciliation:Enabled=true</c>). Every other configuration registers nothing and returns
/// <see langword="false"/>, so no reconciliation loop is scheduled. Generic vocabulary only (AGENTS.md).
/// </summary>
public class StoreNotificationReconciliationServiceCollectionExtensionsTests
{
    private const string _connectionString = "Host=localhost;Database=livecore;Username=app;Password=secret";

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value))
            .Build();

    [Fact]
    public void Not_registered_when_no_database_is_configured_even_if_enabled()
    {
        var services = new ServiceCollection();

        // Enabled, but no connection string: like every worker job, no persistence means no loop. Fail-safe.
        var registered = services.AddStoreNotificationReconciliation(
            Configuration(("Store:Reconciliation:Enabled", "true")));

        Assert.False(registered);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationReconciliationService));
    }

    [Fact]
    public void Not_registered_when_database_is_configured_but_reconciliation_is_not_enabled()
    {
        var services = new ServiceCollection();

        // Database configured, but billing/reconciliation not enabled (the default): the job stays OFF (fail-closed,
        // "only runs when billing is configured").
        var registered = services.AddStoreNotificationReconciliation(
            Configuration(("ConnectionStrings:Database", _connectionString)));

        Assert.False(registered);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationReconciliationService));
    }

    [Fact]
    public void Not_registered_when_explicitly_disabled()
    {
        var services = new ServiceCollection();

        var registered = services.AddStoreNotificationReconciliation(
            Configuration(
                ("ConnectionStrings:Database", _connectionString),
                ("Store:Reconciliation:Enabled", "false")));

        Assert.False(registered);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationReconciliationService));
    }

    [Fact]
    public void Registered_only_when_database_is_configured_and_reconciliation_is_enabled()
    {
        var services = new ServiceCollection();

        var registered = services.AddStoreNotificationReconciliation(
            Configuration(
                ("ConnectionStrings:Database", _connectionString),
                ("Store:Reconciliation:Enabled", "true")));

        Assert.True(registered);
        // The job's application service and its collaborators are wired so the worker can schedule the loop.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationReconciliationService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IReconcilablePurchaseReader));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(StoreNotificationReconciliationOptions));
    }

    [Fact]
    public void Rejects_null_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => StoreNotificationReconciliationServiceCollectionExtensions.AddStoreNotificationReconciliation(null!, Configuration()));
        Assert.Throws<ArgumentNullException>(
            () => services.AddStoreNotificationReconciliation(null!));
    }
}
