// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using LiveCore.Api.Hosting;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="ObjectStorageReadinessProbe"/> (CORE-OBS-009) — specifically its REACHABILITY
/// classification (<see cref="ObjectStorageReadinessProbe.EndpointResponded"/>). Readiness gates reachability, not
/// authorization: a backend that ANSWERS with an HTTP status (even a 403 from a permissions-scoped credential) is
/// reachable and stays Ready, while a transport failure that never reached the endpoint (connection refused, DNS,
/// timeout — which carries no HTTP status) is not-ready. Pinning this keeps the probe from taking a live host out
/// of rotation for a non-reachability problem. The full transport-failure-throws path is exercised end-to-end by
/// the integration suite. Generic, product-neutral vocabulary only (AGENTS.md).
/// </summary>
public class ObjectStorageReadinessProbeTests
{
    [Fact]
    public void DependencyName_is_generic_and_carries_no_secret()
        => Assert.Equal("object-storage", new ObjectStorageReadinessProbe(new AmazonS3Client(
            new BasicAWSCredentials("k", "s"),
            new AmazonS3Config { ServiceURL = "https://storage.example.test", ForcePathStyle = true }))
            .DependencyName);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public void EndpointResponded_is_true_when_the_backend_returned_a_status(HttpStatusCode statusCode)
        => Assert.True(ObjectStorageReadinessProbe.EndpointResponded(
            new AmazonS3Exception("answered") { StatusCode = statusCode }));

    [Fact]
    public void EndpointResponded_is_false_for_a_transport_failure_with_no_status()
        // A connection-refused / DNS / timeout failure surfaces as a service exception with the default (zero)
        // status: it never reached the endpoint, so it is not reachable.
        => Assert.False(ObjectStorageReadinessProbe.EndpointResponded(new AmazonS3Exception("never reached it")));

    [Fact]
    public void EndpointResponded_rejects_a_null_exception()
        => Assert.Throws<ArgumentNullException>(() => ObjectStorageReadinessProbe.EndpointResponded(null!));
}
