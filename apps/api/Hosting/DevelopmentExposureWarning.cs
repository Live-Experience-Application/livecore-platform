// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;

namespace LiveCore.Api.Hosting;

/// <summary>
/// The Development-default exposure warning (CORE-OPS-011, the "Deployment and Operations Completeness" epic).
///
/// The bundled deployment stack defaults to <c>ASPNETCORE_ENVIRONMENT=Development</c> so it comes up green with
/// no identity provider or object storage (deploy/compose/docker-compose.yml; docs/13). That is the right
/// LOCAL default, but in a Development environment the production readiness gate is inert
/// (<see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/>, CORE-OPS-005) and the OIDC audience
/// guard does not trip (<see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/>,
/// CORE-OPS-004), so copying those defaults onto a real server yields a GREEN BUT UNAUTHENTICATED API — the
/// footgun this story closes.
///
/// The primary fix is the opt-in production Compose overlay (deploy/compose/docker-compose.prod.yml) that
/// forces <c>ASPNETCORE_ENVIRONMENT=Production</c> so a production deployment cannot accidentally run the
/// Development posture. This type is the SECOND line of defense: a loud startup warning emitted when the host
/// is STILL in Development yet bound to a NON-LOOPBACK interface (reachable beyond localhost), so the
/// green-but-unauthenticated default cannot ship silently. It does not weaken the legitimate local-development
/// default — a Development host bound only to loopback (the normal dev posture) warns about nothing.
///
/// The decision is pure (no services, no host startup side effects) so it is unit-testable directly, mirroring
/// the environment-aware guards it complements (CORE-OPS-004 / CORE-OPS-005). It logs only the host's own bind
/// addresses (never a secret, token or tenant identifier; threat T7 in docs/07_SECURITY_THREAT_MODEL.md) and
/// changes nothing about how the host serves traffic.
/// </summary>
public static class DevelopmentExposureWarning
{
    /// <summary>
    /// The single, pure decision behind the warning: the host is running in a Development environment AND at
    /// least one of its bound listen addresses is a non-loopback interface (so it is reachable beyond
    /// localhost). Pure so the behavior is unit-testable directly.
    /// </summary>
    /// <param name="isDevelopmentEnvironment">Whether the host runs in a Development environment.</param>
    /// <param name="boundAddresses">
    /// The addresses the server actually bound to (e.g. <c>app.Urls</c>), each a URL such as
    /// <c>http://0.0.0.0:8080</c> or <c>http://localhost:5000</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when, in Development, at least one bound address is a non-loopback bind.
    /// Returns <see langword="false"/> outside Development (the production posture is the safe one and is
    /// guarded by CORE-OPS-004/005) and when every bound address is loopback (the legitimate local-development
    /// default, which is not weakened).
    /// </returns>
    /// <exception cref="ArgumentNullException">The bound-addresses sequence is null.</exception>
    public static bool IsExposedDevelopmentBind(bool isDevelopmentEnvironment, IEnumerable<string> boundAddresses)
    {
        ArgumentNullException.ThrowIfNull(boundAddresses);

        if (!isDevelopmentEnvironment)
        {
            return false;
        }

        foreach (var address in boundAddresses)
        {
            if (IsNonLoopbackBindAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides whether a single server bind address listens on a NON-loopback interface — i.e. is reachable
    /// from another machine. Loopback binds (<c>localhost</c>, the 127.0.0.0/8 range, <c>::1</c>) are not;
    /// the wildcard binds (<c>0.0.0.0</c>, <c>[::]</c>, Kestrel's <c>*</c>/<c>+</c>) and any concrete
    /// non-loopback IP or DNS host are.
    /// </summary>
    /// <param name="boundAddress">A bound listen address such as <c>http://0.0.0.0:8080</c>.</param>
    /// <returns><see langword="true"/> when the address binds to a non-loopback interface.</returns>
    public static bool IsNonLoopbackBindAddress(string? boundAddress)
    {
        if (string.IsNullOrWhiteSpace(boundAddress))
        {
            return false;
        }

        var host = ExtractHost(boundAddress);
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Kestrel wildcard binds (`*` weak, `+` strong) listen on every interface.
        if (host is "*" or "+")
        {
            return true;
        }

        // `localhost` always resolves to a loopback address.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A literal IP is loopback only inside 127.0.0.0/8 or ::1. The "any" binds (0.0.0.0 / ::) are NOT
        // loopback (IPAddress.IsLoopback returns false for them), so they correctly read as non-loopback.
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            return !IPAddress.IsLoopback(ipAddress);
        }

        // A named, non-localhost DNS host is reachable from off-box, so treat it as a non-loopback bind.
        return true;
    }

    /// <summary>
    /// Logs the loud Development-default exposure warning when <see cref="IsExposedDevelopmentBind"/> holds, and
    /// does nothing otherwise. Both the API host (Program) and the worker host
    /// (<c>WorkerHostFactory</c>) call this from <c>ApplicationStarted</c>, once the listener has resolved its
    /// bound addresses. Only the bind addresses are logged (never a secret/tenant identifier; threat T7).
    /// </summary>
    /// <param name="logger">The host logger.</param>
    /// <param name="isDevelopmentEnvironment">Whether the host runs in a Development environment.</param>
    /// <param name="boundAddresses">The addresses the server bound to (e.g. <c>app.Urls</c>).</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static void WarnIfExposedDevelopmentBind(
        ILogger logger,
        bool isDevelopmentEnvironment,
        IEnumerable<string> boundAddresses)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(boundAddresses);

        var addresses = boundAddresses as IReadOnlyCollection<string> ?? boundAddresses.ToArray();
        if (!IsExposedDevelopmentBind(isDevelopmentEnvironment, addresses))
        {
            return;
        }

        logger.LogWarning(
            "SECURITY: this host is running in the Development environment but is bound to a non-loopback " +
            "interface ({BoundAddresses}). In Development the production readiness gate is inert (CORE-OPS-005) " +
            "and the OIDC audience guard does not trip (CORE-OPS-004), so the host can come up green while " +
            "authenticated routes are open — the green-but-unauthenticated default. Set " +
            "ASPNETCORE_ENVIRONMENT=Production for any deployment reachable beyond localhost (the " +
            "deploy/compose/docker-compose.prod.yml overlay does exactly this); see " +
            "docs/13_SELF_HOSTING_REQUIREMENTS.md.",
            string.Join(", ", addresses));
    }

    /// <summary>
    /// Extracts the host portion of a server bind address, tolerant of the forms a server address feature
    /// yields: a scheme prefix (<c>http://</c>), a bracketed IPv6 literal (<c>[::1]:5000</c>), a trailing
    /// <c>:port</c>, the wildcard hosts (<c>*</c>/<c>+</c>) and a trailing path.
    /// </summary>
    private static string ExtractHost(string boundAddress)
    {
        var value = boundAddress.Trim();

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            value = value[(schemeIndex + 3)..];
        }

        var pathIndex = value.IndexOf('/');
        if (pathIndex >= 0)
        {
            value = value[..pathIndex];
        }

        // Bracketed IPv6 literal: the host is between the brackets (`[::1]:5000` -> `::1`).
        if (value.StartsWith('['))
        {
            var closeIndex = value.IndexOf(']');
            if (closeIndex > 1)
            {
                return value[1..closeIndex];
            }

            return value;
        }

        // Otherwise strip a trailing `:port`.
        var portIndex = value.LastIndexOf(':');
        if (portIndex >= 0)
        {
            value = value[..portIndex];
        }

        return value;
    }
}
