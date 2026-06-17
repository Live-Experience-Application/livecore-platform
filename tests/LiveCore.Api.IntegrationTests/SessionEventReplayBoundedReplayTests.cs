// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the BOUNDED, CURSORED reconnect replay (CORE-PERF-002,
/// <c>GET /api/v1/sessions/{sessionId}/events</c>). They drive the REAL application over real HTTP through a
/// factory that wraps two seams with recording decorators (delegating to the real implementations): the
/// session-event repository (so the test sees that the replay reads a CURSORED, CAPPED slice via
/// <see cref="ISessionEventRepository.ListBySessionAfterAsync"/> and NEVER loads the whole stream via
/// <see cref="ISessionEventRepository.ListBySessionAsync"/>) and the visibility-rule repository (so the test
/// counts the rule lookups the replay issues). Everything else — auth, tenant resolution, the recipient
/// resolver, the Visibility engine — runs unchanged.
///
/// The story's required integration tests:
/// <list type="bullet">
///   <item>replay loads ONLY post-cursor rows up to the cap (asserted query), not the full stream;</item>
///   <item>a long, well-attended session reconnect issues a BOUNDED query count — independent of audience
///   size AND of the number of replayed events (the batched audience lookup is ONE query, CORE-PERF-002
///   reusing CORE-PERF-001);</item>
///   <item>replayed events are still correctly visibility-filtered (a participant never replays another's
///   private reveal — threat T3).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventReplayBoundedReplayTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _hostSubject = "host-a";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Replay_reads_a_cursored_capped_slice_and_never_loads_the_whole_stream()
    {
        // CORE-PERF-002: the replay read is the SQL-side cursored, capped read — never the full-stream load.
        await using var factory = new RecordingReplayApiFactory();
        var scenario = await SeedScenarioAsync(factory, participantCount: 3);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToAudienceAsync(host, scenario.SessionId, Guid.CreateVersion7(), "k-1");
        await RevealToAudienceAsync(host, scenario.SessionId, Guid.CreateVersion7(), "k-2");

        factory.Events.Reset();

        // Replay after sequence 1: the bounded read is asked for the post-cursor rows up to the cap.
        var tail = await ReplayAsync(host, scenario.SessionId, afterSequence: 1);

        Assert.NotEmpty(tail.Events);
        // The full-stream read was NEVER used by the replay path; only the cursored, capped read was.
        Assert.Equal(0, factory.Events.FullStreamReads);
        Assert.Equal(1, factory.Events.CursoredReads);
        Assert.Equal(1L, factory.Events.LastAfterSequence);
        Assert.Equal(SessionReplayService.MaxReplayEvents, factory.Events.LastLimit);
    }

    [Fact]
    public async Task A_well_attended_reconnect_issues_a_bounded_rule_lookup_count_independent_of_audience_and_events()
    {
        // CORE-PERF-002, the headline scale proof: the per-replay visibility-rule lookup count is BOUNDED —
        // a single batched lookup — regardless of how many participants are in the audience or how many
        // events are replayed (the old per-event resolve would have done one lookup per event).
        var few = await MeasureReplayRuleLookupsAsync(participantCount: 3, audienceReveals: 2);
        var manyParticipants = await MeasureReplayRuleLookupsAsync(participantCount: 24, audienceReveals: 2);
        var manyEvents = await MeasureReplayRuleLookupsAsync(participantCount: 3, audienceReveals: 8);

        // Exactly one batched rule lookup per replay — and the same regardless of audience size or event count.
        Assert.Equal(1, few.RuleLookups);
        Assert.Equal(few.RuleLookups, manyParticipants.RuleLookups);
        Assert.Equal(few.RuleLookups, manyEvents.RuleLookups);

        // The replay still returned the full host-visible stream in every case (CORE-EVT-003: two events per
        // reveal), so the bound is not achieved by dropping events.
        Assert.Equal(2 * 2, few.HostEventCount);
        Assert.Equal(2 * 2, manyParticipants.HostEventCount);
        Assert.Equal(8 * 2, manyEvents.HostEventCount);
    }

    [Fact]
    public async Task A_well_attended_replay_is_still_visibility_filtered()
    {
        // CORE-PERF-002 must NOT change replay correctness: in a well-attended session, an unselected
        // participant still replays only the audience reveal and NEVER another participant's private reveal
        // (the crown jewel, threat T3) — proven through the bounded, batched path.
        await using var factory = new RecordingReplayApiFactory();
        var scenario = await SeedScenarioAsync(factory, participantCount: 20);
        var privateResource = Guid.CreateVersion7();
        var audienceResource = Guid.CreateVersion7();

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        await RevealToParticipantAsync(host, scenario.SessionId, privateResource, scenario.ParticipantOneId, "k-private");
        await RevealToAudienceAsync(host, scenario.SessionId, audienceResource, "k-audience");

        // The selected participant replays the private reveal AND the audience reveal.
        using var participantOne = factory.CreateClientFor(scenario.ParticipantOneSubject, _issuer, _orgA);
        var replayOne = await ReplayAsync(participantOne, scenario.SessionId, participantId: scenario.ParticipantOneId);
        Assert.Contains(replayOne.Events, item => item.Payload.Contains(privateResource.ToString(), StringComparison.Ordinal));
        Assert.Contains(replayOne.Events, item => item.Payload.Contains(audienceResource.ToString(), StringComparison.Ordinal));

        // The unselected participant replays ONLY the audience reveal — never the private one.
        using var participantTwo = factory.CreateClientFor(scenario.ParticipantTwoSubject, _issuer, _orgA);
        var replayTwo = await ReplayAsync(participantTwo, scenario.SessionId, participantId: scenario.ParticipantTwoId);
        Assert.All(replayTwo.Events, item => Assert.Contains(audienceResource.ToString(), item.Payload, StringComparison.Ordinal));
        Assert.DoesNotContain(
            replayTwo.Events,
            item => item.Payload.Contains(privateResource.ToString(), StringComparison.Ordinal));
    }

    // =====================================================================
    // Measurement helper.
    // =====================================================================

    private static async Task<ReplayMeasurement> MeasureReplayRuleLookupsAsync(int participantCount, int audienceReveals)
    {
        await using var factory = new RecordingReplayApiFactory();
        var scenario = await SeedScenarioAsync(factory, participantCount);

        using var host = factory.CreateClientFor(_hostSubject, _issuer, _orgA);
        for (var index = 0; index < audienceReveals; index++)
        {
            await RevealToAudienceAsync(host, scenario.SessionId, Guid.CreateVersion7(), $"k-{index}");
        }

        // Only the replay itself is measured — reset the rule-lookup counter after seeding/revealing.
        factory.RuleLookups.Reset();

        var replay = await ReplayAsync(host, scenario.SessionId);

        return new ReplayMeasurement(factory.RuleLookups.Count, replay.Events.Count);
    }

    private readonly record struct ReplayMeasurement(int RuleLookups, int HostEventCount);

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<Scenario> SeedScenarioAsync(RecordingReplayApiFactory factory, int participantCount)
    {
        const string participantOneSubject = "participant-1";
        const string participantTwoSubject = "participant-2";
        Scenario scenario = default;
        await factory.SeedAsync(async db =>
        {
            var hostUser = await db.AddUserAsync(_issuer, _hostSubject);
            var participantOneUser = await db.AddUserAsync(_issuer, participantOneSubject);
            var participantTwoUser = await db.AddUserAsync(_issuer, participantTwoSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, hostUser.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(org.Id, participantOneUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(org.Id, participantTwoUser.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, hostUser.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            var participantOne = await db.AddParticipantAsync(org.Id, workspace.Id, participantOneUser.Id, displayName: "P1");
            var participantTwo = await db.AddParticipantAsync(org.Id, workspace.Id, participantTwoUser.Id, displayName: "P2");

            // A well-attended audience: additional anonymous active participants beyond the two user-linked ones.
            for (var index = 0; index < participantCount; index++)
            {
                await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: null);
            }

            scenario = new Scenario(
                session.Id,
                participantOne.Id,
                participantTwo.Id,
                participantOneSubject,
                participantTwoSubject);
        });
        return scenario;
    }

    private static async Task RevealToAudienceAsync(HttpClient host, Guid sessionId, Guid resourceId, string idempotencyKey)
    {
        var response = await PostRevealAsync(
            host, sessionId, new { organizationSlug = _orgA, resourceType = "Entity", resourceId }, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RevealToParticipantAsync(
        HttpClient host, Guid sessionId, Guid resourceId, Guid participantId, string idempotencyKey)
    {
        var response = await PostRevealAsync(
            host, sessionId, new { organizationSlug = _orgA, resourceType = "Entity", resourceId, participantId }, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client, Guid sessionId, object body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<ReplayDto> ReplayAsync(
        HttpClient client, Guid sessionId, Guid? participantId = null, long? afterSequence = null)
    {
        var url = $"/api/v1/sessions/{sessionId}/events?organizationSlug={_orgA}";
        if (participantId is { } participant)
        {
            url += $"&participantId={participant}";
        }

        if (afterSequence is { } cursor)
        {
            url += $"&afterSequence={cursor}";
        }

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayDto>(_json);
        Assert.NotNull(body);
        return body;
    }

    private readonly record struct Scenario(
        Guid SessionId,
        Guid ParticipantOneId,
        Guid ParticipantTwoId,
        string ParticipantOneSubject,
        string ParticipantTwoSubject);

    private sealed record ReplayDto(
        Guid SessionId,
        IReadOnlyList<ReplayItemDto> Events,
        long? NextSequence,
        DateTimeOffset GeneratedAt);

    private sealed record ReplayItemDto(
        Guid EventId,
        long Sequence,
        string EventType,
        Guid SessionId,
        string Payload,
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        Guid? TargetParticipantId);

    // =====================================================================
    // Recording factory + decorators.
    // =====================================================================

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that wraps the visibility-rule repository with a lookup COUNTER and
    /// the session-event repository with a read RECORDER (both delegating to the real implementations), so a
    /// test can prove the replay's query shape. No other production behavior is changed.
    /// </summary>
    private sealed class RecordingReplayApiFactory : WorkspaceApiFactory
    {
        public RuleLookupCounter RuleLookups { get; } = new();

        public EventReadRecorder Events { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVisibilityRuleRepository>();
                services.AddScoped<IVisibilityRuleRepository>(sp =>
                    new CountingVisibilityRuleRepository(
                        new VisibilityRuleRepository(sp.GetRequiredService<LiveCoreDbContext>()),
                        RuleLookups));

                services.RemoveAll<ISessionEventRepository>();
                services.AddScoped<ISessionEventRepository>(sp =>
                    new RecordingSessionEventRepository(
                        new SessionEventRepository(sp.GetRequiredService<LiveCoreDbContext>()),
                        Events));
            });
        }
    }

    private sealed class RuleLookupCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>Records the session-event reads the replay path issues (full-stream vs cursored, and the cursor/cap).</summary>
    private sealed class EventReadRecorder
    {
        private int _fullStreamReads;
        private int _cursoredReads;

        public int FullStreamReads => Volatile.Read(ref _fullStreamReads);

        public int CursoredReads => Volatile.Read(ref _cursoredReads);

        public long? LastAfterSequence { get; private set; }

        public int LastLimit { get; private set; }

        public void RecordFullStream() => Interlocked.Increment(ref _fullStreamReads);

        public void RecordCursored(long? afterSequence, int limit)
        {
            Interlocked.Increment(ref _cursoredReads);
            LastAfterSequence = afterSequence;
            LastLimit = limit;
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _fullStreamReads, 0);
            Interlocked.Exchange(ref _cursoredReads, 0);
        }
    }

    private sealed class CountingVisibilityRuleRepository : IVisibilityRuleRepository
    {
        private readonly IVisibilityRuleRepository _inner;
        private readonly RuleLookupCounter _counter;

        public CountingVisibilityRuleRepository(IVisibilityRuleRepository inner, RuleLookupCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            VisibilityResourceType resourceType,
            Guid resourceId,
            CancellationToken cancellationToken)
        {
            _counter.Increment();
            return _inner.ListByResourceAsync(organizationId, workspaceId, sessionId, resourceType, resourceId, cancellationToken);
        }

        public Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            IReadOnlyCollection<Guid> resourceIds,
            CancellationToken cancellationToken)
        {
            _counter.Increment();
            return _inner.ListByResourcesAsync(organizationId, workspaceId, sessionId, resourceIds, cancellationToken);
        }

        public Task<VisibilityRule?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => _inner.FindByIdAsync(organizationId, workspaceId, id, cancellationToken);

        public Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
            => _inner.ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken);

        public Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken)
            => _inner.AddAsync(rule, cancellationToken);

        public Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken)
            => _inner.UpdateAsync(rule, cancellationToken);

        public Task<int> RemoveByResourceAsync(
            Guid organizationId,
            Guid workspaceId,
            VisibilityResourceType resourceType,
            Guid resourceId,
            CancellationToken cancellationToken)
            => _inner.RemoveByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken);
    }

    private sealed class RecordingSessionEventRepository : ISessionEventRepository
    {
        private readonly ISessionEventRepository _inner;
        private readonly EventReadRecorder _recorder;

        public RecordingSessionEventRepository(ISessionEventRepository inner, EventReadRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => _inner.AppendAsync(sessionEvent, cancellationToken);

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            Guid organizationId,
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            _recorder.RecordFullStream();
            return _inner.ListBySessionAsync(organizationId, sessionId, cancellationToken);
        }

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAfterAsync(
            Guid organizationId,
            Guid sessionId,
            long? afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            _recorder.RecordCursored(afterSequence, limit);
            return _inner.ListBySessionAfterAsync(organizationId, sessionId, afterSequence, limit, cancellationToken);
        }
    }
}
