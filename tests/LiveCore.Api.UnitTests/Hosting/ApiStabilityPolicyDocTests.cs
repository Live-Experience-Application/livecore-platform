// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Api.Hosting;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Documentation tests for the API/SDK stability policy and the path to 1.0 (CORE-REL-002). The acceptance
/// criterion requires <c>docs/23</c> to document the public-surface stability commitment, the pre-1.0
/// breaking-change posture, a concrete deprecation-window length and what declaring 1.0 means, with the RFC 8594
/// `Sunset`/`Deprecation` header mechanism tied to the documented window. These assert that the policy is actually
/// written down where it lives (<c>docs/23_PACKAGE_VERSIONING.md</c>, with the runtime cross-references in
/// <c>docs/08</c>/<c>docs/02</c> and the adopter pointer in <c>README.md</c>) AND that the one machine-checkable
/// value in the policy — the minimum deprecation window — equals the enforced code constant
/// (<see cref="ApiDeprecation.MinimumDeprecationWindowDays"/>), so the documented number and the enforced number
/// cannot drift apart. They read the checked-in markdown from the repository root (found by walking up to the
/// <c>LiveCore.slnx</c> solution file), so they are independent of any build output, mirroring
/// <c>ApiEvolutionPolicyDocTests</c> and <c>PrometheusExporterPinTests</c>.
/// </summary>
public class ApiStabilityPolicyDocTests
{
    /// <summary>The documented minimum deprecation window, read from the enforced code constant.</summary>
    private static readonly string _windowDays =
        ApiDeprecation.MinimumDeprecationWindowDays.ToString(CultureInfo.InvariantCulture) + " days";

    private static string ReadDoc(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveCore.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, Path.Combine(relativeSegments));
        Assert.True(File.Exists(path), $"Expected documentation file at {path}.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_versioning_doc_documents_the_stability_commitment_and_pre_1_0_posture()
    {
        var doc = ReadDoc("docs", "23_PACKAGE_VERSIONING.md");

        // The single policy section and its owning story.
        Assert.Contains("API and SDK stability policy and the path to 1.0", doc, StringComparison.Ordinal);
        Assert.Contains("CORE-REL-002", doc, StringComparison.Ordinal);

        // The public surface the commitment covers, and the pre-1.0 breaking-change posture.
        Assert.Contains("public surface", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-1.0", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_versioning_doc_states_what_declaring_1_0_means()
    {
        var doc = ReadDoc("docs", "23_PACKAGE_VERSIONING.md");

        Assert.Contains("What declaring 1.0 means", doc, StringComparison.Ordinal);
        // Declaring 1.0 means full SemVer kicks in: a breaking change then needs a MAJOR bump and a new API version.
        Assert.Contains("MAJOR", doc, StringComparison.Ordinal);
        Assert.Contains("/api/v2", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_versioning_doc_documents_the_window_value_matching_the_enforced_constant()
    {
        var doc = ReadDoc("docs", "23_PACKAGE_VERSIONING.md");

        // The machine-checkable policy value: the documented window equals the enforced code constant.
        Assert.Contains(_windowDays, doc, StringComparison.Ordinal); // "180 days"
        Assert.Contains("six months", doc, StringComparison.Ordinal);

        // And the doc ties the window to the RFC 8594 mechanism + the enforcing type.
        Assert.Contains("RFC 8594", doc, StringComparison.Ordinal);
        Assert.Contains("MinimumDeprecationWindow", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_mechanism_docs_tie_the_window_to_the_sunset_headers()
    {
        // The mechanism docs (where the Sunset/Deprecation headers are specified) carry the same enforced window.
        var apiContracts = ReadDoc("docs", "08_API_CONTRACTS.md");
        Assert.Contains(_windowDays, apiContracts, StringComparison.Ordinal);
        Assert.Contains("CORE-REL-002", apiContracts, StringComparison.Ordinal);

        var architecture = ReadDoc("docs", "02_ARCHITECTURE.md");
        Assert.Contains(_windowDays, architecture, StringComparison.Ordinal);
        Assert.Contains("CORE-REL-002", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_points_an_adopter_at_the_one_stability_policy()
    {
        var readme = ReadDoc("README.md");

        Assert.Contains("API and SDK stability policy and the path to 1.0", readme, StringComparison.Ordinal);
        Assert.Contains(_windowDays, readme, StringComparison.Ordinal);
        Assert.Contains("CORE-REL-002", readme, StringComparison.Ordinal);
    }
}
