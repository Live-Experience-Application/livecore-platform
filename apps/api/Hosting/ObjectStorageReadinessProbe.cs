// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.Runtime;
using Amazon.S3;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Live reachability probe for the S3-compatible object-storage backend (CORE-OBS-009). Assets are stored as
/// objects in private object storage and served only through short-lived signed URLs minted by the storage
/// adapter (<see cref="LiveCore.Api.Assets.IAssetStorage"/>, CORE-OPS-006). If the backend is configured but
/// dead, asset upload/download flows fail, so the host should leave the ready rotation rather than keep taking
/// traffic it cannot fully serve.
///
/// The probe reuses the SAME SDK <see cref="IAmazonS3"/> client the storage adapter is wired with and issues a
/// single bounded, account-level call to confirm the endpoint ANSWERS. Reachability — not authorization — is what
/// readiness gates: a backend that responds with a client error (for example a permissions-scoped credential that
/// cannot list buckets) is still REACHABLE and stays Ready; only a transport failure (no response: connection
/// refused, DNS failure, timeout) is treated as not-ready. The <see cref="DependencyReadinessHealthCheck"/> bounds
/// the call by the short readiness timeout and fails closed on any unhandled throw. No credential or endpoint is
/// logged (threat T7 in docs/07_SECURITY_THREAT_MODEL.md); the terms are product-neutral (AGENTS.md).
/// </summary>
internal sealed class ObjectStorageReadinessProbe : IDependencyReadinessProbe
{
    private readonly IAmazonS3 _storageClient;

    public ObjectStorageReadinessProbe(IAmazonS3 storageClient)
    {
        ArgumentNullException.ThrowIfNull(storageClient);
        _storageClient = storageClient;
    }

    /// <inheritdoc />
    public string DependencyName => "object-storage";

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Account-level, no bucket required: the lightest call that proves the endpoint is reachable and the
            // SDK client can sign and round-trip a request. The body is irrelevant; only that the backend answered.
            await _storageClient.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonServiceException exception) when (EndpointResponded(exception))
        {
            // The backend ANSWERED (it returned an HTTP status, e.g. 403 for a credential not permitted to list
            // buckets): it is reachable, which is what readiness gates. A permissions/configuration error is a
            // deployment problem caught elsewhere, not an outage — treating it as not-ready would wrongly take a
            // live host out of rotation. A transport failure carries no HTTP status and propagates below.
        }
    }

    /// <summary>
    /// Whether the exception reflects an ANSWER from the storage endpoint (a real HTTP status was returned) rather
    /// than a transport failure that never reached it. An <see cref="AmazonServiceException"/> raised from a
    /// service response carries that response's <see cref="AmazonServiceException.StatusCode"/>; a connection
    /// refused / DNS / timeout failure carries the default (zero) status. Pure so the classification is
    /// unit-testable without a live backend.
    /// </summary>
    internal static bool EndpointResponded(AmazonServiceException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return (int)exception.StatusCode != 0;
    }
}
