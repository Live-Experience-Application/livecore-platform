// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using LiveCore.Api.Hosting;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="ObjectStorageReadinessProbe"/> (CORE-OBS-009, hardened by CORE-OBS-014). The probe
/// FAILS CLOSED, the same semantics as the OIDC discovery probe: only a SUCCESSFUL account-level call keeps the
/// host Ready, while a non-success answer — an auth error (403, for example expired or revoked credentials) or a
/// server error (5xx) — counts the host not-ready exactly as a transport failure (no HTTP status) does, so a
/// reachable-but-broken backend leaves the rotation rather than staying Ready while every asset operation fails.
/// Driven through a stub <see cref="IAmazonS3"/> (a real <see cref="AmazonS3Client"/> subclass with its
/// account-level call overridden) so the success, auth-error, server-error and transport-failure paths are
/// exercised without a live backend — the same offline pattern the storage-adapter tests use. The
/// "not-run-when-unconfigured" half of the acceptance criteria (the probe is wired ONLY for a configured
/// backend) is pinned by <see cref="DependencyReadinessExtensionsTests"/>. Generic, product-neutral vocabulary
/// only (AGENTS.md).
/// </summary>
public class ObjectStorageReadinessProbeTests
{
    [Fact]
    public void DependencyName_is_generic_and_carries_no_secret()
        => Assert.Equal(
            "object-storage",
            new ObjectStorageReadinessProbe(StubAmazonS3Client.Succeeding()).DependencyName);

    [Fact]
    public async Task ProbeAsync_completes_when_ListBuckets_succeeds()
    {
        var probe = new ObjectStorageReadinessProbe(StubAmazonS3Client.Succeeding());

        // A successful account-level call proves the backend answers: no exception => the host stays Ready.
        await probe.ProbeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProbeAsync_is_not_ready_on_a_403_so_a_reachable_but_broken_backend_leaves_rotation()
    {
        // Expired/revoked or forbidden credentials: the backend ANSWERED, but every asset operation would fail.
        // Fail closed (CORE-OBS-014) — matching the OIDC probe — so the host leaves the ready rotation.
        var probe = new ObjectStorageReadinessProbe(
            StubAmazonS3Client.Answering(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<AmazonS3Exception>(() => probe.ProbeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProbeAsync_is_not_ready_on_a_5xx_server_error(HttpStatusCode statusCode)
    {
        // A server error means the backend is up but broken: not-ready, exactly as a transport failure is.
        var probe = new ObjectStorageReadinessProbe(StubAmazonS3Client.Answering(statusCode));

        await Assert.ThrowsAsync<AmazonS3Exception>(() => probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_is_not_ready_on_a_transport_failure_with_no_status()
    {
        // A connection-refused / DNS / timeout failure never reached the endpoint (it carries the default, zero
        // status): not-ready, as it was before this story.
        var probe = new ObjectStorageReadinessProbe(StubAmazonS3Client.TransportFailing());

        await Assert.ThrowsAsync<AmazonS3Exception>(() => probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_honors_cancellation()
    {
        var probe = new ObjectStorageReadinessProbe(StubAmazonS3Client.Hanging());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // The readiness timeout (the supplied token) must cancel a hung call promptly so it fails closed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(cancellation.Token));
    }

    [Fact]
    public void Constructor_rejects_a_null_client()
        => Assert.Throws<ArgumentNullException>(() => new ObjectStorageReadinessProbe(null!));

    /// <summary>
    /// A real AWS SDK S3 client (constructed with static, fake-but-well-formed credentials and a custom
    /// <c>ServiceURL</c>, so it never reaches a backend) whose account-level <c>ListBuckets</c> call is overridden
    /// to drive the probe's success / non-success / transport-failure / cancellation paths offline. A non-success
    /// answer is modelled exactly as the SDK surfaces one — an <see cref="AmazonS3Exception"/> carrying the
    /// returned HTTP status; a transport failure carries the default (zero) status.
    /// </summary>
    private sealed class StubAmazonS3Client : AmazonS3Client
    {
        private readonly Func<CancellationToken, Task<ListBucketsResponse>> _listBuckets;

        private StubAmazonS3Client(Func<CancellationToken, Task<ListBucketsResponse>> listBuckets)
            : base(
                new BasicAWSCredentials("test-access-key", "test-secret-key"),
                new AmazonS3Config
                {
                    ServiceURL = "https://storage.example.test",
                    ForcePathStyle = true,
                    AuthenticationRegion = "us-east-1",
                })
            => _listBuckets = listBuckets;

        public override Task<ListBucketsResponse> ListBucketsAsync(CancellationToken cancellationToken = default)
            => _listBuckets(cancellationToken);

        public static StubAmazonS3Client Succeeding()
            => new(_ => Task.FromResult(new ListBucketsResponse()));

        public static StubAmazonS3Client Answering(HttpStatusCode statusCode)
            => new(_ => throw new AmazonS3Exception("the backend answered with an error")
            {
                StatusCode = statusCode,
            });

        public static StubAmazonS3Client TransportFailing()
            // Default (zero) status: the call never reached the endpoint.
            => new(_ => throw new AmazonS3Exception("never reached the endpoint"));

        public static StubAmazonS3Client Hanging()
            => new(async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ListBucketsResponse();
            });
    }
}
