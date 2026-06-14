using StackExchange.Redis;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Gates and resolves the Redis/Valkey-compatible backplane the cross-instance propagation test runs against
/// (CORE-TST-003), mirroring how <see cref="PostgresTestDatabase"/> gates the real-PostgreSQL leg.
///
/// The documented production HA path makes the SignalR <c>HubLifetimeManager</c> Redis/Valkey-backed
/// (CORE-OPS-007, <c>RealtimeServiceCollectionExtensions.AddLiveCoreRealtime</c>) so a group send fans out to
/// connections on EVERY API instance. That path was config-tested only (the DI-wiring unit tests), never
/// exercised over real pub/sub. <see cref="RedisBackplanePropagationTests"/> exercises it for real, but only
/// when a backplane server is supplied out of band - the CI <c>integration-postgres</c> job's Redis/Valkey
/// service - so a default <c>dotnet test</c> needs no Redis server and the test is skipped.
///
/// No credentials live in the repository: the connection string is read only from the environment and points
/// at a throwaway CI service container (threat T7, <c>docs/07_SECURITY_THREAT_MODEL.md</c>). The values are
/// the generic Redis/Valkey connection vocabulary; nothing here carries vertical domain language (AGENTS.md).
/// </summary>
internal static class RedisBackplaneTestServer
{
    /// <summary>
    /// Environment variable carrying the StackExchange.Redis-format connection string of the backplane server
    /// (for example <c>localhost:6379</c>). Set by the CI job; absent for local runs, which skip the
    /// cross-instance propagation test.
    /// </summary>
    public const string ConnectionEnvironmentVariable = "LIVECORE_TEST_REDIS";

    /// <summary>
    /// <see langword="true"/> when a backplane connection string is configured; otherwise the propagation test
    /// is skipped (a default <c>dotnet test</c> needs no Redis/Valkey server).
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable));

    /// <summary>The configured backplane connection string. Throws when unconfigured (guard with <see cref="IsConfigured"/>).</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{ConnectionEnvironmentVariable} is not set; the Redis/Valkey backplane propagation test is not configured.");

    /// <summary>
    /// Verifies the configured server actually answers before the test boots the multiple API instances, so a
    /// misconfigured endpoint fails fast and clearly instead of timing out much later waiting for a delivery the
    /// background backplane silently never made. The SignalR Redis backplane connects lazily and would not fail
    /// host startup on an unreachable server.
    /// </summary>
    public static void EnsureReachable()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = true;
        options.ConnectTimeout = 5_000;
        options.ConnectRetry = 1;

        try
        {
            using var connection = ConnectionMultiplexer.Connect(options);
            connection.GetDatabase().Execute("PING");
        }
        catch (RedisConnectionException exception)
        {
            throw new InvalidOperationException(
                $"The Redis/Valkey backplane configured by {ConnectionEnvironmentVariable} is unreachable.",
                exception);
        }
    }
}
