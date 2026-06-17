using System.Diagnostics;
using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Proves the CORE-RES-005 acceptance behaviour for storage that needs a REAL outbound attempt: a storage
/// operation against an UNREACHABLE backend FAILS FAST at the configured timeout rather than stalling. The
/// production wiring (<see cref="AssetStorageServiceCollectionExtensions.AddAssetStorage"/>) builds the SDK S3
/// client and applies the configured per-request <c>Timeout</c> and bounded retry count/mode; this test wires it
/// with a SHORT timeout and no retries, points it at a non-routable endpoint (RFC 5737 TEST-NET-1, guaranteed not
/// routable) and asserts the real server-side <c>DeleteObjectAsync</c> aborts far below the AWS SDK's 100-second
/// default — so a hung object-storage dependency can never hold the calling (worker cleanup) thread indefinitely.
///
/// <para>
/// The test is robust whether or not the test host has network egress: with egress the connection to the
/// black-hole address is aborted by the client <c>Timeout</c>; without it the connect fails immediately with
/// "network unreachable". Both are "fails fast" — the assertion only requires the call to throw well within the
/// bound, never to stall. No credential lives in source: the access keys are throwaway fakes and the endpoint is
/// a reserved documentation address (threat T7).
/// </para>
/// </summary>
public sealed class OutboundStorageTimeoutTests
{
    [Fact]
    public async Task A_storage_delete_against_an_unreachable_backend_fails_fast_at_the_configured_timeout()
    {
        // Wire the storage adapter the production way, with a SHORT request timeout, no retries and an
        // unreachable endpoint (192.0.2.1 is RFC 5737 TEST-NET-1, guaranteed non-routable).
        var services = new ServiceCollection();
        services.AddAssetStorage(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Storage:Endpoint"] = "http://192.0.2.1:9000",
                ["Assets:Storage:AccessKeyId"] = "test-access-key",
                ["Assets:Storage:SecretAccessKey"] = "test-secret-key",
                ["Assets:Storage:RequestTimeout"] = "00:00:03",
                ["Assets:Storage:MaxErrorRetry"] = "0",
            })
            .Build());

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IAssetStorage>();

        var stopwatch = Stopwatch.StartNew();

        // The real server-side delete must throw rather than hang; the only thing under test is that it returns
        // control promptly. Minting a pre-signed URL would not exercise this (it is local, no round-trip).
        await Assert.ThrowsAnyAsync<Exception>(
            () => storage.DeleteObjectAsync("livecore-private-assets", "org/ws/never.bin", CancellationToken.None));

        stopwatch.Stop();

        // Bounded: it aborted an order of magnitude below the AWS SDK's 100-second default (and with no retry
        // amplification), so a hung storage backend can never hold the calling thread indefinitely (CORE-RES-005).
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"The storage delete should have failed fast, but it took {stopwatch.Elapsed}.");
    }
}
