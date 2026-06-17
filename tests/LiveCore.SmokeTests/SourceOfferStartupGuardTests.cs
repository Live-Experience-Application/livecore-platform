// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Text.Json;
using LiveCore.Api.SystemModule;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.SmokeTests;

/// <summary>
/// AGPL section 13 source-offer startup-guard tests (CORE-LIC-005) over the real production <c>Program</c> with
/// no test seams: only the <c>SourceOffer:*</c> settings are configured.
///
/// They pin the acceptance criterion that a MODIFIED deployment cannot silently keep offering the canonical
/// UPSTREAM source. The documented rule (docs/16_LICENSING.md): a deployment that declares modified source
/// (<see cref="SourceOffer.ModifiedConfigurationKey"/>) MUST also name its own Corresponding Source
/// (<see cref="SourceOffer.RepositoryUrlConfigurationKey"/>), and a configured repository must be an absolute
/// http(s) URL. When that rule is violated the host REFUSES TO START rather than serving a wrong or
/// non-fetchable section 13 offer — the same refuse-rather-than-serve-a-wrong-value posture as the OIDC
/// audience startup guard (CORE-OPS-004, <see cref="OidcAudienceStartupGuardTests"/>). An unmodified deployment
/// (the canonical default) always starts and offers the revision-pinned upstream source.
/// </summary>
public class SourceOfferStartupGuardTests
{
    private const string _forkRepository = "https://git.example.test/operator/livecore-fork";

    private static WebApplicationFactory<Program> CreateFactory(string? modified, string? repositoryUrl)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (modified is not null)
            {
                builder.UseSetting(SourceOffer.ModifiedConfigurationKey, modified);
            }

            if (repositoryUrl is not null)
            {
                builder.UseSetting(SourceOffer.RepositoryUrlConfigurationKey, repositoryUrl);
            }
        });
    }

    [Fact]
    public void Host_refuses_to_start_when_a_modified_deployment_left_the_repository_unset()
    {
        using var factory = CreateFactory(modified: "true", repositoryUrl: null);

        // Touching Services builds the real host, which runs the source-offer startup guard.
        var exception = Record.Exception(() => _ = factory.Services);

        // The host refuses to start rather than silently offering the canonical upstream source as this
        // modified deployment's Corresponding Source.
        Assert.NotNull(exception);
        Assert.Contains(SourceOffer.RepositoryUrlConfigurationKey, Flatten(exception!), StringComparison.Ordinal);
    }

    [Fact]
    public void Host_refuses_to_start_when_the_configured_repository_is_not_an_absolute_http_url()
    {
        using var factory = CreateFactory(modified: null, repositoryUrl: "not-a-url");

        var exception = Record.Exception(() => _ = factory.Services);

        // A malformed repository would offer a location no remote user can fetch, so the host refuses to start.
        Assert.NotNull(exception);
        Assert.Contains(SourceOffer.RepositoryUrlConfigurationKey, Flatten(exception!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_starts_and_offers_the_revision_pinned_canonical_source_when_unconfigured()
    {
        // The canonical unmodified default always starts and offers the upstream source pinned to the running
        // revision (never the bare repo root).
        using var factory = CreateFactory(modified: null, repositoryUrl: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sourceUrl = await ReadSourceUrlAsync(response);
        Assert.StartsWith($"{SourceOffer.DefaultSourceUrl}/tree/v", sourceUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_starts_and_offers_a_modified_deployments_own_revision_pinned_source()
    {
        // A modified deployment that DOES name its own repository is valid: it starts and offers that
        // repository, pinned to the running revision.
        using var factory = CreateFactory(modified: "true", repositoryUrl: _forkRepository);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sourceUrl = await ReadSourceUrlAsync(response);
        Assert.StartsWith($"{_forkRepository}/tree/v", sourceUrl, StringComparison.Ordinal);
    }

    private static async Task<string?> ReadSourceUrlAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("sourceUrl").GetString();
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
