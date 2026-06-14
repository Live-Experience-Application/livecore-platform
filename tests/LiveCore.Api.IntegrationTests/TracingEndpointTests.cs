using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Observability;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the distributed-tracing hooks (CORE-OBS-003) — the story's required test: spans are
/// emitted for a sample flow, end-to-end through the real application. They boot the real API host
/// (<see cref="WorkspaceApiFactory"/>: test auth + EF Core SQLite), attach a real
/// <see cref="ActivityListener"/> over the <see cref="LiveCoreActivitySource.SourceName"/> source (the same
/// subscription mechanism a collector's exporter uses) and drive the reveal/publish "sample flow" over real
/// HTTP, then assert the produced spans.
///
/// The headline test proves the WHOLE causal chain the docs/15_OBSERVABILITY.md tracing surface promises for a
/// single request: the SERVER request span (the "Key request flow"), the reveal span and the session-event
/// publish span are all produced and share one trace (request -> reveal -> publish), so a collector
/// reconstructs the request's span tree. Correlation is by trace id, so the assertions stay reliable even when
/// other integration tests produce Core spans in parallel (the source name is process-global).
/// </summary>
public sealed class TracingEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _revealRoute = "/api/v1/sessions/{sessionId}/reveal";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_reveal_produces_the_request_reveal_and_publish_spans_in_one_trace()
    {
        await using var factory = new WorkspaceApiFactory();
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedLiveSessionAsync(factory);

        using var capture = new ActivityCapture();
        using var client = factory.CreateClientFor("host-a", _issuer, _orgA);

        var response = await PostRevealAsync(client, seed.SessionId, resourceId, "key-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Find a single trace carrying the full reveal chain: the SERVER request span, the reveal span and the
        // session-event publish span. The reveal emits two durable events (ContentRevealed +
        // VisibilityRuleChanged), so at least one publish span is produced under the reveal span.
        var chain = await capture.WaitForTraceAsync(spans =>
            spans.Any(s => s.OperationName == "http.server.request" && s.Kind == ActivityKind.Server)
            && spans.Any(s => s.OperationName == "livecore.reveal")
            && spans.Any(s => s.OperationName == "livecore.session_event.publish"));

        // A non-empty result means a single trace carried all three spans (WaitForTraceAsync returns the empty
        // list on timeout, so this fails closed if the chain never formed).
        Assert.NotEmpty(chain);

        var request = chain.Single(s => s.OperationName == "http.server.request");
        var reveal = chain.Single(s => s.OperationName == "livecore.reveal");
        var publish = chain.First(s => s.OperationName == "livecore.session_event.publish");

        // The request span is tagged with the route TEMPLATE (never the concrete path; threat T7).
        Assert.Equal(_revealRoute, TagValue(request, "http.route"));
        Assert.Equal("POST", TagValue(request, "http.request.method"));

        // The reveal span is a child of the request span, and each publish span is a child of the reveal span —
        // the request -> reveal -> publish causal tree a collector reconstructs.
        Assert.Equal(request.SpanId, reveal.ParentSpanId);
        Assert.Equal(reveal.SpanId, publish.ParentSpanId);

        // The publish span carries the stable event-type name (one of the two events a reveal emits), never the
        // payload (threat T7).
        Assert.Contains(TagValue(publish, "livecore.event.type"), new[] { "ContentRevealed", "VisibilityRuleChanged" });
    }

    [Fact]
    public async Task The_metrics_scrape_endpoint_produces_no_request_span()
    {
        // The tracing middleware skips the frequently-polled infrastructure endpoints, so /metrics never
        // produces a request span. This holds globally (no test produces one), so it is robust under parallel
        // capture.
        await using var factory = new WorkspaceApiFactory();
        using var capture = new ActivityCapture();
        using var client = factory.CreateClientFor("host-a", _issuer, _orgA);

        using var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();

        // Bounded poll instead of a fixed delay (CORE-TST-005): the former Task.Delay(50) merely hoped any
        // rogue /metrics span would surface within an arbitrary window. After the /metrics request has fully
        // completed, drive a request to a TRACED route and wait — using the same WaitForTraceAsync bounded-poll
        // pattern — until a request span surfaces. Span completion is recorded synchronously in stop order, so
        // once that barrier span is observed, any span the already-awaited /metrics request might have produced
        // would already be captured. The barrier matches any request span OTHER than /metrics, so it is robust
        // to the barrier route's exact template and to spans other tests capture in parallel.
        using var barrier = await client.GetAsync("/api/v1/me");
        var barrierSpans = await capture.WaitForTraceAsync(spans =>
            spans.Any(s =>
                s.OperationName == "http.server.request"
                && TagValue(s, "http.route") is { } route
                && route != "/metrics"));
        Assert.NotEmpty(barrierSpans);

        Assert.DoesNotContain(
            capture.Snapshot(),
            s => s.OperationName == "http.server.request" && TagValue(s, "http.route") == "/metrics");
    }

    private static string? TagValue(Activity activity, string key)
        => activity.TagObjects.FirstOrDefault(tag => tag.Key == key).Value?.ToString();

    private static async Task<SeedResult> SeedLiveSessionAsync(WorkspaceApiFactory factory)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, "host-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        Guid resourceId,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    /// <summary>
    /// A real <see cref="ActivityListener"/> over the <see cref="LiveCoreActivitySource.SourceName"/> source
    /// that records every completed span, sampling each as recorded so spans are created even without an
    /// exporter. Used to observe the spans the host produces end-to-end.
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Lock _gate = new();
        private readonly List<Activity> _stopped = [];

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == LiveCoreActivitySource.SourceName,
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

        public IReadOnlyList<Activity> Snapshot()
        {
            lock (_gate)
            {
                return _stopped.ToList();
            }
        }

        /// <summary>
        /// Polls the captured spans until one trace's spans satisfy <paramref name="isComplete"/>, returning
        /// that trace's spans (or the empty list on timeout, which fails the caller's NotNull assertion).
        /// </summary>
        public async Task<IReadOnlyList<Activity>> WaitForTraceAsync(Func<IReadOnlyList<Activity>, bool> isComplete)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                foreach (var trace in Snapshot().GroupBy(span => span.TraceId))
                {
                    var spans = trace.ToList();
                    if (isComplete(spans))
                    {
                        return spans;
                    }
                }

                await Task.Delay(20);
            }

            return [];
        }

        public void Dispose() => _listener.Dispose();
    }
}
