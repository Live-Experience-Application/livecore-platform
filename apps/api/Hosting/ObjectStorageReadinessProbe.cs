// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.S3;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Live readiness probe for the S3-compatible object-storage backend (CORE-OBS-009). Assets are stored as
/// objects in private object storage and served only through short-lived signed URLs minted by the storage
/// adapter (<see cref="LiveCore.Api.Assets.IAssetStorage"/>, CORE-OPS-006). If the backend is configured but
/// not answering successfully — every asset upload/download flow then fails — the host should leave the ready
/// rotation rather than keep taking traffic it cannot fully serve.
///
/// The probe reuses the SAME SDK <see cref="IAmazonS3"/> client the storage adapter is wired with and issues a
/// single bounded, account-level call to confirm the endpoint answers WITH SUCCESS. It FAILS CLOSED, the same
/// semantics as the OIDC discovery probe (<see cref="OidcDiscoveryReadinessProbe"/>, CORE-OBS-014): a SUCCESSFUL
/// <c>ListBuckets</c> keeps the host Ready, while a non-success answer — an auth error (403, for example expired
/// or revoked credentials) or a server error (5xx) — throws exactly as a transport failure (connection refused,
/// DNS failure, timeout) does, so a reachable-but-broken backend leaves the rotation instead of staying Ready
/// while every asset operation fails. (Before CORE-OBS-014 the probe failed OPEN on any HTTP answer, treating
/// only a no-response transport failure as not-ready, so a host whose credentials had expired kept advertising
/// Ready — inconsistent with the fail-closed OIDC probe.) The <see cref="DependencyReadinessHealthCheck"/>
/// bounds the call by the short readiness timeout and treats any throw as not-ready. The probe is registered
/// ONLY when storage is configured (<see cref="DependencyReadinessExtensions"/>), so the read never runs against
/// the fail-closed unconfigured storage. No credential or endpoint is logged (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md); the terms are product-neutral (AGENTS.md).
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
        // Account-level, no bucket required: the lightest call that proves the endpoint answers WITH SUCCESS and
        // the SDK client can sign and round-trip a request. Fail closed (CORE-OBS-014), the same semantics as the
        // OIDC discovery probe: a SUCCESSFUL response completes the probe (Ready), while a non-success answer —
        // a 403 auth error (e.g. expired or revoked credentials) or a 5xx server error — throws (the AWS SDK
        // raises an AmazonServiceException carrying the returned status), exactly as a transport failure does, so
        // the health check counts the host not-ready. Nothing is swallowed: a reachable-but-broken backend, where
        // every asset operation would fail, must not keep the host in rotation.
        await _storageClient.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
    }
}
