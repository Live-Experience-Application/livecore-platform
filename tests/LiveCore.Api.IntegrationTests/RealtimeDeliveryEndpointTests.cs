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
/// HTTP integration tests for realtime event DELIVERY through the CORE-RT-006 scale-out seam. They drive
/// the real reveal command over real HTTP (<see cref="WorkspaceApiFactory"/>: test auth + EF Core SQLite,
/// foreign keys ON) and substitute a recording <see cref="IRealtimeBackplane"/> for the in-process default,
/// so the test observes exactly which SERVER-MANAGED groups each published event was delivered to —
/// end-to-end through the real publisher, the per-recipient recipient resolver (CORE-RT-004) and the
/// central Visibility engine, without a live SignalR connection. The backplane is the single transport
/// boundary, so capturing it captures every send.
///
/// These are the story's required "Realtime integration tests with selected/unselected participants" and
/// the threat model's required controls "reveal to selected participants only" and "realtime event
/// recipient filter" (docs/07_SECURITY_THREAT_MODEL.md threat T3; docs/11_REALTIME_SYNC.md tests). The
/// group names are re-derived independently from the documented connection model (not from the production
/// helper), so a regression in the production naming cannot hide behind a matching test constant.
///
/// Coverage:
/// <list type="bullet">
///   <item>SELECTED: a selected-participant reveal is delivered to the hosts group and ONLY that
///   participant's group — never to the unselected participant or the observers (the crown jewel).</item>
///   <item>AUDIENCE-WIDE (CORE-PERF-001): an audience-wide reveal is delivered to hosts, observers and the
///   SHARED session-audience group — one publish, NOT one per participant.</item>
///   <item>SCALE (CORE-PERF-001): the per-reveal rule-lookup count and the audience-group send shape are
///   INDEPENDENT of audience size — a reveal to many participants issues the same single rule lookup and
///   the same one audience-group send as a reveal to few, never N.</item>
///   <item>NEGATIVE AUTHORIZATION: a caller without the reveal role is 403 and a cross-tenant caller is
///   404 — and in both cases NO event is delivered (fail-closed: no command, no event, no send).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeDeliveryEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Selected_participant_reveal_is_delivered_to_that_participant_and_hosts_only()
    {
        await using var factory = new RecordingDeliveryApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var selected = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var unselected = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", resourceId, selected), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var groups = factory.Backplane.Groups();
        // Hosts always, plus the SELECTED participant's group.
        Assert.Contains(HostsGroup(seed.SessionId), groups);
        Assert.Contains(ParticipantGroup(seed.SessionId, selected), groups);
        // The crown jewel: the UNSELECTED participant and the observers receive NOTHING.
        Assert.DoesNotContain(ParticipantGroup(seed.SessionId, unselected), groups);
        Assert.DoesNotContain(ObserversGroup(seed.SessionId), groups);
        // Exactly those two recipient groups — no broadcast leaked beyond them. CORE-EVT-003 makes a reveal
        // emit two subject-gated events (ContentRevealed + VisibilityRuleChanged) to the SAME recipient
        // groups, so we assert on the distinct group set.
        Assert.Equal(2, groups.Distinct().Count());
    }

    [Fact]
    public async Task Audience_wide_reveal_is_delivered_to_hosts_observers_and_the_shared_audience_group()
    {
        // CORE-PERF-001: an audience-wide reveal is delivered to hosts, observers and the SHARED
        // session-audience group — ONE publish to the whole audience, NOT one per participant. The
        // individual participant groups are NOT used (they are reserved for selected-participant events).
        await using var factory = new RecordingDeliveryApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        var participantOne = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var participantTwo = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var groups = factory.Backplane.Groups();
        Assert.Contains(HostsGroup(seed.SessionId), groups);
        Assert.Contains(ObserversGroup(seed.SessionId), groups);
        Assert.Contains(AudienceGroup(seed.SessionId), groups);
        // No per-participant fan-out: neither active participant's individual group is used.
        Assert.DoesNotContain(ParticipantGroup(seed.SessionId, participantOne), groups);
        Assert.DoesNotContain(ParticipantGroup(seed.SessionId, participantTwo), groups);
        // Exactly hosts + observers + the shared audience group (distinct recipient groups). CORE-EVT-003
        // makes a reveal emit two subject-gated events (ContentRevealed + VisibilityRuleChanged) to the
        // SAME recipient groups, so we assert on the distinct group set.
        Assert.Equal(3, groups.Distinct().Count());
    }

    [Fact]
    public async Task An_audience_reveal_issues_one_rule_lookup_and_one_audience_group_send_regardless_of_audience_size()
    {
        // CORE-PERF-001, the headline scale proof: an audience-wide reveal to a SMALL audience and to a
        // MUCH LARGER one issue the SAME number of visibility-rule lookups and the SAME audience-group send
        // shape — the cost does not grow with audience size (the old per-participant fan-out would have done
        // 1+N lookups and N sends per event).
        var few = await MeasureAudienceRevealAsync(participantCount: 2);
        var many = await MeasureAudienceRevealAsync(participantCount: 12);

        // The per-reveal rule-lookup count is independent of audience size.
        Assert.Equal(few.RuleLookups, many.RuleLookups);

        // Both deliver to exactly the SAME group shape — hosts + observers + the one shared audience group —
        // and to NO individual participant group, regardless of how many participants are in the audience.
        Assert.Equal(new[] { "hosts", "observers", "audience" }, few.GroupShape);
        Assert.Equal(new[] { "hosts", "observers", "audience" }, many.GroupShape);
    }

    private static async Task<RevealMeasurement> MeasureAudienceRevealAsync(int participantCount)
    {
        await using var factory = new RecordingDeliveryApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host);
        for (var index = 0; index < participantCount; index++)
        {
            await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        }

        // Only the reveal itself is measured — reset the rule-lookup counter after seeding.
        factory.RuleLookups.Reset();

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Reduce the recorded group names to their session-relative "shape" (last segment), so the comparison
        // is independent of the per-factory session id: hosts/observers/audience, never participant:{id}.
        var shape = factory.Backplane.Groups()
            .Select(group => group.Split(':').Last())
            .Distinct()
            .ToArray();

        return new RevealMeasurement(factory.RuleLookups.Count, shape);
    }

    private readonly record struct RevealMeasurement(int RuleLookups, IReadOnlyList<string> GroupShape);

    [Fact]
    public async Task A_caller_without_the_reveal_role_is_forbidden_and_nothing_is_delivered()
    {
        await using var factory = new RecordingDeliveryApiFactory();
        const string subject = "member-observer";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Observer);
        var participant = await SeedParticipantAsync(factory, seed.OrganizationId, seed.WorkspaceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostRevealAsync(
            client, seed.SessionId, BodyForParticipant(_orgA, "Entity", resourceId, participant), "key-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Backplane.Groups());
    }

    [Fact]
    public async Task A_cross_tenant_caller_is_not_found_and_nothing_is_delivered()
    {
        await using var factory = new RecordingDeliveryApiFactory();
        const string subject = "user-ab";
        var resourceId = Guid.CreateVersion7();
        SeedResult seedB = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var session = await db.AddSessionAsync(orgB.Id, workspaceInB.Id, "B", SessionStatus.Live);
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, session.Id);
        });

        // Address the org-B session with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await PostRevealAsync(client, seedB.SessionId, Body(_orgA, "Entity", resourceId), "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Backplane.Groups());
    }

    // =====================================================================
    // Group-name contract, re-derived independently from docs/11_REALTIME_SYNC.md.
    // =====================================================================

    private static string HostsGroup(Guid sessionId) => $"session:{sessionId}:hosts";

    private static string ObserversGroup(Guid sessionId) => $"session:{sessionId}:observers";

    private static string AudienceGroup(Guid sessionId) => $"session:{sessionId}:audience";

    private static string ParticipantGroup(Guid sessionId, Guid participantId)
        => $"session:{sessionId}:participant:{participantId}";

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, string resourceType, Guid resourceId)
        => new { organizationSlug, resourceType, resourceId };

    private static object BodyForParticipant(string? organizationSlug, string resourceType, Guid resourceId, Guid participantId)
        => new { organizationSlug, resourceType, resourceId, participantId };

    private static async Task<HttpResponseMessage> PostRevealAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    /// <summary>Seeds an org + a caller with the given role in both org and workspace + a Live session, all in org A.</summary>
    private static async Task<SeedResult> SeedSessionAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
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
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a recording <see cref="IRealtimeBackplane"/>
    /// for the production in-process one (so the test can read which server-managed groups an event was
    /// delivered to) AND wraps the production <see cref="IVisibilityRuleRepository"/> with a decorator that
    /// COUNTS its rule lookups (so the test can prove the per-reveal query count is independent of audience
    /// size, CORE-PERF-001). Only those two seams are swapped; every other production behavior (auth, tenant
    /// resolution, the publisher, the recipient resolver, the Visibility engine, the rule persistence) runs
    /// unchanged — the decorator delegates to the real repository.
    /// </summary>
    private sealed class RecordingDeliveryApiFactory : WorkspaceApiFactory
    {
        public RecordingRealtimeBackplane Backplane { get; } = new();

        public RuleLookupCounter RuleLookups { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRealtimeBackplane>();
                services.AddSingleton<IRealtimeBackplane>(Backplane);

                // Wrap the production visibility-rule repository with a counting decorator so the test can
                // assert how many rule lookups a reveal issues. It delegates every call to the real
                // repository, so persistence behavior is unchanged.
                services.RemoveAll<IVisibilityRuleRepository>();
                services.AddScoped<IVisibilityRuleRepository>(sp =>
                    new CountingVisibilityRuleRepository(
                        new VisibilityRuleRepository(sp.GetRequiredService<LiveCoreDbContext>()),
                        RuleLookups));
            });
        }
    }

    /// <summary>A thread-safe counter of rule lookups (<see cref="IVisibilityRuleRepository.ListByResourceAsync"/>).</summary>
    private sealed class RuleLookupCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>
    /// A pass-through <see cref="IVisibilityRuleRepository"/> that counts only the per-resource rule lookups
    /// (<see cref="ListByResourceAsync"/>) — the query the reveal command and the realtime recipient
    /// resolution both issue — and delegates everything else to the real repository unchanged.
    /// </summary>
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

    private sealed class RecordingRealtimeBackplane : IRealtimeBackplane
    {
        private readonly object _gate = new();
        private readonly List<string> _groups = [];

        /// <summary>The server-managed groups every delivery targeted, in arrival order (thread-safe snapshot).</summary>
        public IReadOnlyList<string> Groups()
        {
            lock (_gate)
            {
                return _groups.ToArray();
            }
        }

        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _groups.Add(group);
            }

            return Task.CompletedTask;
        }
    }
}
