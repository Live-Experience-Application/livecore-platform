using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for the <see cref="PurchaseEnvironmentPolicy"/> (CORE-MON-008) — the fail-closed Core decision of
/// whether a verified purchase may be honored on a deployment given the environment its proof was verified in.
/// The security-relevant case is the HEADLINE one: a <see cref="PurchaseEnvironment.Sandbox"/> purchase is NOT
/// honored on a PRODUCTION deployment ("a sandbox receipt is not honored in production",
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md), because a sandbox receipt is free to mint in the provider's
/// test harness and must never unlock premium without a real, money-backed purchase behind it. Generic Store
/// vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseEnvironmentPolicyTests
{
    [Fact]
    public void Production_deployment_honors_a_production_purchase()
        => Assert.True(PurchaseEnvironmentPolicy.ForProductionDeployment().IsHonored(PurchaseEnvironment.Production));

    [Fact]
    public void Production_deployment_rejects_a_sandbox_purchase()
    {
        // The crown jewel: a sandbox receipt is NOT honored in production. Honoring one would let anyone unlock
        // premium with a free test-harness receipt.
        Assert.False(PurchaseEnvironmentPolicy.ForProductionDeployment().IsHonored(PurchaseEnvironment.Sandbox));
    }

    [Fact]
    public void Non_production_deployment_honors_a_sandbox_purchase()
    {
        // A local/CI/staging deployment can verify sandbox receipts, so a developer can exercise the flow without
        // a real, money-backed purchase.
        Assert.True(PurchaseEnvironmentPolicy.ForNonProductionDeployment().IsHonored(PurchaseEnvironment.Sandbox));
    }

    [Fact]
    public void Non_production_deployment_honors_a_production_purchase()
        => Assert.True(PurchaseEnvironmentPolicy.ForNonProductionDeployment().IsHonored(PurchaseEnvironment.Production));

    [Theory]
    [InlineData(true, PurchaseEnvironment.Production, true)]
    [InlineData(true, PurchaseEnvironment.Sandbox, false)]
    [InlineData(false, PurchaseEnvironment.Production, true)]
    [InlineData(false, PurchaseEnvironment.Sandbox, true)]
    public void ForDeployment_derives_the_posture_from_the_host_environment(
        bool isProductionEnvironment,
        PurchaseEnvironment environment,
        bool expectedHonored)
    {
        // The wiring Program.cs uses: production = ASPNETCORE_ENVIRONMENT=Production (the same posture the OIDC
        // audience guard / readiness gate key off). Only (production host, sandbox purchase) is rejected.
        var policy = PurchaseEnvironmentPolicy.ForDeployment(isProductionEnvironment);

        Assert.Equal(expectedHonored, policy.IsHonored(environment));
    }

    [Fact]
    public void An_unrecognized_environment_value_is_never_honored()
    {
        // Fail-closed default: an environment value the policy does not recognize can never silently unlock
        // premium, on any deployment.
        Assert.False(PurchaseEnvironmentPolicy.ForProductionDeployment().IsHonored((PurchaseEnvironment)999));
        Assert.False(PurchaseEnvironmentPolicy.ForNonProductionDeployment().IsHonored((PurchaseEnvironment)999));
    }
}
