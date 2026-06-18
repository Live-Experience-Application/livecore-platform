// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Hosting;

/// <summary>
/// Live reachability probe for the OIDC provider's discovery endpoint (CORE-OBS-009). The bearer pipeline
/// validates every authenticated request against the provider's discovery document and signing keys
/// (<see cref="LiveCore.Api.IdentityAccess.OidcAuthenticationExtensions"/>), so a configured-but-unreachable
/// identity provider means authenticated traffic fails closed (401/500): the host should leave the ready rotation
/// rather than keep taking requests it cannot authenticate.
///
/// The probe issues a single bounded GET to the well-known discovery document
/// (<c>{authority}/.well-known/openid-configuration</c> — the same document the bearer handler's configuration
/// manager fetches). A success status means the provider answered and is reachable; a non-success status or a
/// transport failure throws, and the <see cref="DependencyReadinessHealthCheck"/> bounds the call by the short
/// readiness timeout and treats any throw as not-ready (fail-closed). Only the discovery URL leaves the process;
/// no token, secret or credential is sent or logged (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), and the
/// terms are product-neutral (AGENTS.md).
/// </summary>
internal sealed class OidcDiscoveryReadinessProbe : IDependencyReadinessProbe, IDisposable
{
    /// <summary>The well-known OIDC discovery document path appended to the configured Authority.</summary>
    public const string DiscoveryDocumentPath = ".well-known/openid-configuration";

    private readonly HttpClient _httpClient;
    private readonly Uri _discoveryDocumentUrl;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates the probe for the production host. It owns a single long-lived <see cref="HttpClient"/> (the
    /// recommended reuse pattern) whose connections are recycled periodically so a provider's DNS change is picked
    /// up. The per-request bound comes from the readiness timeout (the supplied cancellation token), so the client
    /// itself uses no request timeout.
    /// </summary>
    /// <param name="authority">The configured OIDC Authority (issuer base URL).</param>
    /// <exception cref="ArgumentException">The authority is null or whitespace.</exception>
    public OidcDiscoveryReadinessProbe(string authority)
        : this(authority, CreateDefaultHttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates the probe over a supplied <see cref="HttpClient"/> (used by the tests to drive the success,
    /// non-success and transport-failure paths through a stub handler without a real provider). The probe does not
    /// own the supplied client and never disposes it.
    /// </summary>
    /// <param name="authority">The configured OIDC Authority (issuer base URL).</param>
    /// <param name="httpClient">The HTTP client the probe issues the discovery GET through.</param>
    /// <exception cref="ArgumentException">The authority is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">The HTTP client is null.</exception>
    internal OidcDiscoveryReadinessProbe(string authority, HttpClient httpClient)
        : this(authority, httpClient, ownsHttpClient: false)
    {
    }

    private OidcDiscoveryReadinessProbe(string authority, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _discoveryDocumentUrl = new Uri($"{authority.TrimEnd('/')}/{DiscoveryDocumentPath}");
    }

    /// <inheritdoc />
    public string DependencyName => "oidc-discovery";

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        // Headers-only: the discovery document body is not needed to prove the provider answered, so do not buffer
        // it. A non-success status (the provider is up but the discovery URL is wrong/broken) throws, exactly like
        // a transport failure does, so both leave the host not-ready (fail-closed).
        using var response = await _httpClient
            .GetAsync(_discoveryDocumentUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Disposes the owned <see cref="HttpClient"/> when the probe constructed it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        // Recycle pooled connections periodically so a provider DNS/endpoint change is eventually picked up by the
        // long-lived singleton client. No request timeout here: the readiness timeout (the cancellation token the
        // health check passes) is the single, authoritative bound (CORE-RES-005).
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
