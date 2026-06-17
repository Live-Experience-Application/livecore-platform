// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.UnitTests.Contracts;

/// <summary>
/// Cross-language contract test for the Problem Details error-code catalog (CORE-DX-001).
///
/// <para>
/// The server is the source of truth: <see cref="ProblemCodes"/> (apps/api/ProblemCodes.cs) is the catalog
/// every Problem Details response draws its <c>code</c> member from. That catalog is published to consumers
/// as the <c>ProblemCodes</c> runtime tuple in <c>@livecore/contracts</c>
/// (packages/contracts/src/problem-details.ts). This test reads the published TypeScript source and asserts
/// it lists EXACTLY the server catalog, in the same order — so the two can never drift: adding,
/// removing, renaming or reordering a code on either side fails here until the other side matches.
/// </para>
/// </summary>
public sealed class ProblemCodeCatalogContractTests
{
    [Fact]
    public void The_published_contracts_enum_matches_the_server_catalog_exactly()
    {
        var published = ReadPublishedProblemCodes();

        Assert.Equal(ProblemCodes.All.ToArray(), published);
    }

    [Fact]
    public void The_three_distinct_409_codes_are_each_published_and_distinct()
    {
        var published = ReadPublishedProblemCodes();

        // The acceptance criterion: the three structurally-different 409s are distinguishable by code.
        var distinct409s = new[]
        {
            ProblemCodes.QuotaExceeded,
            ProblemCodes.WorkspaceArchived,
            ProblemCodes.ConcurrencyConflict,
        };

        Assert.Equal(3, distinct409s.Distinct(StringComparer.Ordinal).Count());
        Assert.All(distinct409s, code => Assert.Contains(code, published));
    }

    /// <summary>
    /// Parses the ordered list of string literals from the
    /// <c>export const ProblemCodes = [ ... ] as const;</c> block of the published TypeScript contract.
    /// </summary>
    private static IReadOnlyList<string> ReadPublishedProblemCodes()
    {
        var source = File.ReadAllText(
            LocateRepositoryFile("packages/contracts/src/problem-details.ts"));

        var block = Regex.Match(
            source,
            @"export const ProblemCodes = \[(?<body>.*?)\] as const;",
            RegexOptions.Singleline);
        Assert.True(block.Success, "could not find the published ProblemCodes tuple in the contracts source");

        // Every string literal in the block is a code; the JSDoc comments carry no double-quoted text.
        return Regex.Matches(block.Groups["body"].Value, "\"(?<code>[a-z_]+)\"")
            .Select(match => match.Groups["code"].Value)
            .ToArray();
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repository root (the directory that contains
    /// <paramref name="relativePath"/>), so the test finds the published contract regardless of the working
    /// directory. The repository is checked out wherever the suite runs (CI and locally).
    /// </summary>
    private static string LocateRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }
}
