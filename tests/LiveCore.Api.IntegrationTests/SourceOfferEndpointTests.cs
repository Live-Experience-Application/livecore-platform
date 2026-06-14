using System.Net;
using System.Text.Json;
using LiveCore.Api.SystemModule;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the AGPL-3.0 section 13 source-offer surface (CORE-CMP-001)
/// — the story's required test: "the source-offer endpoint returns the source
/// location + build version; no auth required". They boot the real API host and
/// assert that <c>GET /source</c> responds anonymously with the license, the running
/// build version and the Corresponding Source location, end-to-end.
///
/// The host needs no database or identity provider: the source-offer surface is
/// registered unconditionally, exactly like the health and metrics endpoints.
/// </summary>
public sealed class SourceOfferEndpointTests
{
    [Fact]
    public async Task Source_offer_endpoint_returns_license_version_and_source_location()
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

        // The build version is present (the Corresponding Source revision running).
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));

        // The Corresponding Source location is offered — the canonical upstream
        // repository when no deployment-specific override is configured.
        Assert.Equal(SourceOffer.DefaultSourceUrl, root.GetProperty("sourceUrl").GetString());
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
    public async Task Source_offer_endpoint_offers_a_modified_deployments_own_source_location()
    {
        // A hosted deployment that ships MODIFIED source must offer ITS OWN
        // Corresponding Source (AGPL section 13), so the offered location is
        // configuration-overridable: GET /source then points at the source actually
        // running there rather than the canonical upstream.
        const string forkSourceUrl = "https://git.example.test/operator/livecore-fork";
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting(SourceOffer.RepositoryUrlConfigurationKey, forkSourceUrl));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(forkSourceUrl, document.RootElement.GetProperty("sourceUrl").GetString());
    }
}
