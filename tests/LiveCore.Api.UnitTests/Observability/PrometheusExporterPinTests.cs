// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Dependency-pin assertions for the prerelease Prometheus exporter (CORE-OBS-004). The API's
/// <c>GET /metrics</c> scrape endpoint (CORE-OBS-001) is served by
/// <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>, which has NO stable release — every published version is
/// a prerelease (<c>alpha</c>/<c>beta</c>/<c>rc</c>) and the latest is the pinned <c>1.16.0-beta.1</c> itself. So
/// the prerelease use is not silently ridden: it is recorded as an explicit dated, owner-named decision with an
/// upgrade trigger (CORE-CMP-002 / CORE-OBS-004), and THESE tests make that record enforceable. They assert the
/// pinned version in <c>apps/api/LiveCore.Api.csproj</c> and the resolved version in the locked
/// <c>apps/api/packages.lock.json</c> are both the recorded prerelease, and that the dated decision and its
/// upgrade trigger are written down in the csproj justification, <c>docs/24_SPEC_CONSISTENCY.md</c> (the dated
/// decision register) and <c>docs/15_OBSERVABILITY.md</c>.
///
/// The point is the <b>upgrade trigger</b>: a silent bump of the pin (to a different prerelease or to a future
/// stable release) — or quietly dropping the decision from the docs — fails this build, forcing the change to go
/// through the recorded decision. They read the checked-in files from the repository root (found by walking up to
/// the <c>LiveCore.slnx</c> solution file), so they are independent of any build output and never boot the host,
/// mirroring <c>ApiEvolutionPolicyDocTests</c>. The metrics endpoint behavior itself is covered by
/// <c>MetricsEndpointTests</c> and is unchanged by this story.
/// </summary>
public class PrometheusExporterPinTests
{
    private const string _packageId = "OpenTelemetry.Exporter.Prometheus.AspNetCore";

    /// <summary>
    /// The recorded prerelease pin. When the upgrade trigger fires (a stable release is published and adopted),
    /// this constant, the csproj pin, the lock file and the dated decision in docs/15 and docs/24 all change
    /// together — that is the whole point of tracking the pin here.
    /// </summary>
    private const string _recordedPin = "1.16.0-beta.1";

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveCore.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!;
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var path = Path.Combine(RepositoryRoot().FullName, Path.Combine(relativeSegments));
        Assert.True(File.Exists(path), $"Expected repository file at {path}.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_prometheus_exporter_is_pinned_to_the_recorded_prerelease_in_the_csproj()
    {
        var csproj = ReadRepositoryFile("apps", "api", "LiveCore.Api.csproj");

        var match = Regex.Match(
            csproj,
            $"Include=\"{Regex.Escape(_packageId)}\"\\s+Version=\"([^\"]+)\"");

        Assert.True(match.Success, $"Expected a <PackageReference Include=\"{_packageId}\" .../> in the API csproj.");

        var pinnedVersion = match.Groups[1].Value;
        Assert.Equal(_recordedPin, pinnedVersion);

        // It is a PRERELEASE — the fact the decision exists to track. A stable adoption must update this record.
        Assert.Contains("-beta", pinnedVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void The_locked_restore_resolves_the_same_recorded_prerelease()
    {
        var lockFile = ReadRepositoryFile("apps", "api", "packages.lock.json");

        // The resolved version is recorded BEFORE the nested "dependencies" object in the package's lock block,
        // so the non-greedy span up to the first "resolved" never crosses a closing brace.
        var match = Regex.Match(
            lockFile,
            $"\"{Regex.Escape(_packageId)}\":\\s*\\{{[^}}]*?\"resolved\":\\s*\"([^\"]+)\"");

        Assert.True(match.Success, $"Expected a locked entry for {_packageId} in apps/api/packages.lock.json.");
        Assert.Equal(_recordedPin, match.Groups[1].Value);
    }

    [Fact]
    public void The_csproj_records_the_dated_decision_and_the_upgrade_trigger()
    {
        var csproj = ReadRepositoryFile("apps", "api", "LiveCore.Api.csproj");

        Assert.Contains("CORE-OBS-004", csproj, StringComparison.Ordinal);
        Assert.Contains(_recordedPin, csproj, StringComparison.Ordinal);
        Assert.Contains("UPGRADE TRIGGER", csproj, StringComparison.Ordinal);
        // The trigger names the concrete event that retires the pin: a stable release of THIS package.
        Assert.Contains("FIRST STABLE", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spec_consistency_register_records_the_dated_owner_named_decision_and_trigger()
    {
        var doc = ReadRepositoryFile("docs", "24_SPEC_CONSISTENCY.md");

        Assert.Contains("(CORE-OBS-004)", doc, StringComparison.Ordinal);
        Assert.Contains("Decision (2026-06-18", doc, StringComparison.Ordinal);
        Assert.Contains("owner: Core-later", doc, StringComparison.Ordinal);
        Assert.Contains("Upgrade trigger", doc, StringComparison.Ordinal);
        Assert.Contains("first STABLE", doc, StringComparison.Ordinal);
        Assert.Contains(_recordedPin, doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_observability_doc_references_the_dated_decision_and_pin()
    {
        var doc = ReadRepositoryFile("docs", "15_OBSERVABILITY.md");

        Assert.Contains("CORE-OBS-004", doc, StringComparison.Ordinal);
        Assert.Contains(_recordedPin, doc, StringComparison.Ordinal);
        Assert.Contains("first STABLE", doc, StringComparison.Ordinal);
    }
}
