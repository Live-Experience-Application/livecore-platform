using System.Globalization;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Http;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for the API deprecation/sunset mechanism (CORE-DX-006): the <see cref="DeprecationNotice"/>
/// schedule, the <see cref="ApiDeprecation.ApplyDeprecationHeaders"/> writer (the RFC 8594 <c>Sunset</c> header
/// and the <c>Deprecation</c> header in both its boolean and dated forms) and the
/// <see cref="DeprecationHeaderMiddleware"/> (a notice on the matched endpoint emits the headers; a current route
/// with no notice emits neither). The end-to-end behavior over real HTTP — a flagged route returns the headers —
/// is covered by the integration suite; the CORS exposure is covered by <see cref="CorsConfigurationTests"/> and
/// the integration suite.
/// </summary>
public class ApiDeprecationTests
{
    private static readonly DateTimeOffset _sunsetAt = new(2031, 12, 31, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset _deprecatedSince = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeprecationNotice_requires_a_real_sunset_instant()
    {
        Assert.Throws<ArgumentException>(() => new DeprecationNotice(default));
    }

    [Fact]
    public void DeprecationNotice_rejects_a_deprecation_at_or_after_the_sunset()
    {
        Assert.Throws<ArgumentException>(() => new DeprecationNotice(_sunsetAt, _sunsetAt));
        Assert.Throws<ArgumentException>(() => new DeprecationNotice(_sunsetAt, _sunsetAt.AddDays(1)));
    }

    [Fact]
    public void DeprecationNotice_keeps_the_schedule()
    {
        var notice = new DeprecationNotice(_sunsetAt, _deprecatedSince);

        Assert.Equal(_sunsetAt, notice.SunsetAt);
        Assert.Equal(_deprecatedSince, notice.DeprecatedSince);
    }

    [Fact]
    public void ApplyDeprecationHeaders_writes_the_boolean_deprecation_and_the_rfc8594_sunset()
    {
        var context = new DefaultHttpContext();
        var notice = new DeprecationNotice(_sunsetAt);

        ApiDeprecation.ApplyDeprecationHeaders(context.Response, notice);

        // No deprecation date known: the Deprecation header is the boolean token "true".
        Assert.Equal("true", context.Response.Headers["Deprecation"].ToString());

        // Sunset is an IMF-fixdate (RFC 8594 / RFC 7231) in GMT that round-trips to the same instant.
        var sunset = context.Response.Headers["Sunset"].ToString();
        Assert.EndsWith("GMT", sunset, StringComparison.Ordinal);
        Assert.Equal(_sunsetAt, DateTimeOffset.ParseExact(sunset, "R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ApplyDeprecationHeaders_writes_the_dated_deprecation_when_a_date_is_known()
    {
        var context = new DefaultHttpContext();
        var notice = new DeprecationNotice(_sunsetAt, _deprecatedSince);

        ApiDeprecation.ApplyDeprecationHeaders(context.Response, notice);

        var deprecation = context.Response.Headers["Deprecation"].ToString();
        Assert.EndsWith("GMT", deprecation, StringComparison.Ordinal);
        Assert.Equal(_deprecatedSince, DateTimeOffset.ParseExact(deprecation, "R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Middleware_emits_the_headers_for_an_endpoint_flagged_deprecated()
    {
        var context = new DefaultHttpContext();
        var notice = new DeprecationNotice(_sunsetAt, _deprecatedSince);
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            metadata: new EndpointMetadataCollection(notice),
            displayName: "deprecated-test-endpoint"));

        var nextRan = false;
        var middleware = new DeprecationHeaderMiddleware(_ =>
        {
            nextRan = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextRan);
        Assert.True(context.Response.Headers.ContainsKey("Deprecation"));
        Assert.True(context.Response.Headers.ContainsKey("Sunset"));
    }

    [Fact]
    public async Task Middleware_emits_nothing_for_an_endpoint_without_a_notice()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            metadata: EndpointMetadataCollection.Empty,
            displayName: "current-test-endpoint"));

        var middleware = new DeprecationHeaderMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        // A current (non-deprecated) route must not carry the headers — the signal is strictly opt-in.
        Assert.False(context.Response.Headers.ContainsKey("Deprecation"));
        Assert.False(context.Response.Headers.ContainsKey("Sunset"));
    }

    [Fact]
    public async Task Middleware_emits_nothing_when_no_endpoint_matched()
    {
        var context = new DefaultHttpContext();

        var middleware = new DeprecationHeaderMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Deprecation"));
        Assert.False(context.Response.Headers.ContainsKey("Sunset"));
    }
}
