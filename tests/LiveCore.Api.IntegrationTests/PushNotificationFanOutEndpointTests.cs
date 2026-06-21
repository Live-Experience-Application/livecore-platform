// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the closed-app Web Push fan-out and dispatch (CORE-PUSH-002). They boot the real
/// application (real persistence over EF Core SQLite, the real publisher, recipient resolver and publish-time push
/// fan-out) and substitute ONLY the OUTBOUND sender (<see cref="IWebPushSender"/>) with a recording test double — the
/// same single-seam-swap pattern the realtime delivery tests use for the backplane.
///
/// They prove the story's acceptance criteria:
/// <list type="bullet">
///   <item>with VAPID configured, an authorized session event fans a CONTENT-FREE push out to a SUBSCRIBED recipient
///   and NOT to an UNSUBSCRIBED or an UNAUTHORIZED recipient (the recipient filter reuses the central resolution);</item>
///   <item>the dispatched push carries only an identifier/signal — no projected content;</item>
///   <item>a 410 (gone) from the push endpoint deletes the stale subscription;</item>
///   <item>with NO VAPID configured, no push is attempted (the outbox stays empty);</item>
///   <item>the realtime/REST path is unaffected when the push fan-out fails (the command still commits and succeeds).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class PushNotificationFanOutEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task An_audience_wide_reveal_enqueues_a_push_for_a_subscribed_recipient_only()
    {
        await using var factory = new PushDeliveryApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);

        // Two audience participants: one subscribed, one not. Both are authorized for an audience-wide reveal.
        var subscribed = await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/subscribed");
        var unsubscribed = await SeedParticipantUserAsync(factory, seed, "nosub-user");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recipientUserIds = await ReadOutboxRecipientUserIdsAsync(factory);
        Assert.Contains(subscribed.UserId, recipientUserIds);
        Assert.DoesNotContain(unsubscribed, recipientUserIds);
    }

    [Fact]
    public async Task A_selected_reveal_does_not_enqueue_a_push_for_a_non_targeted_recipient()
    {
        await using var factory = new PushDeliveryApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);

        // Both participants are subscribed; only one is the reveal target. The non-targeted participant is NOT
        // authorized to see a selected reveal, so it must NOT receive a push (the recipient filter, threat T3).
        var target = await SeedSubscribedParticipantAsync(factory, seed, "target-user", "https://push.test/sub/target");
        var other = await SeedSubscribedParticipantAsync(factory, seed, "other-user", "https://push.test/sub/other");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(
            client, seed.SessionId, Guid.CreateVersion7(), participantId: target.ParticipantId, "reveal-selected");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recipientUserIds = await ReadOutboxRecipientUserIdsAsync(factory);
        Assert.Contains(target.UserId, recipientUserIds);
        Assert.DoesNotContain(other.UserId, recipientUserIds);
    }

    [Fact]
    public async Task The_dispatch_sends_a_content_free_signal_and_drains_the_outbox()
    {
        await using var factory = new PushDeliveryApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);
        var recipient = await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/abc");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The ContentRevealed event the reveal appended — the identifier the push signals.
        var revealedEvent = (await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId))
            .Single(e => e.EventType == SessionEventTypes.ContentRevealed);

        factory.Sender.Outcome = WebPushSendOutcome.Delivered;
        var result = await RunDispatchAsync(factory);

        // A reveal emits the audience events (ContentRevealed plus VisibilityRuleChanged), so the audience receives a
        // content-free signal for each — exactly the audience the in-app events reach.
        Assert.True(result.Delivered >= 1);

        // Every push carried ONLY an identifier/signal — the source event id and the session id — never any
        // projected content (the WebPushSignal has no content fields), and only to the subscribed recipient.
        Assert.NotEmpty(factory.Sender.Sent);
        Assert.All(factory.Sender.Sent, sent =>
        {
            Assert.Equal("https://push.test/sub/abc", sent.Endpoint);
            Assert.Equal(seed.SessionId, sent.Signal.SessionId);
        });
        Assert.Contains(factory.Sender.Sent, sent => sent.Signal.SessionEventId == revealedEvent.Id);

        // The processed outbox rows are drained, and the (live) subscription survives a successful delivery.
        Assert.Empty(await ReadOutboxRecipientUserIdsAsync(factory));
        Assert.NotEmpty(await ReadSubscriptionEndpointsAsync(factory, recipient.UserId));
    }

    [Fact]
    public async Task A_410_from_the_push_endpoint_deletes_the_stale_subscription()
    {
        await using var factory = new PushDeliveryApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);
        var recipient = await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/gone");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The push service reports the subscription gone (HTTP 410).
        factory.Sender.Outcome = WebPushSendOutcome.Gone;
        var result = await RunDispatchAsync(factory);

        Assert.True(result.SubscriptionsRemoved >= 1);

        // The stale subscription was deleted, and the outbox drained.
        Assert.Empty(await ReadSubscriptionEndpointsAsync(factory, recipient.UserId));
        Assert.Empty(await ReadOutboxRecipientUserIdsAsync(factory));
    }

    [Fact]
    public async Task No_push_is_attempted_when_vapid_is_not_configured()
    {
        // The base factory configures NO VAPID/delivery, so the fan-out is inert: a reveal succeeds but enqueues
        // nothing (the "inert with no VAPID key configured" criterion).
        await using var factory = new WorkspaceApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);
        await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/abc");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await ReadOutboxRecipientUserIdsAsync(factory));
    }

    [Fact]
    public async Task A_reveal_still_succeeds_and_commits_when_the_push_fan_out_throws()
    {
        // The push fan-out is best-effort: a failure must never block or fail the in-session realtime delivery.
        await using var factory = new ThrowingPushFanOutApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);
        await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/abc");

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");

        // The command still succeeds, and its durable event is committed — the failed push never rolled it back.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ContentRevealed);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client, Guid sessionId, Guid resourceId, Guid? participantId, string idempotencyKey)
    {
        var body = participantId is { } pid
            ? (object)new { organizationSlug = _orgA, resourceType = "Entity", resourceId, participantId = pid }
            : new { organizationSlug = _orgA, resourceType = "Entity", resourceId };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<PushNotificationDispatchResult> RunDispatchAsync(PushDeliveryApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<PushNotificationDispatchService>();
        return await dispatch.DispatchPendingAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Guid>> ReadOutboxRecipientUserIdsAsync(WorkspaceApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PushNotificationDeliveries
            .AsNoTracking()
            .Select(delivery => delivery.RecipientUserProfileId)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadSubscriptionEndpointsAsync(WorkspaceApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserProfileId == userId)
            .Select(subscription => subscription.Endpoint)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<SessionEvent>> ReadSessionEventsAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<ISessionEventRepository>();
        return await events.ListBySessionAsync(organizationId, sessionId, CancellationToken.None);
    }

    private static async Task<SeedResult> SeedHostSessionAsync(WorkspaceApiFactory factory, string subject)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    /// <summary>Seeds an active audience participant linked to a fresh user; returns the user's id.</summary>
    private static async Task<Guid> SeedParticipantUserAsync(WorkspaceApiFactory factory, SeedResult seed, string subject)
    {
        Guid userId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            userId = user.Id;
            await db.AddParticipantAsync(seed.OrganizationId, seed.WorkspaceId, user.Id);
        });
        return userId;
    }

    /// <summary>Seeds an active participant linked to a fresh user WITH a registered push subscription.</summary>
    private static async Task<RecipientSeed> SeedSubscribedParticipantAsync(
        WorkspaceApiFactory factory, SeedResult seed, string subject, string endpoint)
    {
        RecipientSeed recipient = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var participant = await db.AddParticipantAsync(seed.OrganizationId, seed.WorkspaceId, user.Id);
            await db.AddPushSubscriptionAsync(user.Id, endpoint);
            recipient = new RecipientSeed(user.Id, participant.Id);
        });
        return recipient;
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    private readonly record struct RecipientSeed(Guid UserId, Guid ParticipantId);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that turns on closed-app push delivery (with throwaway VAPID material) and
    /// substitutes a RECORDING <see cref="IWebPushSender"/> plus the worker's dispatch service, so a test can run a
    /// dispatch sweep in-process and inspect what was sent.
    /// </summary>
    private sealed class PushDeliveryApiFactory : WorkspaceApiFactory
    {
        public RecordingWebPushSender Sender { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Opt in to delivery and supply throwaway VAPID material so WebPushDeliveryOptions.IsActive is true (the
            // real sender is swapped out below, so the values need only be present, not real keys).
            builder.UseSetting("WebPush:Delivery:Enabled", "true");
            builder.UseSetting("WebPush:Vapid:PublicKey", "test-public-key");
            builder.UseSetting("WebPush:Vapid:PrivateKey", "test-private-key");
            builder.UseSetting("WebPush:Vapid:Subject", "mailto:ops@example.test");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IWebPushSender>(Sender);
                // The dispatch service is a worker-host registration; register it here so the test can run a sweep.
                services.TryAddScoped<PushNotificationDispatchService>();
            });
        }
    }

    /// <summary>A <see cref="WorkspaceApiFactory"/> whose push fan-out always throws, to prove the publisher swallows it.</summary>
    private sealed class ThrowingPushFanOutApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISessionEventPushFanOut>();
                services.AddScoped<ISessionEventPushFanOut, ThrowingPushFanOut>();
            });
        }
    }

    private sealed class ThrowingPushFanOut : ISessionEventPushFanOut
    {
        public Task FanOutAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException("push fan-out unavailable (simulated failure)");
    }

    private sealed class RecordingWebPushSender : IWebPushSender
    {
        private readonly List<WebPushDispatch> _sent = [];

        public WebPushSendOutcome Outcome { get; set; } = WebPushSendOutcome.Delivered;

        public IReadOnlyList<WebPushDispatch> Sent => _sent;

        public Task<WebPushSendResult> SendAsync(WebPushDispatch dispatch, CancellationToken cancellationToken)
        {
            _sent.Add(dispatch);
            return Task.FromResult(new WebPushSendResult(Outcome));
        }
    }
}
