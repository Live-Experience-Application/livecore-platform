using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Http;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="MobileApiGateway.TryMapMobilePath"/> (CORE-MON-009) — the mobile <c>/v1</c> →
/// Core <c>/api/v1</c> route-prefix mapping the in-process gateway applies before routing. They pin that EVERY
/// documented mobile route (the mirror of <c>csv/mobile_store_api_routes.csv</c>) maps to its <c>/api/v1</c>
/// path (including the parameterized workspace quota-status route and a query string), and — the security
/// property — that the gateway is SCOPED: a non-documented <c>/v1</c> path, an already-<c>/api/v1</c> path and
/// near-miss shapes are NOT mapped, so the rest of the Core API is never aliased under a second prefix
/// (threats T1/T5). The end-to-end behavior (a documented mobile path reaches the endpoint, anonymous 401, a
/// non-documented /v1 path 404) is covered over real HTTP by the integration suite.
/// </summary>
public class MobileApiGatewayTests
{
    [Theory]
    [InlineData("/v1/me/entitlements", "/api/v1/me/entitlements")]
    [InlineData("/v1/me/quota-status", "/api/v1/me/quota-status")]
    [InlineData("/v1/me/ad-eligibility", "/api/v1/me/ad-eligibility")]
    [InlineData("/v1/purchases/apple/transactions", "/api/v1/purchases/apple/transactions")]
    [InlineData("/v1/purchases/google/tokens", "/api/v1/purchases/google/tokens")]
    [InlineData("/v1/store-notifications/apple", "/api/v1/store-notifications/apple")]
    [InlineData("/v1/store-notifications/google/rtdn", "/api/v1/store-notifications/google/rtdn")]
    public void TryMapMobilePath_maps_every_documented_mobile_route(string mobilePath, string expected)
    {
        var mapped = MobileApiGateway.TryMapMobilePath(new PathString(mobilePath), out var coreApiPath);

        Assert.True(mapped);
        Assert.Equal(expected, coreApiPath.Value);
    }

    [Theory]
    [InlineData("/v1/workspaces/11111111-1111-1111-1111-111111111111/quota-status",
        "/api/v1/workspaces/11111111-1111-1111-1111-111111111111/quota-status")]
    [InlineData("/v1/workspaces/any-segment/quota-status", "/api/v1/workspaces/any-segment/quota-status")]
    public void TryMapMobilePath_maps_the_parameterized_workspace_quota_status_route(string mobilePath, string expected)
    {
        // The single {workspaceId} segment matches any one non-empty segment; the endpoint validates the Guid.
        var mapped = MobileApiGateway.TryMapMobilePath(new PathString(mobilePath), out var coreApiPath);

        Assert.True(mapped);
        Assert.Equal(expected, coreApiPath.Value);
    }

    [Fact]
    public void TryMapMobilePath_is_case_insensitive_on_literal_segments()
    {
        // Routing matches path segments case-insensitively; the mapping mirrors that for the literal segments.
        var mapped = MobileApiGateway.TryMapMobilePath(new PathString("/v1/ME/Entitlements"), out var coreApiPath);

        Assert.True(mapped);
        Assert.Equal("/api/v1/ME/Entitlements", coreApiPath.Value);
    }

    [Theory]
    [InlineData("/v1/workspaces")] // not a documented mobile route
    [InlineData("/v1/organizations")] // not a documented mobile route
    [InlineData("/v1/sessions/11111111-1111-1111-1111-111111111111/reveal")] // not a documented mobile route
    [InlineData("/v1/me")] // the principal read is /api/v1/me but is not in the mobile shape
    [InlineData("/v1/me/entitlements/extra")] // near-miss: too many segments
    [InlineData("/v1/purchases/apple")] // near-miss: too few segments
    [InlineData("/v1/workspaces/abc/def/quota-status")] // near-miss: extra segment in the parameterized route
    [InlineData("/v1")] // the bare prefix is not a route
    [InlineData("/api/v1/me/entitlements")] // already a Core path: never double-prefixed
    [InlineData("/health/ready")] // unrelated path
    [InlineData("/")] // root
    public void TryMapMobilePath_does_not_map_a_non_documented_path(string path)
    {
        var mapped = MobileApiGateway.TryMapMobilePath(new PathString(path), out var coreApiPath);

        // Fail-safe: only the exact documented mobile shapes are aliased; everything else is left untouched and
        // still 404s, so the gateway never exposes the rest of the API under /v1 (threats T1/T5).
        Assert.False(mapped);
        Assert.False(coreApiPath.HasValue);
    }

    [Fact]
    public void TryMapMobilePath_does_not_map_an_empty_path()
    {
        var mapped = MobileApiGateway.TryMapMobilePath(default, out var coreApiPath);

        Assert.False(mapped);
        Assert.False(coreApiPath.HasValue);
    }
}
