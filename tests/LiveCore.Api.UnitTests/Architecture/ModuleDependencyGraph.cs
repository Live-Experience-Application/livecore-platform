// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.UnitTests.Architecture;

/// <summary>
/// The explicit, reviewed inter-module dependency graph of the Core API modular monolith (CORE-ARCH-001).
///
/// <para>
/// The Core API is a modular monolith: every "module" is a folder/namespace under <c>LiveCore.Api.*</c> in the
/// single <c>LiveCore.Api.csproj</c>, with no project boundary to stop one module referencing another. This type
/// captures the allowed edges as data so <see cref="ModuleBoundaryArchitectureTests"/> can assert them against the
/// COMPILED assembly and fail when a module references one it must not. The same graph is documented for humans in
/// <c>docs/02_ARCHITECTURE.md</c> and <c>docs/05_MODULE_CONTRACTS.md</c>; the doc-sync tests keep the two in step.
/// </para>
///
/// <para>
/// <b>Governed domain modules</b> (<see cref="GovernedModules"/>) are the nodes of the graph. An edge
/// <c>A -&gt; B</c> in <see cref="AllowedDependencies"/> means types in <c>LiveCore.Api.A</c> may reference types in
/// <c>LiveCore.Api.B</c>. Any governed-to-governed reference NOT listed is forbidden and fails the architecture
/// test.
/// </para>
///
/// <para>
/// <b>Shared kernel</b> (<see cref="SharedKernel"/>) is the cross-cutting infrastructure every module may depend on
/// and is therefore NOT a node in the governed graph (neither restricted as a source nor forbidden as a target):
/// <c>Persistence</c> (the shared EF data layer / <c>LiveCoreDbContext</c> and repositories — it deliberately
/// references every aggregate, so it is infrastructure, not a peer module), <c>Observability</c> (cross-cutting
/// metrics/tracing/log context), <c>Hosting</c> (the composition root and the mobile API gateway — a composition
/// root references everything by design), and <c>SystemModule</c> (shared idempotency primitives). The root
/// <c>LiveCore.Api</c> namespace (the shared <c>CoreProblem</c>/<c>ProblemCodes</c>/<c>Pagination</c>/<c>EntityTag</c>
/// helpers) is shared kernel for the same reason.
/// </para>
///
/// <para>
/// The edge list is the CURRENT, reviewed reality measured from the compiled assembly: it is a freeze, not an
/// aspiration. Its job is to (1) prevent a NEW illegal edge being added silently and (2) document the intended
/// graph. Some existing edges form cycles (for example <c>Content</c>&#8596;<c>Scenes</c> and
/// <c>Realtime</c>&#8596;<c>Visibility</c>); breaking those is a refactor outside this story's scope, so they are
/// captured here as the reviewed status quo. Tightening the graph (removing an edge) is a deliberate, reviewed
/// change: delete the edge here AND remove the underlying reference.
/// </para>
/// </summary>
internal static class ModuleDependencyGraph
{
    /// <summary>The namespace prefix shared by every Core API module.</summary>
    public const string RootNamespace = "LiveCore.Api";

    /// <summary>
    /// The governed domain modules — the nodes of the inter-module graph. These are the modules from
    /// <c>docs/02_ARCHITECTURE.md</c> ("Backend module boundaries") plus the mobile-extension and lifecycle modules
    /// added since (<c>Entitlements</c>, <c>Store</c>, <c>Recaps</c>, <c>Retention</c>). The shared-kernel
    /// infrastructure namespaces (<see cref="SharedKernel"/>) are intentionally absent.
    /// </summary>
    public static readonly IReadOnlyList<string> GovernedModules = new[]
    {
        "Assets",
        "Audit",
        "Content",
        "Entities",
        "Entitlements",
        "Exports",
        "IdentityAccess",
        "Organizations",
        "Participants",
        "Realtime",
        "Recaps",
        "Retention",
        "Scenes",
        "Sessions",
        "Store",
        "Templates",
        "Visibility",
        "Workspaces",
    };

    /// <summary>
    /// Cross-cutting infrastructure namespaces every module may depend on. Not governed as graph nodes; documented
    /// here so the completeness test can assert they neither overlap the governed set nor are silently dropped.
    /// </summary>
    public static readonly IReadOnlyList<string> SharedKernel = new[]
    {
        "Persistence",
        "Observability",
        "Hosting",
        "SystemModule",
    };

    /// <summary>
    /// The allowed edges: each governed module mapped to the OTHER governed modules its types may reference. Every
    /// entry is a real dependency present in the compiled tree today (asserted minimal by the no-stale-edges test).
    /// Targets in <see cref="SharedKernel"/> and the root helpers are always allowed and are never listed here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedDependencies =
        new Dictionary<string, IReadOnlyList<string>>
        {
            // Asset metadata + signed-URL authorization references the resources assets attach to and the
            // identity/tenant/visibility context that authorizes a download.
            ["Assets"] = new[]
            {
                "Audit", "Content", "Entities", "Entitlements", "IdentityAccess",
                "Organizations", "Participants", "Visibility", "Workspaces",
            },
            // The append-only audit log records security events for a principal within a tenant.
            ["Audit"] = new[] { "IdentityAccess", "Organizations" },
            // Content blocks live inside scenes, attach assets, and defer visibility to the Visibility module.
            ["Content"] = new[]
            {
                "Assets", "Audit", "IdentityAccess", "Organizations", "Scenes", "Visibility", "Workspaces",
            },
            // Generic entities attach assets and resolve visibility within a tenant/workspace.
            ["Entities"] = new[]
            {
                "Assets", "Audit", "IdentityAccess", "Organizations", "Visibility", "Workspaces",
            },
            // Entitlements (quota/store extension) records audit events and is scoped per principal/tenant/workspace.
            ["Entitlements"] = new[] { "Audit", "IdentityAccess", "Organizations", "Workspaces" },
            // Export jobs project the resources they package (content, entities, scenes, sessions, participants).
            ["Exports"] = new[]
            {
                "Assets", "Content", "Entities", "IdentityAccess", "Organizations",
                "Participants", "Scenes", "Sessions", "Workspaces",
            },
            // Identity mapping resolves the principal's tenant, workspace and participant memberships.
            ["IdentityAccess"] = new[] { "Audit", "Organizations", "Participants", "Workspaces" },
            // Tenancy records audit events and resolves the principal it grants membership to.
            ["Organizations"] = new[] { "Audit", "IdentityAccess" },
            // Participant records are keyed to a principal within a tenant/workspace.
            ["Participants"] = new[] { "IdentityAccess", "Organizations", "Workspaces" },
            // Realtime delivery filters session events through Visibility for the authorized participants.
            ["Realtime"] = new[]
            {
                "IdentityAccess", "Organizations", "Participants", "Sessions", "Visibility", "Workspaces",
            },
            // Recap projection reads ended sessions and publishes over Realtime.
            ["Recaps"] = new[] { "IdentityAccess", "Organizations", "Realtime", "Sessions", "Workspaces" },
            // The data-retention sweep coordinates deletion across the resources it ages out.
            ["Retention"] = new[] { "Assets", "Audit", "Exports", "Recaps", "Sessions", "Workspaces" },
            // Scenes order content within a tenant/workspace and resolve visibility.
            ["Scenes"] = new[]
            {
                "Assets", "Audit", "Content", "IdentityAccess", "Organizations", "Visibility", "Workspaces",
            },
            // Session lifecycle enforces quotas (Entitlements) and drives realtime delivery to participants.
            ["Sessions"] = new[]
            {
                "Audit", "Entitlements", "IdentityAccess", "Organizations", "Participants", "Realtime", "Workspaces",
            },
            // Store receipt verification grants entitlements and audits the change.
            ["Store"] = new[] { "Audit", "Entitlements", "IdentityAccess" },
            // Templates instantiate generic entity types within a tenant.
            ["Templates"] = new[] { "Audit", "Entities", "IdentityAccess", "Organizations" },
            // Visibility is the central security module: it computes audiences for session events over Realtime.
            ["Visibility"] = new[]
            {
                "Audit", "IdentityAccess", "Organizations", "Participants", "Realtime", "Sessions", "Workspaces",
            },
            // Workspaces enforce per-workspace quotas and are scoped to a tenant/principal.
            ["Workspaces"] = new[] { "Audit", "Entitlements", "IdentityAccess", "Organizations" },
        };

    /// <summary>The fully-qualified namespace of a module short name.</summary>
    public static string NamespaceOf(string module) => $"{RootNamespace}.{module}";

    /// <summary>
    /// The governed modules <paramref name="module"/> must NOT reference: every other governed module that is not in
    /// its allowed-dependency list. Targets outside the governed set (shared kernel, root helpers) are never
    /// forbidden.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenTargets(string module)
    {
        var allowed = AllowedDependencies.TryGetValue(module, out var edges)
            ? edges
            : Array.Empty<string>();

        return GovernedModules
            .Where(other => other != module && !allowed.Contains(other))
            .ToArray();
    }
}
