// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the IN-MEMORY participant-visible feed (CORE-PERF-004,
/// <c>GET /api/v1/participants/{participantId}/visible-feed</c>). They drive the REAL application over real
/// HTTP through a factory that wraps ONE seam with a recording decorator (delegating to the real
/// implementation): the visibility-rule repository, so the test counts the rule lookups the feed issues
/// (the WORKSPACE load vs the per-resource load). Everything else — auth, tenant resolution, the
/// VisibilityPreviewService / VisibilityPolicy engine — runs unchanged.
///
/// The story's required integration tests:
/// <list type="bullet">
///   <item>the feed issues a BOUNDED query count independent of resource volume (asserted): exactly ONE
///   workspace-rule load and ZERO per-resource loads, whether the session has 2 or many ruled resources
///   (the old per-candidate path issued <c>1 + M</c> lookups);</item>
///   <item>the feed CONTENTS are unchanged vs the previous per-candidate computation: a mixed workspace
///   returns exactly the audience-wide and own-private visible resources — never a Hidden, a foreign-session
///   or another participant's private reveal — through the single-load path;</item>
///   <item>the new index <c>visibility_rules(workspace_id, resource_type, resource_id)</c> exists (migration
///   asserted).</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ParticipantVisibleFeedBoundedQueryTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _subject = "participant-a";

    private static readonly System.Text.Json.JsonSerializerOptions _json = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_feed_issues_a_bounded_query_count_independent_of_resource_volume()
    {
        // CORE-PERF-004 headline scale proof: the per-feed visibility-rule lookup is BOUNDED — exactly ONE
        // workspace load and ZERO per-resource loads — regardless of how many ruled resources the session
        // carries (the old per-candidate resolve did one ListByResource per candidate, i.e. 1 + M lookups).
        var few = await MeasureFeedRuleLookupsAsync(audienceWideResources: 2);
        var many = await MeasureFeedRuleLookupsAsync(audienceWideResources: 30);

        // Exactly one workspace-rule load and no per-resource loads, the SAME regardless of resource volume.
        Assert.Equal(1, few.WorkspaceLoads);
        Assert.Equal(0, few.ResourceLoads);
        Assert.Equal(few.WorkspaceLoads, many.WorkspaceLoads);
        Assert.Equal(few.ResourceLoads, many.ResourceLoads);

        // The feed still returned every visible resource in each case (CORE-PERF-004: the bound is not
        // achieved by dropping resources from the feed).
        Assert.Equal(2, few.ItemCount);
        Assert.Equal(30, many.ItemCount);
    }

    [Fact]
    public async Task The_feed_contents_are_unchanged_through_the_single_load_path()
    {
        // Equivalence at the HTTP boundary: a mixed workspace returns EXACTLY the audience-wide and
        // own-private visible resources — never a Hidden one, a reveal in a concurrent session, or another
        // participant's private reveal — computed through the single-load in-memory gate, in ONE workspace
        // lookup.
        await using var factory = new RecordingFeedApiFactory();
        Guid participantId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid audienceWide = Guid.Empty;
        Guid privateToMe = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            var other = await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: null, displayName: "Other");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            participantId = participant.Id;
            sessionId = session.Id;

            audienceWide = Guid.CreateVersion7();
            await db.AddVisibilityRuleAsync(org.Id, workspace.Id, session.Id, VisibilityResourceType.Scene, audienceWide, VisibilityState.Visible);

            privateToMe = Guid.CreateVersion7();
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, privateToMe, participant.Id, VisibilityState.Visible);

            // Excluded: hidden, private-to-other, and a reveal in a concurrent session.
            await db.AddVisibilityRuleAsync(org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, Guid.CreateVersion7(), VisibilityState.Hidden);
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, Guid.CreateVersion7(), other.Id, VisibilityState.Visible);
            var otherSession = await db.AddSessionAsync(org.Id, workspace.Id, "Other Session", SessionStatus.Live);
            await db.AddVisibilityRuleAsync(org.Id, workspace.Id, otherSession.Id, VisibilityResourceType.Scene, Guid.CreateVersion7(), VisibilityState.Visible);
        });

        factory.RuleLookups.Reset();
        using var client = factory.CreateClientFor(_subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}&sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);

        var actual = feed.Items.Select(item => (item.ResourceType, item.ResourceId)).ToHashSet();
        Assert.Equal(
            new HashSet<(string, Guid)>
            {
                ("Scene", audienceWide),
                ("ContentBlock", privateToMe),
            },
            actual);

        // Still a single workspace-rule load, no per-resource loads.
        Assert.Equal(1, factory.RuleLookups.WorkspaceLoads);
        Assert.Equal(0, factory.RuleLookups.ResourceLoads);
    }

    [Fact]
    public void The_resource_cleanup_index_is_created_by_a_migration()
    {
        // The documented resource-cleanup index visibility_rules(workspace_id, resource_type, resource_id)
        // (CORE-PERF-004) is created by the checked-in migration — asserted by inspecting the migration's Up
        // operations directly (the migration, not just the model).
        var migration = new LiveCore.Api.Persistence.Migrations.AddVisibilityRuleResourceCleanupIndex();

        var createIndex = Assert.Single(
            migration.UpOperations.OfType<CreateIndexOperation>(),
            operation => operation.Name == "ix_visibility_rules_workspace_id_resource_type_resource_id");

        Assert.Equal("visibility_rules", createIndex.Table);
        Assert.Equal(new[] { "workspace_id", "resource_type", "resource_id" }, createIndex.Columns);
        Assert.False(createIndex.IsUnique);
    }

    [Fact]
    public async Task The_resource_cleanup_index_exists_in_the_running_schema()
    {
        // The same index is present on the live VisibilityRule model the host runs against (the schema the
        // migration produces; the model<->migration consistency test ties the two together).
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var entityType = context.Model.FindEntityType(typeof(VisibilityRule));
        Assert.NotNull(entityType);
        var index = entityType.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "ix_visibility_rules_workspace_id_resource_type_resource_id");

        Assert.NotNull(index);
        Assert.Equal(
            new[] { "WorkspaceId", "ResourceType", "ResourceId" },
            index.Properties.Select(property => property.Name).ToArray());
        Assert.False(index.IsUnique);
    }

    // =====================================================================
    // Measurement helper.
    // =====================================================================

    private static async Task<FeedMeasurement> MeasureFeedRuleLookupsAsync(int audienceWideResources)
    {
        await using var factory = new RecordingFeedApiFactory();
        Guid participantId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            participantId = participant.Id;
            sessionId = session.Id;

            // A varying number of audience-wide visible resources in the session: the feed must compute the
            // visible set without one rule query per resource.
            for (var index = 0; index < audienceWideResources; index++)
            {
                await db.AddVisibilityRuleAsync(
                    org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, Guid.CreateVersion7(), VisibilityState.Visible);
            }
        });

        // Only the feed request itself is measured — reset after seeding.
        factory.RuleLookups.Reset();

        using var client = factory.CreateClientFor(_subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}&sessionId={sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feed = await response.Content.ReadFromJsonAsync<FeedDto>(_json);
        Assert.NotNull(feed);

        return new FeedMeasurement(
            factory.RuleLookups.WorkspaceLoads,
            factory.RuleLookups.ResourceLoads,
            feed.Items.Count);
    }

    private readonly record struct FeedMeasurement(int WorkspaceLoads, int ResourceLoads, int ItemCount);

    private sealed record FeedDto(
        Guid ParticipantId,
        Guid WorkspaceId,
        IReadOnlyList<FeedItemDto> Items,
        DateTimeOffset GeneratedAt);

    private sealed record FeedItemDto(string ResourceType, Guid ResourceId);

    // =====================================================================
    // Recording factory + counting decorator.
    // =====================================================================

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that wraps the visibility-rule repository with a lookup COUNTER
    /// (delegating to the real implementation), so a test can prove the feed's query shape. No other
    /// production behavior is changed.
    /// </summary>
    private sealed class RecordingFeedApiFactory : WorkspaceApiFactory
    {
        public RuleLookupCounter RuleLookups { get; } = new();

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
            });
        }
    }

    private sealed class RuleLookupCounter
    {
        private int _workspaceLoads;
        private int _resourceLoads;

        public int WorkspaceLoads => Volatile.Read(ref _workspaceLoads);

        public int ResourceLoads => Volatile.Read(ref _resourceLoads);

        public void IncrementWorkspaceLoads() => Interlocked.Increment(ref _workspaceLoads);

        public void IncrementResourceLoads() => Interlocked.Increment(ref _resourceLoads);

        public void Reset()
        {
            Interlocked.Exchange(ref _workspaceLoads, 0);
            Interlocked.Exchange(ref _resourceLoads, 0);
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

        public Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
        {
            _counter.IncrementWorkspaceLoads();
            return _inner.ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken);
        }

        public Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            VisibilityResourceType resourceType,
            Guid resourceId,
            CancellationToken cancellationToken)
        {
            _counter.IncrementResourceLoads();
            return _inner.ListByResourceAsync(organizationId, workspaceId, sessionId, resourceType, resourceId, cancellationToken);
        }

        public Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            IReadOnlyCollection<Guid> resourceIds,
            CancellationToken cancellationToken)
        {
            _counter.IncrementResourceLoads();
            return _inner.ListByResourcesAsync(organizationId, workspaceId, sessionId, resourceIds, cancellationToken);
        }

        public Task<VisibilityRule?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => _inner.FindByIdAsync(organizationId, workspaceId, id, cancellationToken);

        public Task<VisibilityRule?> FindByIdInSessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, Guid id, CancellationToken cancellationToken)
            => _inner.FindByIdInSessionAsync(organizationId, workspaceId, sessionId, id, cancellationToken);

        public Task<IReadOnlyList<VisibilityRule>> ListPageBySessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId, int skip, int take, CancellationToken cancellationToken)
            => _inner.ListPageBySessionAsync(organizationId, workspaceId, sessionId, skip, take, cancellationToken);

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
}
