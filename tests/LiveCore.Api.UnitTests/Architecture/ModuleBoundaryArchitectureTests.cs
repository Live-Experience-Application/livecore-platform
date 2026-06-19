// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Reflection;
using System.Text;
using NetArchTest.Rules;

namespace LiveCore.Api.UnitTests.Architecture;

/// <summary>
/// Architecture (fitness-function) tests that ENFORCE the modular monolith's inter-module dependency graph
/// (CORE-ARCH-001). The Core API's modules are folders/namespaces in one <c>LiveCore.Api.csproj</c>, so nothing in
/// the compiler stops one module referencing another it must not. These tests read the COMPILED
/// <c>LiveCore.Api</c> assembly with NetArchTest (Mono.Cecil under the hood, so REFERENCES IN METHOD BODIES are
/// inspected, not just signatures) and assert that every governed module depends only on the modules the explicit,
/// reviewed graph in <see cref="ModuleDependencyGraph"/> allows. The suite runs in the standard
/// <c>LiveCore.Api.UnitTests</c> project, so it gates CI.
///
/// <para>
/// Coverage of the acceptance criteria: <see cref="Every_governed_module_depends_only_on_modules_the_graph_allows"/>
/// asserts the current tree passes; <see cref="A_forbidden_cross_module_reference_fails_the_architecture_test"/> is
/// the negative test that proves an illegal cross-module reference is actually detected and fails the rule; the
/// self-validation tests keep the allow-list explicit, minimal and honest.
/// </para>
/// </summary>
public class ModuleBoundaryArchitectureTests
{
    private static readonly Assembly _apiAssembly = typeof(LiveCore.Api.CoreProblem).Assembly;

    /// <summary>
    /// The acceptance criterion's positive case: the CURRENT tree passes. Every governed module references only the
    /// other governed modules its reviewed allow-list permits (plus the always-allowed shared kernel and root
    /// helpers). A new edge that is not in <see cref="ModuleDependencyGraph.AllowedDependencies"/> fails here, with a
    /// message naming the offending module, the forbidden target and the concrete types — so the author either
    /// removes the reference or has the new edge reviewed and added to the graph (and its docs).
    /// </summary>
    [Fact]
    public void Every_governed_module_depends_only_on_modules_the_graph_allows()
    {
        var failures = new StringBuilder();

        foreach (var module in ModuleDependencyGraph.GovernedModules)
        {
            var forbidden = ModuleDependencyGraph.ForbiddenTargets(module);
            if (forbidden.Count == 0)
            {
                continue;
            }

            var forbiddenNamespaces = forbidden.Select(ModuleDependencyGraph.NamespaceOf).ToArray();

            var result = Types.InAssembly(_apiAssembly)
                .That()
                .ResideInNamespace(ModuleDependencyGraph.NamespaceOf(module))
                .ShouldNot()
                .HaveDependencyOnAny(forbiddenNamespaces)
                .GetResult();

            if (!result.IsSuccessful)
            {
                var offenders = string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>());
                failures.AppendLine(
                    $"Module '{module}' references a module outside its allowed graph. Offending types: {offenders}.");
            }
        }

        Assert.True(
            failures.Length == 0,
            "The compiled module dependency graph violates the reviewed allow-list in ModuleDependencyGraph. " +
            "Either remove the cross-module reference or, if the new edge is legitimate, get it reviewed and add it " +
            "to ModuleDependencyGraph.AllowedDependencies AND docs/02_ARCHITECTURE.md / docs/05_MODULE_CONTRACTS.md." +
            Environment.NewLine + failures);
    }

    /// <summary>
    /// The acceptance criterion's NEGATIVE test: a deliberately-introduced illegal cross-module reference must fail
    /// the architecture test. Rather than commit a real boundary violation into production source (which would itself
    /// break the build and pollute the tree), we drive the SAME rule engine over the REAL compiled assembly with a
    /// real, existing edge reclassified as forbidden, and assert the rule reports it as a failure naming the
    /// offending types. This proves the guard actually fails when a module references one it must not — if NetArchTest
    /// silently passed everything, this test would catch it.
    /// </summary>
    [Fact]
    public void A_forbidden_cross_module_reference_fails_the_architecture_test()
    {
        // Pick a real, currently-existing edge from the reviewed graph and treat its target as forbidden.
        var (source, forbiddenTarget) = ModuleDependencyGraph.AllowedDependencies
            .SelectMany(entry => entry.Value.Select(target => (Source: entry.Key, Target: target)))
            .First();

        var result = Types.InAssembly(_apiAssembly)
            .That()
            .ResideInNamespace(ModuleDependencyGraph.NamespaceOf(source))
            .ShouldNot()
            .HaveDependencyOnAny(ModuleDependencyGraph.NamespaceOf(forbiddenTarget))
            .GetResult();

        Assert.False(
            result.IsSuccessful,
            $"Expected the architecture rule to FAIL for the deliberately-forbidden edge '{source}' -> " +
            $"'{forbiddenTarget}', but it passed — the guard would not catch an illegal cross-module reference.");
        Assert.NotEmpty(result.FailingTypeNames ?? Enumerable.Empty<string>());
    }

    /// <summary>
    /// Self-validation: the explicit allow-list must reference only known governed modules — no self-edges, no
    /// duplicates, and no shared-kernel/unknown targets (those are always allowed and must not be listed). This keeps
    /// the reviewed graph well-formed and catches a typo'd or renamed module name.
    /// </summary>
    [Fact]
    public void The_allow_list_is_well_formed()
    {
        var governed = ModuleDependencyGraph.GovernedModules.ToHashSet();
        var errors = new StringBuilder();

        foreach (var (module, targets) in ModuleDependencyGraph.AllowedDependencies)
        {
            if (!governed.Contains(module))
            {
                errors.AppendLine($"Allow-list source '{module}' is not a governed module.");
            }

            if (targets.Distinct().Count() != targets.Count)
            {
                errors.AppendLine($"Allow-list for '{module}' contains duplicate targets.");
            }

            foreach (var target in targets)
            {
                if (target == module)
                {
                    errors.AppendLine($"Allow-list for '{module}' contains a self-edge.");
                }
                else if (!governed.Contains(target))
                {
                    errors.AppendLine(
                        $"Allow-list for '{module}' lists '{target}', which is not a governed module " +
                        "(shared-kernel and root targets are always allowed and must not be listed).");
                }
            }
        }

        // The governed set and the shared kernel must be disjoint.
        var overlap = governed.Intersect(ModuleDependencyGraph.SharedKernel).ToArray();
        if (overlap.Length > 0)
        {
            errors.AppendLine($"Modules appear in both the governed set and the shared kernel: {string.Join(", ", overlap)}.");
        }

        Assert.True(errors.Length == 0, errors.ToString());
    }

    /// <summary>
    /// Self-validation: every governed module name (and every shared-kernel name) actually resolves to a namespace
    /// that contains types in the compiled assembly. This fails fast if a module is renamed/removed without updating
    /// the graph, so the enforcement can never silently become a no-op over a stale, non-existent namespace.
    /// </summary>
    [Fact]
    public void Every_declared_module_namespace_exists_in_the_assembly()
    {
        var missing = new List<string>();

        foreach (var module in ModuleDependencyGraph.GovernedModules.Concat(ModuleDependencyGraph.SharedKernel))
        {
            var hasTypes = Types.InAssembly(_apiAssembly)
                .That()
                .ResideInNamespace(ModuleDependencyGraph.NamespaceOf(module))
                .GetTypes()
                .Any();

            if (!hasTypes)
            {
                missing.Add(module);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"These declared modules have no types in LiveCore.Api (renamed or removed?): {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Self-validation / honesty: the allow-list is a freeze of REAL edges, so every declared edge must still exist
    /// in the compiled tree. A stale entry means a dependency was removed without tightening the graph; this test
    /// flags it so the reviewed list never rots into permitting edges that no longer exist (which would weaken the
    /// guard).
    /// </summary>
    [Fact]
    public void The_allow_list_has_no_stale_edges()
    {
        var stale = new List<string>();

        foreach (var (module, targets) in ModuleDependencyGraph.AllowedDependencies)
        {
            foreach (var target in targets)
            {
                var edgeExists = Types.InAssembly(_apiAssembly)
                    .That()
                    .ResideInNamespace(ModuleDependencyGraph.NamespaceOf(module))
                    .And()
                    .HaveDependencyOnAny(ModuleDependencyGraph.NamespaceOf(target))
                    .GetTypes()
                    .Any();

                if (!edgeExists)
                {
                    stale.Add($"{module} -> {target}");
                }
            }
        }

        Assert.True(
            stale.Count == 0,
            "These allowed edges no longer exist in the compiled tree; remove them from ModuleDependencyGraph " +
            "(and the docs) so the reviewed graph stays minimal: " + string.Join(", ", stale));
    }
}
