namespace LiveCore.Api.Hosting;

/// <summary>
/// The production secret-management and configuration contract (CORE-OPS-008, the "Production Operations
/// Readiness" epic).
///
/// Every secret/config value the host consumes is supplied at runtime from configuration only — no credential
/// lives in source (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The full, names-only contract ships as the
/// repository-root <c>.env.example</c>, and the env-var → secret-store mapping is documented in
/// docs/13_SELF_HOSTING_REQUIREMENTS.md. This type is the single, testable list of the settings that MUST be
/// present for a correct production deployment, plus the pure decision that reports which of them are missing.
///
/// It is the startup-log counterpart of the readiness gate and the audience guard, reusing the same
/// environment-aware, fail-closed posture rather than adding a new one:
/// <list type="bullet">
/// <item>Outside Production the contract is inert (an empty missing-list), preserving the local-development
/// latitude the host already grants — a Development run with no database or identity provider still starts
/// (the same latitude as <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/>,
/// CORE-OPS-004, and <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/>, CORE-OPS-005).</item>
/// <item>In Production a missing required value does NOT crash an otherwise-live process: the host stays up and
/// fails closed (authenticated routes 401, persistence-backed routes 503) and reports NOT-READY (CORE-OPS-005),
/// while <see cref="Program"/> logs a loud, NAMED startup error so the misconfiguration is unmissable. The one
/// hard fail-to-start case is the security foot-gun where an Authority is configured but the Audience is blank
/// (audience validation silently disabled), which the OIDC audience guard refuses at startup (CORE-OPS-004).</item>
/// </list>
///
/// The decision reports only the missing setting KEY NAMES, never any configured value, so it can be logged
/// without leaking a secret (threat T7) — the same reason the readiness response stays status-only.
/// </summary>
public static class ProductionConfigurationValidator
{
    /// <summary>
    /// Configuration key of the PostgreSQL connection string (<c>ConnectionStrings__Database</c>). It carries
    /// the database password, so it is a secret and is injected from the deployment's secret store only
    /// (docs/13_SELF_HOSTING_REQUIREMENTS.md).
    /// </summary>
    public const string DatabaseConnectionStringKey = "ConnectionStrings:Database";

    /// <summary>
    /// Configuration key of the OIDC issuer/authority the API validates access tokens against
    /// (<c>Authentication__Oidc__Authority</c>). Not a secret, but required in production: with no Authority
    /// every authenticated endpoint fails closed with 401 (docs/adr/0005-oidc-first-authentication.md).
    /// </summary>
    public const string OidcAuthorityKey = "Authentication:Oidc:Authority";

    /// <summary>
    /// Configuration key of the expected token audience (<c>Authentication__Oidc__Audience</c>, the <c>aud</c>
    /// claim). Required in production: a blank Audience disables audience scoping, which the audience guard
    /// refuses at startup once an Authority is configured (CORE-OPS-004).
    /// </summary>
    public const string OidcAudienceKey = "Authentication:Oidc:Audience";

    /// <summary>
    /// The configuration keys that MUST be present for a correct production deployment. These are the always-
    /// required values; the conditionally-required secret groups (object storage <c>Assets:Storage:*</c>, the
    /// realtime backplane <c>Realtime:Backplane:*</c>, the store verification/notification adapter credentials)
    /// are required only for the features that use them and stay fail-closed when unset, so they are documented
    /// in the contract (<c>.env.example</c>, docs/13) rather than hard-required here.
    /// </summary>
    public static IReadOnlyList<string> RequiredProductionSettings { get; } =
    [
        DatabaseConnectionStringKey,
        OidcAuthorityKey,
        OidcAudienceKey,
    ];

    /// <summary>
    /// The single, pure decision behind the startup configuration contract: in a Production environment, which
    /// of the <see cref="RequiredProductionSettings"/> are absent (null or whitespace). Pure (no services, no
    /// configuration side effects) so the behavior is unit-testable directly, mirroring
    /// <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/> (CORE-OPS-004) and
    /// <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/> (CORE-OPS-005).
    /// </summary>
    /// <param name="configuration">The host configuration the required values are read from.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <returns>
    /// The required setting keys that are missing, in declared order. Empty outside Production (the contract is
    /// inert, preserving local-development latitude) and empty in Production when every required value is
    /// present. Only the KEY NAMES are returned, never the configured values, so the result can be logged
    /// without leaking a secret (threat T7).
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> FindMissingRequiredSettings(
        IConfiguration configuration,
        bool isProductionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Outside Production the contract is inert, the same local-development latitude the OIDC audience guard
        // (CORE-OPS-004) and the readiness gate (CORE-OPS-005) grant: a dev run without these values still
        // starts and fails closed.
        if (!isProductionEnvironment)
        {
            return [];
        }

        var missing = new List<string>();
        foreach (var key in RequiredProductionSettings)
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
            {
                missing.Add(key);
            }
        }

        return missing;
    }
}
