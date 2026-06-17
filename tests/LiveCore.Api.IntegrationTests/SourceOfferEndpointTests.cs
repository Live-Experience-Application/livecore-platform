// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Text.Json;
using LiveCore.Api.SystemModule;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the AGPL-3.0 section 13 source-offer surface (CORE-CMP-001,
/// CORE-LIC-005) — the story's required tests: the source-offer URL contains the
/// running version/revision, the offer matches the stamped build version, and a
/// modified deployment offers its own (revision-pinned) Corresponding Source. They
/// boot the real API host and assert that <c>GET /source</c> responds anonymously
/// with the license, the running build version and the REVISION-PINNED Corresponding
/// Source location, end-to-end. (The fail-closed startup refusal of a modified
/// deployment that left the repository unset is covered, against the real production
/// <c>Program</c>, by <c>SourceOfferStartupGuardTests</c> in the smoke suite.)
///
/// The host needs no database or identity provider: the source-offer surface is
/// registered unconditionally, exactly like the health and metrics endpoints.
/// </summary>
public sealed class SourceOfferEndpointTests
{
    [Fact]
    public async Task Source_offer_endpoint_returns_license_version_and_revision_pinned_source_location()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // The camelCase contract a remote user / vertical app consumes — only the
        // license, build version and source location, nothing else (threat T7).
        var propertyNames = root
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["license", "version", "sourceUrl"], propertyNames);

        // The license the section 13 offer is about.
        Assert.Equal(SourceOffer.LicenseId, root.GetProperty("license").GetString());

        // The build version is present (the Corresponding Source revision running) and
        // matches the single stamped build version the host reuses (ResolveBuildVersion).
        var version = root.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(SourceOffer.ResolveBuildVersion(), version);

        // The Corresponding Source location is offered — the canonical upstream
        // repository when no deployment-specific override is configured, PINNED to the
        // running revision's tag so it resolves to the exact version running (section
        // 13), never the bare repository root.
        var sourceUrl = root.GetProperty("sourceUrl").GetString();
        Assert.Equal($"{SourceOffer.DefaultSourceUrl}/tree/v{version}", sourceUrl);
        Assert.Contains(version!, sourceUrl!, StringComparison.Ordinal);
        Assert.NotEqual(SourceOffer.DefaultSourceUrl, sourceUrl);
    }

    [Fact]
    public async Task Source_offer_endpoint_is_reachable_without_authentication()
    {
        // The section 13 offer is owed to EVERY remote user the application interacts
        // with, so the endpoint is anonymous: an unauthenticated request is served,
        // not challenged with 401 (the story's "no auth required" criterion).
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Source_offer_endpoint_offers_a_modified_deployments_own_revision_pinned_source_location()
    {
        // A hosted deployment that ships MODIFIED source must offer ITS OWN
        // Corresponding Source (AGPL section 13), so the offered location is
        // configuration-overridable: GET /source then points at the source actually
        // running there rather than the canonical upstream — and is still PINNED to
        // the running revision on that repository.
        const string forkSourceUrl = "https://git.example.test/operator/livecore-fork";
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(SourceOffer.RepositoryUrlConfigurationKey, forkSourceUrl);
                builder.UseSetting(SourceOffer.ModifiedConfigurationKey, "true");
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        Assert.Equal($"{forkSourceUrl}/tree/v{version}", root.GetProperty("sourceUrl").GetString());
    }
}
