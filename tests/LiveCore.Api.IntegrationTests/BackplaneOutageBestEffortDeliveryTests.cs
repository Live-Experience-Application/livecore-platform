using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the genuinely best-effort realtime delivery of CORE-RES-001. They boot the real
/// application (<see cref="WorkspaceApiFactory"/>: real persistence over EF Core SQLite, the real publisher and
/// per-recipient recipient resolver) and substitute a <see cref="IRealtimeBackplane"/> that THROWS on every send
/// — a simulated backplane (Redis/Valkey) outage. The story's required test: "with the backplane unavailable a
/// reveal/hide/start/end and participant join/leave still succeed (state committed, delivery counted-and-dropped)".
///
/// Each test asserts the three guarantees during the outage:
/// <list type="bullet">
///   <item>the command STILL SUCCEEDS (HTTP 200) instead of surfacing a 500;</item>
///   <item>its STATE IS COMMITTED — the durable, append-only session event(s) the command emits are persisted
///   (read back from the real event repository), proving the commit happened before the failed delivery;</item>
///   <item>delivery was COUNTED-AND-DROPPED — the backplane WAS invoked (so the swallow genuinely engaged, not
///   that there was simply nothing to deliver) yet the throw never propagated.</item>
/// </list>
/// That the swallowed failure also increments the "event delivery failures" metric is pinned by the publisher
/// unit tests. Only the realtime transport seam is swapped; every other production behavior (auth, tenant
/// resolution, the Visibility engine, the transactional unit of work) runs unchanged. All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class BackplaneOutageBestEffortDeliveryTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_reveal_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Live);
        await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var resourceId = Guid.CreateVersion7();

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, resourceId, "reveal-1");

        // The committed reveal returns success even though every delivery threw.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // State committed: the durable reveal event(s) are persisted.
        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ContentRevealed);

        // Delivery counted-and-dropped: the backplane was invoked and threw, but the throw was swallowed.
        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    [Fact]
    public async Task A_hide_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Live);
        await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var resourceId = Guid.CreateVersion7();

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Reveal first (also during the outage), so the subsequent hide actually changes visibility and emits.
        using (var reveal = await PostRevealAsync(client, seed.SessionId, resourceId, "reveal-1"))
        {
            Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        }

        factory.Backplane.Reset();

        using var response = await PostHideAsync(client, seed.SessionId, resourceId, "hide-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ContentHidden);

        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    [Fact]
    public async Task A_session_start_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Prepared);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/start?organizationSlug={_orgA}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.SessionStarted);

        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    [Fact]
    public async Task A_session_end_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/end?organizationSlug={_orgA}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.SessionEnded);

        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    [Fact]
    public async Task A_participant_join_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Live);
        var participantId = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/participants/{participantId}/join?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ParticipantJoined);

        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    [Fact]
    public async Task A_participant_leave_still_succeeds_when_the_backplane_is_unavailable()
    {
        await using var factory = new BackplaneOutageApiFactory();
        const string subject = "host-a";
        var seed = await SeedHostSessionAsync(factory, subject, SessionStatus.Live);
        var participantId = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Join first (also during the outage), so the participant is present and the leave actually departs.
        using (var join = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/participants/{participantId}/join?organizationSlug={_orgA}",
            content: null))
        {
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        factory.Backplane.Reset();

        using var response = await client.PostAsync(
            $"/api/v1/sessions/{seed.SessionId}/participants/{participantId}/leave?organizationSlug={_orgA}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ParticipantLeft);

        Assert.True(factory.Backplane.Attempts > 0, "the backplane was never exercised, so the swallow proves nothing");
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        Guid resourceId,
        string idempotencyKey)
        => await PostVisibilityAsync(client, sessionId, "reveal", resourceId, idempotencyKey);

    private static async Task<HttpResponseMessage> PostHideAsync(
        HttpClient client,
        Guid sessionId,
        Guid resourceId,
        string idempotencyKey)
        => await PostVisibilityAsync(client, sessionId, "hide", resourceId, idempotencyKey);

    private static async Task<HttpResponseMessage> PostVisibilityAsync(
        HttpClient client,
        Guid sessionId,
        string verb,
        Guid resourceId,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/{verb}")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId }, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<SessionEvent>> ReadSessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<ISessionEventRepository>();
        return await events.ListBySessionAsync(organizationId, sessionId, CancellationToken.None);
    }

    /// <summary>Seeds (in org A) a caller who is a Host in both org and workspace, plus a session in the given status.</summary>
    private static async Task<SeedResult> SeedHostSessionAsync(
        WorkspaceApiFactory factory,
        string subject,
        SessionStatus status)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", status);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    private static async Task<Guid> SeedParticipantAsync(WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId)
    {
        Guid participantId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var participant = await db.AddParticipantAsync(organizationId, workspaceId, userProfileId: null);
            participantId = participant.Id;
        });
        return participantId;
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a backplane that THROWS on every send — a simulated
    /// outage. Only the realtime transport seam is swapped; the publisher's best-effort swallow, the recipient
    /// resolver and every other production behavior run unchanged.
    /// </summary>
    private sealed class BackplaneOutageApiFactory : WorkspaceApiFactory
    {
        public CountingThrowingRealtimeBackplane Backplane { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRealtimeBackplane>();
                services.AddSingleton<IRealtimeBackplane>(Backplane);
            });
        }
    }

    private sealed class CountingThrowingRealtimeBackplane : IRealtimeBackplane
    {
        private int _attempts;

        /// <summary>How many sends were attempted (each one throws), since the last <see cref="Reset"/>.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>Clears the attempt counter so a later phase observes only its own sends.</summary>
        public void Reset() => Interlocked.Exchange(ref _attempts, 0);

        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("backplane unavailable (simulated outage)");
        }
    }
}
