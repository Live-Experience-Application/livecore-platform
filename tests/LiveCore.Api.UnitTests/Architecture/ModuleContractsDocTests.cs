// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.UnitTests.Architecture;

/// <summary>
/// Documentation-sync tests for the enforced module dependency graph (CORE-ARCH-001). The acceptance criterion
/// requires the allowed-edges list to be explicit and reviewed, and the graph is documented for humans in
/// <c>docs/02_ARCHITECTURE.md</c> and <c>docs/05_MODULE_CONTRACTS.md</c>. These tests assert the docs actually
/// describe the graph and — crucially — that the per-module list in <c>docs/05</c> stays EXACTLY in step with the
/// enforced <see cref="ModuleDependencyGraph.AllowedDependencies"/>, so the code and the reviewed documentation
/// cannot silently diverge. They read the checked-in markdown from the repository root (found by walking up to the
/// <c>LiveCore.slnx</c> solution file), independent of any build output.
/// </summary>
public class ModuleContractsDocTests
{
    private static string ReadDoc(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveCore.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, relativePath);
        Assert.True(File.Exists(path), $"Expected documentation file at {path}.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_architecture_doc_records_the_enforced_dependency_graph()
    {
        var doc = ReadDoc(Path.Combine("docs", "02_ARCHITECTURE.md"));

        Assert.Contains("Enforced module dependency graph", doc, StringComparison.Ordinal);
        Assert.Contains("CORE-ARCH-001", doc, StringComparison.Ordinal);
        Assert.Contains("NetArchTest", doc, StringComparison.Ordinal);
        // The shared-kernel concept must be documented so the model is reviewable.
        Assert.Contains("Shared kernel", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_module_contracts_doc_lists_the_dependency_graph_exactly()
    {
        var doc = ReadDoc(Path.Combine("docs", "05_MODULE_CONTRACTS.md"));

        Assert.Contains("Enforced dependency graph (CORE-ARCH-001)", doc, StringComparison.Ordinal);

        // Parse the per-module edge rows ("Module -> A, B, C") from the doc.
        var documented = ParseDocumentedEdges(doc);

        var errors = new List<string>();

        // Every governed module must appear exactly once in the doc.
        foreach (var module in ModuleDependencyGraph.GovernedModules)
        {
            if (!documented.ContainsKey(module))
            {
                errors.Add($"docs/05 is missing a dependency-graph row for module '{module}'.");
            }
        }

        foreach (var module in documented.Keys)
        {
            if (!ModuleDependencyGraph.GovernedModules.Contains(module))
            {
                errors.Add($"docs/05 lists an unknown module '{module}' in the dependency graph.");
            }
        }

        // The documented targets must match the enforced allow-list exactly (order-insensitive).
        foreach (var module in ModuleDependencyGraph.GovernedModules)
        {
            if (!documented.TryGetValue(module, out var documentedTargets))
            {
                continue;
            }

            var enforced = ModuleDependencyGraph.AllowedDependencies.TryGetValue(module, out var edges)
                ? edges
                : Array.Empty<string>();

            var documentedSet = documentedTargets.ToHashSet();
            var enforcedSet = enforced.ToHashSet();

            var onlyInDoc = documentedSet.Except(enforcedSet).OrderBy(x => x).ToArray();
            var onlyInCode = enforcedSet.Except(documentedSet).OrderBy(x => x).ToArray();

            if (onlyInDoc.Length > 0)
            {
                errors.Add($"'{module}': docs/05 lists targets not enforced in code: {string.Join(", ", onlyInDoc)}.");
            }

            if (onlyInCode.Length > 0)
            {
                errors.Add($"'{module}': code allows targets not documented in docs/05: {string.Join(", ", onlyInCode)}.");
            }
        }

        Assert.True(
            errors.Count == 0,
            "docs/05_MODULE_CONTRACTS.md and ModuleDependencyGraph.AllowedDependencies have drifted. Update both " +
            "when an edge changes:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static Dictionary<string, string[]> ParseDocumentedEdges(string doc)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Match rows of the form "ModuleName    -> A, B, C" or "ModuleName -> none". Rows that start with a
        // backtick (the "`A -> none` means..." explanatory text) do not match because they do not start with a word.
        var rowRegex = new Regex(@"^([A-Za-z]+)\s+->\s+(.+)$", RegexOptions.Multiline);

        foreach (Match match in rowRegex.Matches(doc))
        {
            var module = match.Groups[1].Value;

            // Only treat rows whose source is a governed module as graph rows (skip prose like "Host -> ...").
            if (!ModuleDependencyGraph.GovernedModules.Contains(module))
            {
                continue;
            }

            var rhs = match.Groups[2].Value.Trim();
            var targets = rhs.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? Array.Empty<string>()
                : rhs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            result[module] = targets;
        }

        return result;
    }
}
