// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Hosting;

/// <summary>
/// The startup posture the host takes for its realtime scale-out backplane (CORE-RES-006, the "Multi-Instance
/// Runtime Correctness" epic). The realtime backplane is opt-in: with no connection string configured the host
/// keeps the in-process SignalR backplane (<see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>),
/// which delivers correctly for a SINGLE API instance only — an event computed on one instance is silently
/// dropped for clients connected to the others (docs/11_REALTIME_SYNC.md "Scale-out"). This is the verdict the
/// startup guard turns into either silence, a prominent warning or a refuse-to-start error.
/// </summary>
public enum RealtimeBackplaneStartupStatus
{
    /// <summary>
    /// The topology is correct, so startup is SILENT: a backplane is configured (any number of instances fans
    /// realtime out across them), or a single-instance development run (the documented, unaffected case).
    /// </summary>
    Ok,

    /// <summary>
    /// A single declared instance on the in-process backplane in a container/production deployment. This is
    /// correct WHILE exactly one instance runs, but scaling up without first configuring a backplane would
    /// silently drop cross-instance delivery — so the host starts normally and logs ONE prominent startup
    /// warning that cross-instance realtime delivery is disabled.
    /// </summary>
    SingleInstanceWarning,

    /// <summary>
    /// More than one declared API instance with NO backplane configured: a definite misconfiguration in any
    /// environment (a reveal computed on one instance never reaches clients on the others). The host REFUSES TO
    /// START with a clear, named error — the app-side counterpart of the Helm chart refusing to render the same
    /// topology (CORE-DEP-009) and the OIDC audience guard refusing to start (CORE-OPS-004).
    /// </summary>
    MultiInstanceMisconfigured,
}

/// <summary>
/// The realtime backplane topology guard (CORE-RES-006, the "Multi-Instance Runtime Correctness" epic) — the
/// app-side defence in depth for the misconfiguration CORE-DEP-009 made the Helm chart refuse to render.
///
/// <para>
/// The realtime backplane is opt-in (<see cref="RealtimeBackplaneOptions"/>), so before this story a deployment
/// that ran more than one API instance with no backplane started SILENTLY and then silently dropped cross-instance
/// realtime delivery; <see cref="ProductionConfigurationValidator"/> deliberately omits the backplane from its
/// required set (it is conditionally required, not always required), so the misconfiguration was invisible at the
/// app layer. CORE-DEP-009 closed it for a Helm install (the chart refuses to render a multi-replica install with
/// no backplane), but a path that bypasses the chart — a <c>kubectl scale</c>, a Compose <c>--scale api=N</c>, a
/// hand-written manifest — would still run the broken topology. This guard catches it at the app, wherever the
/// host is launched.
/// </para>
///
/// <para>
/// It REUSES the <see cref="ProductionConfigurationValidator"/> pattern: a single, pure, environment-aware,
/// fail-closed decision (<see cref="Evaluate"/>) over the deployment-declared instance count
/// (<see cref="InstanceCountKey"/>) and whether a backplane connection string is configured, so the behaviour is
/// unit-testable directly, mirroring
/// <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/> (CORE-OPS-004) and
/// <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/> (CORE-OPS-005). It reads only the
/// TOPOLOGY — an instance count and whether a connection string is present, never the connection string value —
/// so the verdict can be logged without leaking a secret (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
public static class RealtimeBackplaneStartupValidator
{
    /// <summary>
    /// Configuration key of the deployment's declared API instance count (<c>Realtime:InstanceCount</c>, e.g.
    /// the environment variable <c>Realtime__InstanceCount</c>). The deployment sets it to the number of API
    /// instances it runs (the Helm <c>api.replicaCount</c>, a Compose <c>--scale api=N</c>); absent or
    /// non-positive it defaults to <see cref="DefaultInstanceCount"/> (a single instance), so a normal
    /// single-instance run never has to set it. It is a topology hint only and carries nothing sensitive
    /// (threat T7).
    /// </summary>
    public const string InstanceCountKey = "Realtime:InstanceCount";

    /// <summary>
    /// Configuration key of the realtime backplane connection string (<c>Realtime:Backplane:ConnectionString</c>),
    /// reported BY NAME in the startup diagnostics so the operator knows what to configure — never its value
    /// (threat T7). Kept in step with the key <see cref="RealtimeBackplaneOptions"/> actually reads.
    /// </summary>
    public const string BackplaneConnectionStringKey =
        RealtimeBackplaneOptions.ConfigurationSection + ":ConnectionString";

    /// <summary>
    /// The instance count assumed when <see cref="InstanceCountKey"/> is unset, blank, unparseable or
    /// non-positive: a SINGLE instance, the safe default for which the in-process backplane is correct.
    /// </summary>
    public const int DefaultInstanceCount = 1;

    /// <summary>
    /// The single, pure decision behind the startup guard: given the deployment's declared instance count, whether
    /// a realtime backplane connection string is configured and whether the host runs in a container/production
    /// environment, what posture should startup take. Pure (no services, no configuration side effects) so the
    /// behaviour is unit-testable directly.
    ///
    /// <list type="bullet">
    /// <item>A configured backplane fans realtime out across instances, so EVERY topology is correct:
    /// <see cref="RealtimeBackplaneStartupStatus.Ok"/> (silent).</item>
    /// <item>No backplane and more than one declared instance is broken in ANY environment — fail closed with
    /// <see cref="RealtimeBackplaneStartupStatus.MultiInstanceMisconfigured"/> (refuse to start), so a multi-instance
    /// misconfiguration is never silently allowed (the fail-closed default).</item>
    /// <item>No backplane, a single declared instance, in a container/production deployment is correct now but a
    /// scale-up foot-gun: <see cref="RealtimeBackplaneStartupStatus.SingleInstanceWarning"/>.</item>
    /// <item>No backplane, a single declared instance, outside production (local development) is
    /// <see cref="RealtimeBackplaneStartupStatus.Ok"/> (silent) — single-instance development is unaffected.</item>
    /// </list>
    /// </summary>
    /// <param name="instanceCount">The deployment's declared number of API instances (see <see cref="InstanceCountKey"/>).</param>
    /// <param name="backplaneConfigured">Whether a realtime backplane connection string is configured.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a container/production environment.</param>
    public static RealtimeBackplaneStartupStatus Evaluate(
        int instanceCount,
        bool backplaneConfigured,
        bool isProductionEnvironment)
    {
        // A configured backplane publishes every group send across instances, so any number of instances delivers
        // correctly: start silently regardless of environment or count.
        if (backplaneConfigured)
        {
            return RealtimeBackplaneStartupStatus.Ok;
        }

        // No backplane: the in-process SignalR backplane is in use, which delivers correctly only WITHIN one
        // instance. More than one declared instance is therefore a definite misconfiguration everywhere — an event
        // computed on one instance is silently dropped for clients connected to the others — so fail closed and
        // refuse to start, irrespective of environment (the same posture the Helm chart takes at render time,
        // CORE-DEP-009).
        if (instanceCount > 1)
        {
            return RealtimeBackplaneStartupStatus.MultiInstanceMisconfigured;
        }

        // No backplane and a single declared instance is correct. In a container/production deployment surface a
        // prominent warning so the operator knows cross-instance delivery is off before they scale; a
        // single-instance development run stays silent (unaffected).
        return isProductionEnvironment
            ? RealtimeBackplaneStartupStatus.SingleInstanceWarning
            : RealtimeBackplaneStartupStatus.Ok;
    }

    /// <summary>
    /// Reads the deployment's declared API instance count from configuration (<see cref="InstanceCountKey"/>),
    /// trimming surrounding whitespace. A value that is absent, blank, unparseable or non-positive falls back to
    /// <see cref="DefaultInstanceCount"/> (a single instance) rather than throwing: the instance count is a
    /// deployment-topology hint, and a malformed hint must never make the host worse than the documented
    /// single-instance default.
    /// </summary>
    /// <param name="configuration">The host configuration the instance count is read from.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static int ReadConfiguredInstanceCount(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[InstanceCountKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultInstanceCount;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1
                ? parsed
                : DefaultInstanceCount;
    }
}
