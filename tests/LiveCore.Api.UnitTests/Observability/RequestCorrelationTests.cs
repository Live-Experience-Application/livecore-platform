// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Hosting;
using LiveCore.Api.Observability;
using Microsoft.AspNetCore.Http;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="RequestCorrelation"/> (CORE-OBS-005), the resolver of the single per-request
/// correlation id used both as the <c>X-Request-Id</c> response header and the <c>request_id</c> log scope key.
/// They pin the precedence (well-formed inbound client id, else the active trace id, else the framework id), the
/// fail-closed handling of a hostile inbound id (threat T7 / log forging), and the per-request caching that
/// guarantees the header and the log scope agree.
/// </summary>
public sealed class RequestCorrelationTests
{
    [Fact]
    public void It_honors_a_well_formed_inbound_request_id()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorsConfiguration.RequestIdHeaderName] = "client-Req.123_abc";

        Assert.Equal("client-Req.123_abc", RequestCorrelation.Resolve(context));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("new\nline")]
    [InlineData("semi;colon")]
    [InlineData("path/like")]
    public void It_rejects_a_malformed_inbound_request_id_and_falls_back(string hostileId)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "framework-id" };
        context.Request.Headers[CorsConfiguration.RequestIdHeaderName] = hostileId;

        // No active trace here, so the fail-closed fallback is the framework per-request id — never the hostile
        // caller-supplied value (so it can never forge a log line or smuggle content).
        Assert.Equal("framework-id", RequestCorrelation.Resolve(context));
    }

    [Fact]
    public void It_rejects_an_over_long_inbound_request_id_and_falls_back()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "framework-id" };
        context.Request.Headers[CorsConfiguration.RequestIdHeaderName] = new string('a', 129);

        Assert.Equal("framework-id", RequestCorrelation.Resolve(context));
    }

    [Fact]
    public void It_uses_the_active_trace_id_when_no_inbound_id_is_supplied()
    {
        using var listener = RecordingListener();
        using var source = new ActivitySource("LiveCore.Tests.RequestCorrelation");
        using var activity = source.StartActivity("unit", ActivityKind.Server);
        Assert.NotNull(activity);

        var context = new DefaultHttpContext { TraceIdentifier = "framework-id" };

        // The active trace id correlates a log line directly with the server trace, so it is preferred over the
        // opaque framework id.
        Assert.Equal(activity!.TraceId.ToString(), RequestCorrelation.Resolve(context));
    }

    [Fact]
    public void It_falls_back_to_the_framework_id_when_there_is_no_active_trace()
    {
        // Guarantee no ambient activity for this execution context, so the trace-id branch is not taken.
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = "framework-id" };

        Assert.Equal("framework-id", RequestCorrelation.Resolve(context));
    }

    [Fact]
    public void It_resolves_once_and_caches_so_the_header_and_the_log_scope_agree()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "framework-id" };

        var first = RequestCorrelation.Resolve(context);

        // A later header mutation must not change the already-resolved id: the response header and the log scope
        // are seeded by different middleware and must always carry the same value.
        context.Request.Headers[CorsConfiguration.RequestIdHeaderName] = "late-arrival";
        var second = RequestCorrelation.Resolve(context);

        Assert.Equal(first, second);
        Assert.Equal("framework-id", second);
    }

    private static ActivityListener RecordingListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "LiveCore.Tests.RequestCorrelation",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
