// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Production readiness gate for the API's required dependencies (CORE-OPS-005, the "Production Operations
/// Readiness" epic).
///
/// Before this story <c>/health/ready</c> reported Healthy whenever no registered readiness check failed —
/// and the only readiness check, the database connectivity check, is registered ONLY when a connection
/// string is configured (Program.cs). So a host deployed with NO persistence (and/or no OIDC identity
/// provider) reported READY while every domain route fails closed (503 with no persistence, 401 with no
/// identity provider): orchestration would route live traffic at an API that cannot actually serve it.
///
/// This check closes that gap. In a PRODUCTION environment it reports NOT-READY (Unhealthy) when a required
/// dependency — persistence (the database connection string) or OIDC (the identity Authority) — is not
/// configured, so a misconfigured production host is taken out of the ready rotation instead of advertising a
/// readiness it does not have. Outside Production the check is inert (Healthy), preserving the
/// local-development latitude the host already grants (running without a database or an identity provider) —
/// the same environment-aware posture as the OIDC audience guard (CORE-OPS-004,
/// <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/>).
///
/// The readiness response stays status-only (<see cref="HealthEndpoints"/>), so WHICH dependency is missing
/// is never leaked to the unauthenticated readiness endpoint — it is a startup log line, not an HTTP detail
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class RequiredDependencyReadiness
{
    /// <summary>
    /// Name the readiness check is registered under. Kept server-side (the readiness endpoint writes only the
    /// overall status), never leaked to the unauthenticated response.
    /// </summary>
    public const string CheckName = "required-dependencies";

    /// <summary>
    /// The single, pure decision the readiness check enforces: in a Production environment a required
    /// dependency is missing when persistence OR OIDC is unconfigured. Pure (no services, no configuration
    /// side effects) so the behavior is unit-testable directly, mirroring
    /// <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/>.
    /// </summary>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <param name="persistenceConfigured">Whether a database connection string is configured.</param>
    /// <param name="oidcConfigured">Whether an OIDC Authority is configured.</param>
    /// <returns>
    /// <see langword="true"/> only when, in Production, persistence or OIDC is unconfigured. Returns
    /// <see langword="false"/> outside Production (local-development latitude) and when both required
    /// dependencies are configured.
    /// </returns>
    public static bool RequiredDependencyMissing(
        bool isProductionEnvironment,
        bool persistenceConfigured,
        bool oidcConfigured)
        => isProductionEnvironment && (!persistenceConfigured || !oidcConfigured);

    /// <summary>
    /// Registers the production required-dependency readiness check (tagged
    /// <see cref="HealthEndpoints.ReadinessTag"/>) so <c>/health/ready</c> reports NOT-READY in Production
    /// when persistence or OIDC is unconfigured. The configured/unconfigured state of each dependency is known
    /// at startup, so it is captured here rather than re-evaluated per probe. Registered UNCONDITIONALLY
    /// (independent of whether persistence is configured), precisely so it still runs in the case where
    /// persistence is ABSENT — the case that previously reported a false READY.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="environment">The host environment (the Production check is environment-aware).</param>
    /// <param name="persistenceConfigured">Whether a database connection string is configured.</param>
    /// <param name="oidcConfigured">Whether an OIDC Authority is configured.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddRequiredDependencyReadinessCheck(
        this IServiceCollection services,
        IHostEnvironment environment,
        bool persistenceConfigured,
        bool oidcConfigured)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddHealthChecks().AddCheck(
            CheckName,
            new RequiredDependencyHealthCheck(environment.IsProduction(), persistenceConfigured, oidcConfigured),
            tags: [HealthEndpoints.ReadinessTag]);

        return services;
    }
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> behind
/// <see cref="RequiredDependencyReadiness.AddRequiredDependencyReadinessCheck"/>. It captures the environment
/// and the startup-time configured state of the required dependencies and applies the pure
/// <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/> decision. The failure description stays
/// server-side (the readiness endpoint writes only the overall status), so it never tells an anonymous caller
/// WHICH dependency is missing (threat T7).
/// </summary>
internal sealed class RequiredDependencyHealthCheck : IHealthCheck
{
    private readonly bool _isProductionEnvironment;
    private readonly bool _persistenceConfigured;
    private readonly bool _oidcConfigured;

    public RequiredDependencyHealthCheck(
        bool isProductionEnvironment,
        bool persistenceConfigured,
        bool oidcConfigured)
    {
        _isProductionEnvironment = isProductionEnvironment;
        _persistenceConfigured = persistenceConfigured;
        _oidcConfigured = oidcConfigured;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (RequiredDependencyReadiness.RequiredDependencyMissing(
                _isProductionEnvironment, _persistenceConfigured, _oidcConfigured))
        {
            // Generic, dependency-agnostic description: it is inspected server-side and is never written to
            // the unauthenticated readiness response.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "A required production dependency (persistence or OIDC) is not configured."));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
