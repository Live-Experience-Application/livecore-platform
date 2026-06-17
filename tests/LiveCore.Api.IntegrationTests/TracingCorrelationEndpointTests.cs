// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using LiveCore.Api.Hosting;
using LiveCore.Api.Observability;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the distributed-tracing auto-instrumentation and the correlation-id response headers
/// (CORE-OBS-005) — the story's required tests, end-to-end through the real application
/// (<see cref="WorkspaceApiFactory"/>: test auth + EF Core SQLite). They cover all four acceptance points:
/// <list type="bullet">
///   <item>a request trace contains CHILD spans for a database call and an outbound HTTP call (the ASP.NET
///   Core, EF Core and HttpClient auto-instrumentations nest under the request span);</item>
///   <item>the response carries the request/trace id header (<c>X-Request-Id</c> + W3C <c>traceparent</c>);</item>
///   <item>that id is the same value the request's log scope carries as <c>request_id</c>;</item>
///   <item>an inbound <c>traceparent</c> is honored — the server adopts the caller's trace and returns it.</item>
/// </list>
/// </summary>
public sealed partial class TracingCorrelationEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _org = "northwind-labs";
    private const string _subject = "8b1f0a2c-3d4e-4f5a-9b6c-7d8e9f0a1b2c";

    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    private static partial Regex W3CTraceParent();

    [Fact]
    public async Task A_request_trace_contains_child_spans_for_a_db_call_and_an_outbound_http_call()
    {
        // A real loopback HTTP target so the server makes a genuine outbound HttpClient call over a real socket
        // (the in-memory test transport would not produce a System.Net.Http client span).
        await using var target = await LoopbackHttpTarget.StartAsync();
        await using var factory = new TraceFanoutApiFactory(target.RequestUrl);

        using var capture = new SpanCapture();
        using var client = factory.CreateClientFor(_subject, _issuer, _org);

        using var response = await client.GetAsync(TraceFanoutApiFactory.FanoutPath);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The fan-out endpoint echoes the active trace id, so the assertions correlate on exactly this request's
        // trace even though the process-global capture sees spans other tests produce in parallel.
        var traceId = Assert.Single(response.Headers.GetValues(TraceFanoutApiFactory.TraceIdHeader));

        // Wait until the request's trace carries the outbound HTTP span, its parent request span and a sibling
        // database span (WaitForTraceAsync returns the empty list on timeout, failing the NotEmpty assertion).
        var spans = await capture.WaitForTraceAsync(traceId, trace =>
        {
            var http = trace.FirstOrDefault(IsOutboundHttpSpan);
            if (http is null)
            {
                return false;
            }

            var request = trace.FirstOrDefault(span => span.SpanId == http.ParentSpanId);
            return request is { Kind: ActivityKind.Server }
                && trace.Any(span => IsDatabaseSpan(span, request.SpanId));
        });
        Assert.NotEmpty(spans);

        var httpSpan = spans.First(IsOutboundHttpSpan);
        var requestSpan = spans.Single(span => span.SpanId == httpSpan.ParentSpanId);
        var dbSpan = spans.First(span => IsDatabaseSpan(span, requestSpan.SpanId));

        // The request span is the framework SERVER span; the DB and outbound-HTTP spans are its children, all in
        // the one trace — the request -> {db, http} tree a collector reconstructs.
        Assert.Equal(ActivityKind.Server, requestSpan.Kind);
        Assert.Equal(ActivityKind.Client, dbSpan.Kind);
        Assert.Equal(ActivityKind.Client, httpSpan.Kind);
        Assert.Equal(requestSpan.SpanId, dbSpan.ParentSpanId);
        Assert.Equal(requestSpan.SpanId, httpSpan.ParentSpanId);
        Assert.Equal(httpSpan.TraceId, dbSpan.TraceId);
        Assert.Equal(httpSpan.TraceId, requestSpan.TraceId);
    }

    [Fact]
    public async Task The_response_carries_the_request_id_header_and_it_matches_the_log_scope()
    {
        await using var factory = new LogCapturingApiFactory();

        var workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _subject);
            var org = await db.AddOrganizationAsync(_org);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(_subject, _issuer, _org);

        using var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_org}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The response carries the correlation id and the W3C trace context.
        var requestId = Assert.Single(response.Headers.GetValues(CorsConfiguration.RequestIdHeaderName));
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        var traceParent = Assert.Single(response.Headers.GetValues(CorsConfiguration.TraceParentHeaderName));
        Assert.Matches(W3CTraceParent(), traceParent);
        // With no inbound client id the correlation id IS the trace id, so it is the traceparent's trace-id field.
        Assert.Equal(TraceIdOf(traceParent), requestId);

        // The SAME id appears in the request's log scope as request_id, so a caller that reads X-Request-Id off
        // the response can find the matching server log lines.
        var summary = Assert.Single(
            factory.LogProvider.Entries,
            entry => entry.Message.StartsWith("Handled request", StringComparison.Ordinal));
        Assert.True(summary.ScopeValues.TryGetValue(RequestLogContext.RequestIdKey, out var loggedRequestId));
        Assert.Equal(requestId, loggedRequestId?.ToString());
    }

    [Fact]
    public async Task An_inbound_traceparent_is_honored_and_returned_in_the_correlation_headers()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var inboundTraceId = ActivityTraceId.CreateRandom();
        var inboundSpanId = ActivitySpanId.CreateRandom();
        var inboundTraceParent = $"00-{inboundTraceId}-{inboundSpanId}-01";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_org}");
        request.Headers.TryAddWithoutValidation(CorsConfiguration.TraceParentHeaderName, inboundTraceParent);

        using var response = await client.SendAsync(request);

        // The route requires authorization, so an anonymous caller is challenged with 401 — but the correlation
        // headers ride on the 401 too (the middleware runs before authentication and writes from OnStarting).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The server adopted the caller's trace: the returned trace id is the inbound one, end-to-end.
        var requestId = Assert.Single(response.Headers.GetValues(CorsConfiguration.RequestIdHeaderName));
        Assert.Equal(inboundTraceId.ToString(), requestId);

        var traceParent = Assert.Single(response.Headers.GetValues(CorsConfiguration.TraceParentHeaderName));
        Assert.Matches(W3CTraceParent(), traceParent);
        Assert.Equal(inboundTraceId.ToString(), TraceIdOf(traceParent));
        // It is a NEW server span under the caller's trace, not an echo of the caller's span id.
        Assert.NotEqual(inboundSpanId.ToString(), SpanIdOf(traceParent));
    }

    /// <summary>The outbound HTTP client span is the runtime's <c>System.Net.Http</c> client activity.</summary>
    private static bool IsOutboundHttpSpan(Activity span)
        => span.Kind == ActivityKind.Client
            && string.Equals(span.Source.Name, "System.Net.Http", StringComparison.Ordinal);

    /// <summary>
    /// The database span is the OTHER client child of the request span (the EF Core command), identified
    /// structurally rather than by an instrumentation-version-specific source name or tag.
    /// </summary>
    private static bool IsDatabaseSpan(Activity span, ActivitySpanId requestSpanId)
        => span.Kind == ActivityKind.Client
            && span.ParentSpanId == requestSpanId
            && !IsOutboundHttpSpan(span);

    private static string TraceIdOf(string traceParent) => traceParent.Split('-')[1];

    private static string SpanIdOf(string traceParent) => traceParent.Split('-')[2];

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that mounts a single test-only endpoint
    /// (<see cref="FanoutPath"/>) which, while the framework request span is current, issues a database command
    /// and an outbound HTTP call — so the request trace fans out into a DB child span and an HTTP child span. The
    /// endpoint is a terminal middleware added through an <see cref="IStartupFilter"/>; production routing is
    /// untouched.
    /// </summary>
    private sealed class TraceFanoutApiFactory : WorkspaceApiFactory
    {
        public const string FanoutPath = "/test/trace-fanout";
        public const string TraceIdHeader = "X-Test-Trace-Id";

        private readonly string _outboundUrl;

        public TraceFanoutApiFactory(string outboundUrl) => _outboundUrl = outboundUrl;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TraceFanoutStartupFilter(_outboundUrl)));
        }

        private sealed class TraceFanoutStartupFilter : IStartupFilter
        {
            private readonly string _outboundUrl;

            public TraceFanoutStartupFilter(string outboundUrl) => _outboundUrl = outboundUrl;

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                // Resolve a scope factory from the application root so the endpoint has a DbContext regardless of
                // where in the pipeline this terminal middleware sits.
                var scopeFactory = app.ApplicationServices.GetRequiredService<IServiceScopeFactory>();

                app.Use(async (context, nextDelegate) =>
                {
                    if (context.Request.Path.Value != FanoutPath)
                    {
                        await nextDelegate();
                        return;
                    }

                    // Activity.Current here is the framework request (server) span, so the DB and HTTP spans
                    // nest under it. Echo the trace id so the test can correlate the captured spans.
                    context.Response.Headers[TraceIdHeader] = Activity.Current?.TraceId.ToString();

                    using (var scope = scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
                        await db.Database.ExecuteSqlRawAsync("SELECT 1");
                    }

                    using var http = new HttpClient();
                    using var outbound = await http.GetAsync(_outboundUrl);

                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                });

                next(app);
            };
        }
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that also captures the structured log scopes (reusing
    /// <see cref="CapturingLoggerProvider"/>), so a test can assert the response correlation id equals the
    /// request's logged <c>request_id</c>.
    /// </summary>
    private sealed class LogCapturingApiFactory : WorkspaceApiFactory
    {
        public CapturingLoggerProvider LogProvider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(LogProvider));
        }
    }

    /// <summary>
    /// A minimal real Kestrel server on a loopback port that answers any GET with 200, so the API under test can
    /// make a genuine outbound HTTP call over a real socket (which is what produces an instrumented
    /// <c>System.Net.Http</c> client span; the in-memory test transport would not).
    /// </summary>
    private sealed class LoopbackHttpTarget : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public string RequestUrl { get; }

        private LoopbackHttpTarget(WebApplication app, string requestUrl)
        {
            _app = app;
            RequestUrl = requestUrl;
        }

        public static async Task<LoopbackHttpTarget> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var app = builder.Build();
            app.MapGet("/{**catchAll}", () => Results.Ok());
            await app.StartAsync();

            var address = app.Services
                .GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();

            return new LoopbackHttpTarget(app, $"{address.TrimEnd('/')}/ping");
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>
    /// A process-wide <see cref="ActivityListener"/> that records every completed span (sampling each as
    /// recorded), so the test observes the auto-instrumentation spans across all sources (the framework, EF Core
    /// and HttpClient) the OpenTelemetry pipeline produces. Assertions correlate by trace id to stay reliable
    /// under parallel capture.
    /// </summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Lock _gate = new();
        private readonly List<Activity> _stopped = [];

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static _ => true,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task<IReadOnlyList<Activity>> WaitForTraceAsync(
            string traceId,
            Func<IReadOnlyList<Activity>, bool> isComplete)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                List<Activity> trace;
                lock (_gate)
                {
                    trace = _stopped.Where(span => span.TraceId.ToString() == traceId).ToList();
                }

                if (trace.Count > 0 && isComplete(trace))
                {
                    return trace;
                }

                await Task.Delay(20);
            }

            return [];
        }

        public void Dispose() => _listener.Dispose();
    }
}
