using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="HubBearerToken"/> (CORE-RT-001) — the predicate that restricts
/// query-string bearer tokens to the SignalR hub path area. The boundary is security-relevant: a token
/// may be read from the query string ONLY for hub connections (so a token never leaks from a REST URL),
/// and the match must be on a segment boundary so a look-alike path is not mistaken for the hub area
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class HubBearerTokenTests
{
    [Theory]
    [InlineData("/hubs")]
    [InlineData("/hubs/session")]
    [InlineData("/hubs/session/negotiate")]
    [InlineData("/HUBS/session")] // case-insensitive
    [InlineData("/Hubs/Session")]
    public void IsHubPath_is_true_for_the_hub_area(string path)
        => Assert.True(HubBearerToken.IsHubPath(path));

    [Theory]
    [InlineData("/hubsignup")] // segment boundary: not the hub area
    [InlineData("/hubsession")]
    [InlineData("/api/v1/workspaces")]
    [InlineData("/api/v1/sessions/abc/reveal")]
    [InlineData("/")]
    [InlineData("/health/live")]
    public void IsHubPath_is_false_outside_the_hub_area(string path)
        => Assert.False(HubBearerToken.IsHubPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsHubPath_is_false_for_no_path(string? path)
        => Assert.False(HubBearerToken.IsHubPath(path));

    [Fact]
    public void The_query_parameter_is_access_token()
        => Assert.Equal("access_token", HubBearerToken.QueryParameter);

    [Fact]
    public void The_path_prefix_is_the_hubs_area()
        => Assert.Equal("/hubs", HubBearerToken.PathPrefix);
}
