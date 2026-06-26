// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
///
/// CORE-PUSH-003 RE-VERIFIES the same loop end-to-end on the STANDARD self-host stack (a vertical adopter
/// re-reported closed-app DELIVERY as unobserved on the 0.4.0 harness, ARC-GAP-111): rather than swapping the whole
/// <see cref="IWebPushSender"/>, those tests keep the REAL <see cref="VapidWebPushSender"/> wired exactly as the
/// worker's <c>AddWebPushDelivery</c> wires it (a typed HttpClient) and substitute ONLY the transport (a capturing
/// <see cref="HttpMessageHandler"/>), so the actual OUTBOUND HTTP request the configured stack emits is asserted:
/// <list type="bullet">
///   <item>the stack ACTUALLY emits a push (the real sender runs and produces an outbound request);</item>
///   <item>the request body is EMPTY and it carries ONLY the VAPID Authorization (RFC 8292) and TTL (RFC 8030)
///   headers — no resource id, no session/event/target id, no content of any kind;</item>
///   <item>a HOST-ONLY event reaches no participant audience, so a subscribed participant receives nothing;</item>
///   <item>the subscription's <c>p256dh</c>/<c>auth</c> secrets never reach the wire or any log, even on the
///   404/410 stale-subscription cleanup path (threat T7).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class PushNotificationFanOutEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    // The default subscription secrets TestData.AddPushSubscriptionAsync registers; CORE-PUSH-003 asserts neither
    // the p256dh nor the auth secret ever reaches the wire or a log (threat T7).
    private const string _seedP256dh = "seed-p256dh-key";
    private const string _seedAuth = "seed-auth-secret";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // A real P-256 VAPID key pair so the production VapidWebPushSender can actually sign the outbound push in the
    // capturing-transport tests (CORE-PUSH-003). The recording-sender tests above need no real keys.
    private static readonly (string Public, string Private) _vapidKeyPair = GenerateVapidKeyPair();

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
    // CORE-PUSH-003: re-verify that the STANDARD self-host stack actually EMITS a content-free push. The tests
    // above prove the fan-out and the dispatch with a recording sender; these keep the REAL VapidWebPushSender
    // (wired exactly as the worker wires it) and capture the actual outbound HTTP request + the emitted logs, so the
    // vertical adopter's re-reported "closed-app DELIVERY unobserved" (ARC-GAP-111) is disproven end-to-end on the wire.
    // =====================================================================

    [Fact]
    public async Task The_configured_stack_emits_a_content_free_push_carrying_only_vapid_and_ttl_headers()
    {
        await using var factory = new CapturingPushDeliveryApiFactory();
        const string host = "host-a";
        const string endpoint = "https://push.test/sub/real-recipient";
        var seed = await SeedHostSessionAsync(factory, host);
        await SeedSubscribedParticipantAsync(factory, seed, "sub-user", endpoint);

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        var resourceId = Guid.CreateVersion7();
        using var response = await PostRevealAsync(client, seed.SessionId, resourceId, participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var revealedEvent = (await ReadSessionEventsAsync(factory, seed.OrganizationId, seed.SessionId))
            .Single(e => e.EventType == SessionEventTypes.ContentRevealed);

        var result = await RunDispatchAsync(factory);
        Assert.True(result.Delivered >= 1);

        // THE STANDARD STACK ACTUALLY EMITTED THE PUSH over real HTTP (the vertical adopter's "could not observe
        // delivery" re-check): the production VapidWebPushSender ran end-to-end and produced at least one request.
        Assert.NotEmpty(factory.Transport.Requests);
        Assert.All(factory.Transport.Requests, request =>
        {
            // A POST to the subscription endpoint with an EMPTY body — content-free, so nothing can surface on a
            // lock screen (the strongest possible guarantee: there is not even an encrypted blob).
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(endpoint, request.Uri.ToString());
            Assert.Empty(request.Body);

            // ONLY the VAPID Authorization (RFC 8292) and the TTL (RFC 8030) headers — no other header of any kind.
            Assert.All(request.HeaderNames, name => Assert.Contains(name, new[] { "Authorization", "TTL" }));
            Assert.Contains("Authorization", request.HeaderNames);
            Assert.Contains("TTL", request.HeaderNames);
            Assert.StartsWith("vapid t=", request.HeaderValue("Authorization"), StringComparison.Ordinal);
            Assert.True(
                long.TryParse(request.HeaderValue("TTL"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttl)
                && ttl > 0,
                "The TTL header must be a positive integer of seconds.");

            // Not one identifier or secret rides on the wire: no resource id, no session/event id, no content, and
            // never the subscription's p256dh/auth encryption secrets (a payload-less push reads none of them).
            var wire = request.SerializeHeadersAndBody();
            Assert.DoesNotContain(resourceId.ToString(), wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(seed.SessionId.ToString(), wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(revealedEvent.Id.ToString(), wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_seedP256dh, wire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_seedAuth, wire, StringComparison.OrdinalIgnoreCase);
        });

        // ...and no log line the sender or the dispatch emitted carried a subscription secret (threat T7).
        var logs = factory.LogText();
        Assert.DoesNotContain(_seedP256dh, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_seedAuth, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_host_only_event_enqueues_no_push_even_for_a_subscribed_audience_participant()
    {
        await using var factory = new PushDeliveryApiFactory();
        const string host = "host-a";
        var seed = await SeedHostSessionAsync(factory, host);

        // A subscribed, ACTIVE audience participant: they WOULD receive an audience-wide push, so this is a real
        // negative — a HOST-ONLY event (a generated recap / a created session) reaches the hosts only and must
        // therefore enqueue nothing for them (the resolver fails closed to no participant audience; threats T2/T7).
        var recipient = await SeedSubscribedParticipantAsync(factory, seed, "sub-user", "https://push.test/sub/host-only");

        await using var scope = factory.Services.CreateAsyncScope();
        var fanOut = scope.ServiceProvider.GetRequiredService<ISessionEventPushFanOut>();
        var hostOnlyEvent = SessionEvent.Create(
            seed.OrganizationId,
            seed.WorkspaceId,
            seed.SessionId,
            SessionEventTypes.RecapGenerated,
            createdBy: null,
            targetParticipantId: null,
            payload: "{}",
            schemaVersion: 1,
            createdAt: DateTimeOffset.UnixEpoch);
        Assert.True(SessionEventTypes.IsHostOnly(hostOnlyEvent.EventType));

        await fanOut.FanOutAsync(hostOnlyEvent, CancellationToken.None);

        // No outbox row for the subscribed participant (or anyone): the host-only event has no participant audience.
        Assert.Empty(await ReadOutboxRecipientUserIdsAsync(factory));

        // The participant's subscription is untouched — this negative is about routing, not stale-subscription cleanup.
        Assert.NotEmpty(await ReadSubscriptionEndpointsAsync(factory, recipient.UserId));
    }

    [Fact]
    public async Task A_gone_subscription_is_cleaned_up_without_logging_its_endpoint_or_secrets()
    {
        await using var factory = new CapturingPushDeliveryApiFactory();
        const string host = "host-a";
        const string endpoint = "https://push.test/sub/gone-recipient";
        var seed = await SeedHostSessionAsync(factory, host);
        var recipient = await SeedSubscribedParticipantAsync(factory, seed, "sub-user", endpoint);

        using var client = factory.CreateClientFor(host, _issuer, _orgA);
        using var response = await PostRevealAsync(client, seed.SessionId, Guid.CreateVersion7(), participantId: null, "reveal-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The real push service reports the endpoint GONE (HTTP 410): the real sender maps it to Gone and the
        // dispatch deletes the stale subscription (the 404/410 cleanup) — exercised here through the real transport.
        factory.Transport.ResponseStatus = HttpStatusCode.Gone;
        var result = await RunDispatchAsync(factory);

        Assert.True(result.SubscriptionsRemoved >= 1);
        Assert.Empty(await ReadSubscriptionEndpointsAsync(factory, recipient.UserId));

        // The cleanup logged the subscription/recipient by IDENTIFIER only; the subscription's p256dh/auth encryption
        // secrets are read into NO log even on the 404/410 cleanup path (threat T7). (The destination endpoint URL
        // may appear in the standard HttpClient transport log — it is the routable address, not a subscription
        // secret — so the assertion targets the secrets the story names.)
        var logs = factory.LogText();
        Assert.DoesNotContain(_seedP256dh, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_seedAuth, logs, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<PushNotificationDispatchResult> RunDispatchAsync(WorkspaceApiFactory factory)
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

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that turns on closed-app push delivery with a REAL VAPID key pair and
    /// keeps the production <see cref="VapidWebPushSender"/> wired exactly as the worker's <c>AddWebPushDelivery</c>
    /// wires it (a typed HttpClient), substituting ONLY the transport with a capturing
    /// <see cref="HttpMessageHandler"/> (CORE-PUSH-003). So a test runs a dispatch sweep in-process and inspects the
    /// ACTUAL outbound HTTP request the standard self-host stack emits, plus everything the stack logged.
    /// </summary>
    private sealed class CapturingPushDeliveryApiFactory : WorkspaceApiFactory
    {
        private readonly RecordingLoggerProvider _logs = new();

        public CapturingHttpMessageHandler Transport { get; } = new();

        /// <summary>Every line the host logged during the test, so an assertion can prove a secret never appears.</summary>
        public string LogText() => string.Join('\n', _logs.Lines);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Opt in to delivery with a REAL VAPID key pair so the production sender can actually sign (unlike
            // PushDeliveryApiFactory, this factory keeps the real sender and captures the request it produces).
            builder.UseSetting("WebPush:Delivery:Enabled", "true");
            builder.UseSetting("WebPush:Vapid:PublicKey", _vapidKeyPair.Public);
            builder.UseSetting("WebPush:Vapid:PrivateKey", _vapidKeyPair.Private);
            builder.UseSetting("WebPush:Vapid:Subject", "mailto:ops@example.test");

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));

            builder.ConfigureServices(services =>
            {
                // Wire the REAL VapidWebPushSender as a typed HttpClient — exactly the worker's AddWebPushDelivery
                // registration — but capture its outbound request instead of contacting a real push service.
                services.AddHttpClient<IWebPushSender, VapidWebPushSender>()
                    .ConfigurePrimaryHttpMessageHandler(() => Transport);

                // The dispatch service is a worker-host registration; add it here so the test can run a sweep.
                services.TryAddScoped<PushNotificationDispatchService>();
            });
        }
    }

    /// <summary>
    /// A capturing <see cref="HttpMessageHandler"/> that records each outbound request (method, uri, headers and the
    /// fully-read body) and returns a configurable status, so a test asserts what the real sender actually put on the
    /// wire without contacting a push service.
    /// </summary>
    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];

        /// <summary>The status the stub push service returns (default 201 Created -> Delivered).</summary>
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.Created;

        public IReadOnlyList<CapturedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Copy the request headers out now: the dispatch disposes the request once SendAsync returns.
            var headers = request.Headers
                .Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))
                .ToArray();

            _requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, body));
            return new HttpResponseMessage(ResponseStatus);
        }
    }

    /// <summary>One captured outbound push request (CORE-PUSH-003 assertions).</summary>
    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        byte[] Body)
    {
        public IEnumerable<string> HeaderNames => Headers.Select(header => header.Key);

        public string HeaderValue(string name) =>
            Headers.Single(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>The headers and body rendered to one string, so an assertion can prove no value rides the wire.</summary>
        public string SerializeHeadersAndBody() =>
            string.Join('\n', Headers.Select(header => $"{header.Key}: {header.Value}"))
            + '\n'
            + Encoding.UTF8.GetString(Body);
    }

    /// <summary>An in-memory <see cref="ILoggerProvider"/> recording every formatted log line for secret-leak assertions.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_lines);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _lines;

            public RecordingLogger(ConcurrentQueue<string> lines) => _lines = lines;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                // Capture BOTH the rendered message and the raw state, so a secret cannot hide in an unformatted
                // structured value (e.g. a log property) that the message template omitted.
                _lines.Enqueue(formatter(state, exception));
                _lines.Enqueue(state?.ToString() ?? string.Empty);
            }
        }
    }

    /// <summary>
    /// Generates a fresh P-256 VAPID key pair as the base64url public point (0x04||X||Y) and private scalar the
    /// production sender is configured with — the same encoding the registration and the unit tests use.
    /// </summary>
    private static (string Public, string Private) GenerateVapidKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        var point = new byte[65];
        point[0] = 0x04;
        LeftPad(parameters.Q.X!, 32).CopyTo(point, 1);
        LeftPad(parameters.Q.Y!, 32).CopyTo(point, 33);

        return (
            WebPushVapid.Base64UrlEncode(point),
            WebPushVapid.Base64UrlEncode(LeftPad(parameters.D!, 32)));
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }
}
