namespace LiveCore.Api.Store;

/// <summary>
/// The fail-closed Core decision of WHETHER a verified purchase may be honored on THIS deployment given the
/// environment its proof was verified in (CORE-MON-008) — the Core-owned half of "a fail-closed port that
/// distinguishes sandbox vs production" (the story acceptance criterion). The deployment-supplied
/// <see cref="IPurchaseVerificationProvider"/> adapter reports the <see cref="VerifiedPurchase.Environment"/>;
/// this policy decides if a purchase from that environment is trusted here, BEFORE it is recorded or grants any
/// entitlement. The cryptographic verification stays in the adapter (out of Core per threat T7 /
/// docs/13_SELF_HOSTING_REQUIREMENTS.md); this is the sandbox/production separation Core guarantees around it.
///
/// THE RULE (asymmetric and fail-closed):
/// <list type="bullet">
///   <item>A <see cref="PurchaseEnvironment.Production"/> purchase is honored by every deployment — it is a real,
///   money-backed purchase.</item>
///   <item>A <see cref="PurchaseEnvironment.Sandbox"/> purchase is honored ONLY by a non-production deployment.
///   A PRODUCTION deployment rejects it: a sandbox receipt is free to mint in the provider's test harness, so
///   honoring one in production would let anyone unlock premium state without paying ("Never unlock limits before
///   server verification succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). This is the headline
///   "a sandbox receipt is not honored in production" guarantee.</item>
///   <item>Any environment value the policy does not recognize is NOT honored (fail-closed default) — an unknown
///   environment can never silently unlock premium.</item>
/// </list>
///
/// The deployment posture is derived from the host environment exactly as the OIDC audience guard (CORE-OPS-004),
/// the readiness gate (CORE-OPS-005) and the production configuration contract (CORE-OPS-008) are — production is
/// <c>ASPNETCORE_ENVIRONMENT=Production</c> (the default when unset). So a production host fails closed against
/// sandbox receipts with no extra configuration, while a local/test deployment keeps the latitude to verify
/// sandbox receipts (the same fail-closed-in-production / permissive-locally posture those gates use). The policy
/// is a pure, product-neutral value: it reads no configuration and holds no secret (threat T7).
/// </summary>
public sealed class PurchaseEnvironmentPolicy
{
    private readonly bool _honorsSandbox;

    private PurchaseEnvironmentPolicy(bool honorsSandbox) => _honorsSandbox = honorsSandbox;

    /// <summary>
    /// The policy for a PRODUCTION deployment: only a <see cref="PurchaseEnvironment.Production"/> purchase is
    /// honored; a <see cref="PurchaseEnvironment.Sandbox"/> purchase is rejected (a sandbox receipt is not honored
    /// in production).
    /// </summary>
    public static PurchaseEnvironmentPolicy ForProductionDeployment() => new(honorsSandbox: false);

    /// <summary>
    /// The policy for a NON-production deployment (local development, CI, a staging/sandbox environment): a
    /// <see cref="PurchaseEnvironment.Sandbox"/> purchase is honored (so developers can verify sandbox receipts),
    /// as is a <see cref="PurchaseEnvironment.Production"/> one.
    /// </summary>
    public static PurchaseEnvironmentPolicy ForNonProductionDeployment() => new(honorsSandbox: true);

    /// <summary>
    /// Selects the policy for the deployment's host environment: <see cref="ForProductionDeployment"/> when
    /// <paramref name="isProductionEnvironment"/> is <see langword="true"/> (a production host fails closed against
    /// sandbox receipts), otherwise <see cref="ForNonProductionDeployment"/>.
    /// </summary>
    /// <param name="isProductionEnvironment">Whether the host runs in the Production environment.</param>
    public static PurchaseEnvironmentPolicy ForDeployment(bool isProductionEnvironment)
        => isProductionEnvironment ? ForProductionDeployment() : ForNonProductionDeployment();

    /// <summary>
    /// Whether a purchase verified in <paramref name="environment"/> may be honored on this deployment. Fail-closed:
    /// a <see cref="PurchaseEnvironment.Sandbox"/> purchase is honored only on a non-production deployment, and any
    /// unrecognized environment value is never honored.
    /// </summary>
    /// <param name="environment">The environment the provider adapter verified the purchase in.</param>
    /// <returns><see langword="true"/> when the purchase may be recorded and grant entitlements; otherwise <see langword="false"/>.</returns>
    public bool IsHonored(PurchaseEnvironment environment)
        => environment switch
        {
            PurchaseEnvironment.Production => true,
            PurchaseEnvironment.Sandbox => _honorsSandbox,
            _ => false,
        };
}
