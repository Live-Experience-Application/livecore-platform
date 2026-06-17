// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Documentation tests for the API evolution policy (CORE-DX-006). The acceptance criterion requires a
/// documented additive-only evolution rule alongside the deprecation/sunset header mechanism. These assert the
/// rule and the mechanism are actually written down where the policy lives — the architecture doc
/// (<c>docs/02</c>), the API contract doc (<c>docs/08</c>) and the package versioning doc (<c>docs/23</c>) — so
/// the convention cannot be silently dropped. They read the checked-in markdown from the repository root (found
/// by walking up to the <c>LiveCore.slnx</c> solution file), so they are independent of any build output.
/// </summary>
public class ApiEvolutionPolicyDocTests
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
    public void The_api_contract_doc_states_the_additive_only_rule_and_the_sunset_mechanism()
    {
        var doc = ReadDoc(Path.Combine("docs", "08_API_CONTRACTS.md"));

        // The additive-only evolution rule.
        Assert.Contains("additive-only", doc, StringComparison.OrdinalIgnoreCase);

        // The deprecation/sunset header mechanism (RFC 8594) that gives advance signal.
        Assert.Contains("RFC 8594", doc, StringComparison.Ordinal);
        Assert.Contains("Sunset", doc, StringComparison.Ordinal);
        Assert.Contains("Deprecation", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_architecture_doc_records_the_evolution_and_deprecation_policy()
    {
        var doc = ReadDoc(Path.Combine("docs", "02_ARCHITECTURE.md"));

        Assert.Contains("additive-only", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sunset", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_versioning_doc_ties_the_additive_only_rule_to_the_runtime_contract()
    {
        var doc = ReadDoc(Path.Combine("docs", "23_PACKAGE_VERSIONING.md"));

        Assert.Contains("additive-only", doc, StringComparison.OrdinalIgnoreCase);
    }
}
