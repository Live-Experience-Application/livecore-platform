// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Tests for <see cref="EntitySearchService"/> (CORE-ENT-005, retrofitted by CORE-API-006 to apply the
/// REAL audience visibility filter, then by CORE-SVIS-004 to scope that filter to the caller's SESSION) —
/// entity search within a workspace WITH SESSION-SCOPED VISIBILITY FILTERING. They mirror the precedent of
/// <c>SessionParticipantJoinService</c> and <see cref="VisibilityPreviewService"/>'s tests: the service is
/// a plain decision service over the real EF Core repositories and the central
/// <see cref="VisibilityPolicy"/>, driven against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the tenant/workspace/session scoping, the type filter, the
/// deterministic ordering and the per-participant visibility decision all run against genuinely persisted
/// state.
///
/// The behaviors covered:
/// <list type="bullet">
///   <item>HOST view: the host-capable roles (Owner/Admin/Host/CoHost — "View host-only content" =
///   yes, docs/06_AUTHORIZATION_MATRIX.md) get every matching workspace entity, regardless of any
///   visibility rule or session.</item>
///   <item>AUDIENCE-PARTICIPANT view (CORE-API-006 + CORE-SVIS-004): an audience participant
///   (Participant/Observer with an identified participant AND an identified session) gets exactly the
///   entities REVEALED to them IN THAT SESSION — an audience-wide visible rule of that session, or a
///   visible rule of that session scoped to exactly them. The crown jewels: a participant never sees an
///   entity revealed only to a DIFFERENT participant (the selected-participant guarantee), nor one
///   revealed only in a SIBLING session of the same workspace (the cross-session guarantee; threats
///   T5/T3). Hidden, unruled and foreign-tenant entities are excluded.</item>
///   <item>FAIL-CLOSED (threats T1/T5): the audit role, any undefined role, and an audience role with
///   no identified participant or no identified session get the EMPTY view even when entities are
///   revealed — no entity existence leaks to a caller with no content-view standing.</item>
///   <item>FILTERING: the generic name substring (ordinal, case-insensitive) and the optional type
///   filter narrow both the host and the audience result.</item>
///   <item>TENANT/WORKSPACE ISOLATION (threat T5; organization boundary checked before workspace
///   boundary, docs/06): a search in one organization/workspace never returns another organization's or
///   another workspace's entities, in both directions, for both the host and the audience path.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every fixture name/value is GENERIC and
/// NEUTRAL ("alpha", "beta", "type-alpha"); no vertical vocabulary appears (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntitySearchServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";
    private const string _validValues = """{"label":"value"}""";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    public EntitySearchServiceTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step
        // still uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK
        // constraints in the model are genuinely exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private EntitySearchService CreateService(LiveCoreDbContext context)
    {
        var rules = new VisibilityRuleRepository(context);
        var policy = new VisibilityPolicy(rules);
        return new EntitySearchService(new EntityRepository(context), new VisibilityPreviewService(rules, policy));
    }

    /// <summary>
    /// Builds the service over a COUNTING visibility-rule repository (CORE-PERF-008), so a test can prove
    /// the audience path's rule-query shape: the WORKSPACE load (the single-load gate) vs the per-RESOURCE
    /// load (the old per-candidate fan-out). The entity repository is the real one; only the rule lookups
    /// are counted.
    /// </summary>
    private static EntitySearchService CreateServiceWithCounting(LiveCoreDbContext context, RuleLookupCounter counter)
    {
        var rules = new CountingVisibilityRuleRepository(new VisibilityRuleRepository(context), counter);
        var policy = new VisibilityPolicy(rules);
        return new EntitySearchService(new EntityRepository(context), new VisibilityPreviewService(rules, policy));
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<EntityType> SeedEntityTypeAsync(Guid organizationId, Guid workspaceId, string typeKey)
    {
        var entityType = EntityType.Create(organizationId, workspaceId, typeKey, "Display", "{}", _createdAt);
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        Assert.Equal(EntityTypeAddResult.Added, await repository.AddAsync(entityType, CancellationToken.None));
        return entityType;
    }

    private async Task<Entity> SeedEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name = "alpha",
        string values = _validValues)
    {
        var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, values, _createdAt);
        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.Equal(EntityAddResult.Added, await repository.AddAsync(entity, CancellationToken.None));
        return entity;
    }

    private async Task<Participant> SeedParticipantAsync(Guid organizationId, Guid workspaceId)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, "Participant", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            ParticipantAddResult.Added,
            await new ParticipantRepository(context).AddAsync(participant, CancellationToken.None));
        return participant;
    }

    /// <summary>Creates a fresh, distinct session of the workspace (NOT the per-workspace cached one).</summary>
    private async Task<Guid> SeedSessionAsync(Guid organizationId, Guid workspaceId, string title)
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>Lazily seeds and caches ONE session per workspace, so rules sharing a workspace share a session.</summary>
    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var sessionId = await SeedSessionAsync(organizationId, workspaceId, "Live Session");
        _sessionByWorkspace[workspaceId] = sessionId;
        return sessionId;
    }

    /// <summary>Seeds an AUDIENCE-WIDE visibility rule for the given resource (visible to everyone) in the cached session.</summary>
    private async Task SeedRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid resourceId,
        VisibilityState visibility)
    {
        var sessionId = await SessionIdAsync(organizationId, workspaceId);
        await SeedRuleInSessionAsync(organizationId, workspaceId, sessionId, resourceId, visibility);
    }

    /// <summary>Seeds an AUDIENCE-WIDE visibility rule for the given resource in a SPECIFIC session.</summary>
    private async Task SeedRuleInSessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid resourceId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(
            organizationId, workspaceId, sessionId, VisibilityResourceType.Entity, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            VisibilityRuleAddResult.Added,
            await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    /// <summary>Seeds a SELECTED-PARTICIPANT visibility rule (visible only to one participant) in the cached session.</summary>
    private async Task SeedParticipantRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid resourceId,
        Guid targetParticipantId,
        VisibilityState visibility)
    {
        var sessionId = await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.CreateForParticipant(
            organizationId, workspaceId, sessionId, VisibilityResourceType.Entity, resourceId, targetParticipantId, visibility, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            VisibilityRuleAddResult.Added,
            await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    /// <summary>Seeds one organization + workspace + entity type and returns their ids.</summary>
    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid EntityTypeId)> SeedWorkspaceWithTypeAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA,
        string typeKey = "type-alpha")
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, typeKey);
        return (organization.Id, workspace.Id, entityType.Id);
    }

    // --- Host view: host-capable roles get every matching entity --------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_capable_role_gets_every_matching_entity(MembershipRole role)
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var a = await SeedEntityAsync(org, ws, typeId, "alpha");
        var b = await SeedEntityAsync(org, ws, typeId, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        // The host path is session-agnostic (it sees every entity regardless of any reveal), so no session.
        var result = await service.SearchAsync(
            org, ws, sessionId: null, role, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.True(result.IsHostView);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == a.Id);
        Assert.Contains(result.Items, e => e.Id == b.Id);
    }

    [Fact]
    public async Task Host_capable_role_sees_every_entity_even_hidden_or_unruled_ones()
    {
        // A host sees host-only content: a hidden entity and an unruled entity are both returned. A
        // participant would see neither (see the audience tests) — this is the host/audience contrast.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var hidden = await SeedEntityAsync(org, ws, typeId, "hidden");
        var unruled = await SeedEntityAsync(org, ws, typeId, "unruled");
        await SeedRuleAsync(org, ws, hidden.Id, VisibilityState.Hidden);

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, sessionId: null, MembershipRole.Host, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == hidden.Id);
        Assert.Contains(result.Items, e => e.Id == unruled.Id);
    }

    [Fact]
    public async Task Host_view_returns_entities_in_deterministic_id_order()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var first = await SeedEntityAsync(org, ws, typeId, "first");
        var second = await SeedEntityAsync(org, ws, typeId, "second");
        var third = await SeedEntityAsync(org, ws, typeId, "third");

        // UUIDv7 ids are time-ordered; sort independently so the assertion is not coupled to UUIDv7
        // monotonicity (mirrors EntityRepositoryTests).
        var expected = new[] { first, second, third }
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToArray();

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(expected, result.Items.Select(entity => entity.Id).ToArray());
    }

    // --- Audience-participant view: only entities revealed to the participant --------

    [Fact]
    public async Task Audience_participant_sees_an_audience_wide_revealed_entity()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedEntityAsync(org, ws, typeId, "beta"); // unruled -> not visible
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        // The audience view is now the REAL filtered set (not a hard-coded empty set): the participant
        // sees the audience-wide revealed entity, and not the unruled one.
        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.False(result.IsHostView);
        Assert.Single(result.Items);
        Assert.Equal(revealed.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task An_observer_with_a_participant_id_is_filtered_like_a_participant()
    {
        // Observer is an audience role (docs/06): with an identified participant and session it takes the
        // same visibility-filtered path, so an audience-wide revealed entity is visible to it.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Observer, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Single(result.Items);
        Assert.Equal(revealed.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Audience_participant_sees_only_entities_revealed_to_them()
    {
        // THE crown jewel: an entity revealed ONLY to `selected` is in their result but NOT in a
        // different participant `other`'s result — the selected-participant guarantee (threat T5).
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var forSelected = await SeedEntityAsync(org, ws, typeId, "alpha");
        var forOther = await SeedEntityAsync(org, ws, typeId, "beta");
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        var other = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, forSelected.Id, selected, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, forOther.Id, other, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        var selectedResult = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, selected, EntitySearchCriteria.MatchAll, CancellationToken.None);
        var otherResult = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, other, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(selectedResult.Items);
        Assert.Equal(forSelected.Id, selectedResult.Items[0].Id);
        Assert.DoesNotContain(selectedResult.Items, e => e.Id == forOther.Id);

        Assert.Single(otherResult.Items);
        Assert.Equal(forOther.Id, otherResult.Items[0].Id);
        Assert.DoesNotContain(otherResult.Items, e => e.Id == forSelected.Id);
    }

    [Fact]
    public async Task Audience_participant_sees_only_entities_revealed_in_their_session()
    {
        // CORE-SVIS-004 crown jewel: a reveal is session-scoped, so a participant's entity search returns
        // only entities revealed IN THE SESSION IT NAMES. An entity revealed audience-wide in session A is
        // returned when the search names session A, but NOT a sibling session B of the same workspace — a
        // reveal in one session never leaks into a concurrent session (threat T5/T3).
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        await SeedRuleInSessionAsync(org, ws, sessionA, revealed.Id, VisibilityState.Visible);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var inA = await service.SearchAsync(
            org, ws, sessionA, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);
        var inB = await service.SearchAsync(
            org, ws, sessionB, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(inA.Items);
        Assert.Equal(revealed.Id, inA.Items[0].Id);

        // The sibling session sees nothing for the same entity — the reveal is bounded to session A.
        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, inB.View);
        Assert.Empty(inB.Items);
    }

    [Fact]
    public async Task Audience_participant_sees_both_audience_wide_and_their_own_private_reveal()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var audienceWide = await SeedEntityAsync(org, ws, typeId, "wide");
        var privateToSelected = await SeedEntityAsync(org, ws, typeId, "mine");
        var privateToOther = await SeedEntityAsync(org, ws, typeId, "theirs");
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        var other = (await SeedParticipantAsync(org, ws)).Id;
        await SeedRuleAsync(org, ws, audienceWide.Id, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, privateToSelected.Id, selected, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, privateToOther.Id, other, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, selected, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == audienceWide.Id);
        Assert.Contains(result.Items, e => e.Id == privateToSelected.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == privateToOther.Id);
    }

    [Fact]
    public async Task Audience_participant_does_not_see_hidden_or_unruled_entities()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var unruled = await SeedEntityAsync(org, ws, typeId, "unruled");
        var hidden = await SeedEntityAsync(org, ws, typeId, "hidden");
        var visible = await SeedEntityAsync(org, ws, typeId, "visible");
        await SeedRuleAsync(org, ws, hidden.Id, VisibilityState.Hidden);
        await SeedRuleAsync(org, ws, visible.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(visible.Id, result.Items[0].Id);
        Assert.DoesNotContain(result.Items, e => e.Id == unruled.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == hidden.Id);
    }

    [Fact]
    public async Task Audience_participant_with_a_hidden_private_reveal_sees_nothing()
    {
        // A participant-scoped rule that is Hidden grants nothing — fail-closed even for its target.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var entity = await SeedEntityAsync(org, ws, typeId, "alpha");
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        await SeedParticipantRuleAsync(org, ws, entity.Id, selected, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, selected, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Audience_participant_sees_nothing_when_no_entity_is_revealed()
    {
        // Entities exist but carry no rule (host-only by default): a participant sees nothing, and no
        // entity existence leaks. The view is the filtered audience view, not the host view.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedEntityAsync(org, ws, typeId, "beta");
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Audience_participant_view_combines_name_and_type_filters_with_visibility()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        // The only entity that is the right type AND matches the name AND is revealed.
        var match = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "shared-name");
        // Right name, revealed, but wrong type -> excluded by the type filter.
        var wrongType = await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "shared-name");
        // Right type, revealed, but wrong name -> excluded by the name filter.
        var wrongName = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "other");
        // Right type and name but NOT revealed -> excluded by the visibility filter.
        var notRevealed = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "shared-extra");
        await SeedRuleAsync(organization.Id, workspace.Id, match.Id, VisibilityState.Visible);
        await SeedRuleAsync(organization.Id, workspace.Id, wrongType.Id, VisibilityState.Visible);
        await SeedRuleAsync(organization.Id, workspace.Id, wrongName.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(organization.Id, workspace.Id);
        var participant = (await SeedParticipantAsync(organization.Id, workspace.Id)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            organization.Id,
            workspace.Id,
            session,
            MembershipRole.Participant,
            participant,
            EntitySearchCriteria.Create("shared", typeAlpha.Id),
            CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(match.Id, result.Items[0].Id);
        Assert.DoesNotContain(result.Items, e => e.Id == wrongType.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == wrongName.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == notRevealed.Id);
    }

    // --- Fail-closed: no content-view standing --------------------------------------

    [Fact]
    public async Task Auditor_fails_closed_even_when_an_entity_is_revealed()
    {
        // Auditor is "audit-only" on the content rows (docs/06), so it is NOT an audience role: even
        // with a participant id, a session and an audience-wide visible entity, it gets the empty view.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Auditor, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Undefined_role_fails_closed_even_when_an_entity_is_revealed()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, session, (MembershipRole)999, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, result.View);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_without_a_participant_fails_closed(MembershipRole role)
    {
        // An audience role with NO identified participant has no participant to compute visibility for,
        // so it fails closed to the empty view even when an entity is revealed audience-wide in the named
        // session — and a null vs an empty participant id behave identically (fail-closed, never a throw).
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        var nullResult = await service.SearchAsync(
            org, ws, session, role, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);
        var emptyResult = await service.SearchAsync(
            org, ws, session, role, Guid.Empty, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, nullResult.View);
        Assert.Empty(nullResult.Items);
        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, emptyResult.View);
        Assert.Empty(emptyResult.Items);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_without_a_session_fails_closed(MembershipRole role)
    {
        // A reveal is session-scoped (CORE-SVIS-004), so an audience role with an identified participant
        // but NO identified session has nothing to compute against and fails closed to the empty view —
        // even when an entity is revealed audience-wide. A null vs an empty session id behave identically.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        await SeedRuleAsync(org, ws, revealed.Id, VisibilityState.Visible);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var nullResult = await service.SearchAsync(
            org, ws, sessionId: null, role, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);
        var emptyResult = await service.SearchAsync(
            org, ws, Guid.Empty, role, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, nullResult.View);
        Assert.Empty(nullResult.Items);
        Assert.Equal(EntitySearchView.AudienceVisibilityFiltered, emptyResult.View);
        Assert.Empty(emptyResult.Items);
    }

    // --- Name filter (host view) ----------------------------------------------------

    [Fact]
    public async Task Name_filter_matches_a_case_insensitive_substring()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var alpha = await SeedEntityAsync(org, ws, typeId, "alpha");
        var alphabet = await SeedEntityAsync(org, ws, typeId, "Alphabet");
        await SeedEntityAsync(org, ws, typeId, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        // "ALP" matches "alpha" and "Alphabet" case-insensitively, not "beta".
        var result = await service.SearchAsync(
            org, ws, sessionId: null, MembershipRole.Host, participantId: null, EntitySearchCriteria.Create("ALP"), CancellationToken.None);

        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == alpha.Id);
        Assert.Contains(result.Items, e => e.Id == alphabet.Id);
    }

    [Fact]
    public async Task Name_filter_with_no_match_returns_an_empty_host_view()
    {
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        await SeedEntityAsync(org, ws, typeId, "alpha");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            org, ws, sessionId: null, MembershipRole.Host, participantId: null, EntitySearchCriteria.Create("gamma"), CancellationToken.None);

        // No match is still the HOST view (the caller is host-capable) — just empty. This is
        // distinct from the empty AUDIENCE view, which a non-host role receives regardless of matches.
        Assert.Equal(EntitySearchView.HostOnlyContent, result.View);
        Assert.Empty(result.Items);
    }

    // --- Type filter (host view) ----------------------------------------------------

    [Fact]
    public async Task Type_filter_restricts_to_one_entity_type()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var alpha1 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-1");
        var alpha2 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-2");
        var beta1 = await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "beta-1");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            organization.Id,
            workspace.Id,
            sessionId: null,
            MembershipRole.Owner,
            participantId: null,
            EntitySearchCriteria.Create(entityTypeId: typeAlpha.Id),
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Id == alpha1.Id);
        Assert.Contains(result.Items, e => e.Id == alpha2.Id);
        Assert.DoesNotContain(result.Items, e => e.Id == beta1.Id);
    }

    [Fact]
    public async Task Type_and_name_filters_combine()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var match = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "shared-name");
        // Same name, different type -> excluded by the type filter.
        await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "shared-name");
        // Same type, different name -> excluded by the name filter.
        await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "other");

        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.SearchAsync(
            organization.Id,
            workspace.Id,
            sessionId: null,
            MembershipRole.Owner,
            participantId: null,
            EntitySearchCriteria.Create("shared", typeAlpha.Id),
            CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(match.Id, result.Items[0].Id);
    }

    // --- Tenant + workspace isolation (host view) -----------------------------------

    [Fact]
    public async Task Host_search_never_returns_another_organizations_entities()
    {
        // Tenant A with an entity, tenant B with its own entity.
        var (orgA, wsA, typeA) = await SeedWorkspaceWithTypeAsync(_organizationSlugA, _workspaceSlugA, "type-alpha");
        var entityA = await SeedEntityAsync(orgA, wsA, typeA, "alpha");
        var (orgB, wsB, typeB) = await SeedWorkspaceWithTypeAsync(_organizationSlugB, _workspaceSlugB, "type-beta");
        var entityB = await SeedEntityAsync(orgB, wsB, typeB, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        // Searching tenant A's workspace returns only A's entity, never B's — even as Owner.
        var resultA = await service.SearchAsync(
            orgA, wsA, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Single(resultA.Items);
        Assert.Equal(entityA.Id, resultA.Items[0].Id);

        // And the reverse: tenant B's workspace returns only B's entity.
        var resultB = await service.SearchAsync(
            orgB, wsB, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Single(resultB.Items);
        Assert.Equal(entityB.Id, resultB.Items[0].Id);

        // Cross-tenant addressing is hidden: tenant B's id with tenant A's workspace returns nothing
        // (the organization boundary is checked before the workspace boundary; threat T5).
        var crossTenant = await service.SearchAsync(
            orgB, wsA, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Empty(crossTenant.Items);
    }

    [Fact]
    public async Task Host_search_never_returns_another_workspaces_entities_in_the_same_organization()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceX = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceY = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeX = await SeedEntityTypeAsync(organization.Id, workspaceX.Id, "type-alpha");
        var typeY = await SeedEntityTypeAsync(organization.Id, workspaceY.Id, "type-beta");
        var entityX = await SeedEntityAsync(organization.Id, workspaceX.Id, typeX.Id, "alpha");
        var entityY = await SeedEntityAsync(organization.Id, workspaceY.Id, typeY.Id, "beta");

        await using var context = CreateContext();
        var service = CreateService(context);

        var resultX = await service.SearchAsync(
            organization.Id, workspaceX.Id, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(resultX.Items);
        Assert.Equal(entityX.Id, resultX.Items[0].Id);
        Assert.DoesNotContain(resultX.Items, e => e.Id == entityY.Id);
    }

    // --- Tenant + workspace isolation (audience-participant view) --------------------

    [Fact]
    public async Task Audience_participant_search_never_returns_another_tenants_entities()
    {
        // Tenant A and tenant B each have an entity revealed audience-wide in their own workspace.
        var (orgA, wsA, typeA) = await SeedWorkspaceWithTypeAsync(_organizationSlugA, _workspaceSlugA, "type-alpha");
        var entityA = await SeedEntityAsync(orgA, wsA, typeA, "alpha");
        await SeedRuleAsync(orgA, wsA, entityA.Id, VisibilityState.Visible);
        var sessionInA = await SessionIdAsync(orgA, wsA);
        var participantA = (await SeedParticipantAsync(orgA, wsA)).Id;

        var (orgB, wsB, typeB) = await SeedWorkspaceWithTypeAsync(_organizationSlugB, _workspaceSlugB, "type-beta");
        var entityB = await SeedEntityAsync(orgB, wsB, typeB, "beta");
        await SeedRuleAsync(orgB, wsB, entityB.Id, VisibilityState.Visible);

        await using var context = CreateContext();
        var service = CreateService(context);

        // Tenant A's participant searching A's workspace sees only A's entity, never B's.
        var resultA = await service.SearchAsync(
            orgA, wsA, sessionInA, MembershipRole.Participant, participantA, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Single(resultA.Items);
        Assert.Equal(entityA.Id, resultA.Items[0].Id);
        Assert.DoesNotContain(resultA.Items, e => e.Id == entityB.Id);

        // Cross-tenant addressing is hidden: tenant B's id with tenant A's workspace returns nothing
        // (the organization boundary is checked before the workspace boundary; threat T5).
        var crossTenant = await service.SearchAsync(
            orgB, wsA, sessionInA, MembershipRole.Participant, participantA, EntitySearchCriteria.MatchAll, CancellationToken.None);
        Assert.Empty(crossTenant.Items);
    }

    [Fact]
    public async Task Audience_participant_search_never_returns_another_workspaces_entities()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceX = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceY = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeX = await SeedEntityTypeAsync(organization.Id, workspaceX.Id, "type-alpha");
        var typeY = await SeedEntityTypeAsync(organization.Id, workspaceY.Id, "type-beta");
        var entityX = await SeedEntityAsync(organization.Id, workspaceX.Id, typeX.Id, "alpha");
        var entityY = await SeedEntityAsync(organization.Id, workspaceY.Id, typeY.Id, "beta");
        await SeedRuleAsync(organization.Id, workspaceX.Id, entityX.Id, VisibilityState.Visible);
        await SeedRuleAsync(organization.Id, workspaceY.Id, entityY.Id, VisibilityState.Visible);
        var sessionInX = await SessionIdAsync(organization.Id, workspaceX.Id);
        var participantX = (await SeedParticipantAsync(organization.Id, workspaceX.Id)).Id;

        await using var context = CreateContext();
        var service = CreateService(context);

        var resultX = await service.SearchAsync(
            organization.Id, workspaceX.Id, sessionInX, MembershipRole.Participant, participantX, EntitySearchCriteria.MatchAll, CancellationToken.None);

        Assert.Single(resultX.Items);
        Assert.Equal(entityX.Id, resultX.Items[0].Id);
        Assert.DoesNotContain(resultX.Items, e => e.Id == entityY.Id);
    }

    // --- CORE-PERF-008: single-load in-memory gate (bounded query count + equivalence) ---

    [Theory]
    [InlineData(2)]
    [InlineData(30)]
    public async Task Audience_search_issues_a_bounded_rule_query_count_independent_of_entity_volume(int entityCount)
    {
        // CORE-PERF-008 headline scale proof: the audience path issues EXACTLY ONE workspace-rule load and
        // ZERO per-resource loads, the SAME whether the session has 2 or 30 revealed entities. The old
        // per-candidate path issued one ListByResource per candidate (1 + M lookups), so this asserts O(1)
        // rule queries, not O(N).
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var session = await SessionIdAsync(org, ws);
        var participant = (await SeedParticipantAsync(org, ws)).Id;
        for (var index = 0; index < entityCount; index++)
        {
            var entity = await SeedEntityAsync(org, ws, typeId, $"entity-{index}");
            await SeedRuleInSessionAsync(org, ws, session, entity.Id, VisibilityState.Visible);
        }

        await using var context = CreateContext();
        var counter = new RuleLookupCounter();
        var service = CreateServiceWithCounting(context, counter);

        var result = await service.SearchAsync(
            org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None);

        // The bound is not achieved by dropping entities from the result: every revealed entity is returned.
        Assert.Equal(entityCount, result.Items.Count);

        // Exactly one workspace-rule load and no per-resource loads, INDEPENDENT of entity volume.
        Assert.Equal(1, counter.WorkspaceLoads);
        Assert.Equal(0, counter.ResourceLoads);
    }

    [Fact]
    public async Task Audience_search_matches_the_per_candidate_computation_for_a_mixed_workspace()
    {
        // CORE-PERF-008 equivalence: the single-load in-memory result is byte-for-byte the OLD per-candidate
        // computation for a workspace mixing an audience-wide reveal, a selected-participant private reveal
        // (the crown jewel), a private reveal to ANOTHER participant, a hidden reveal and an unruled entity —
        // asserted for BOTH the selected participant and a different one.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var audienceWide = await SeedEntityAsync(org, ws, typeId, "wide");
        var privateToSelected = await SeedEntityAsync(org, ws, typeId, "mine");
        var privateToOther = await SeedEntityAsync(org, ws, typeId, "theirs");
        var hidden = await SeedEntityAsync(org, ws, typeId, "hidden");
        await SeedEntityAsync(org, ws, typeId, "unruled");
        var selected = (await SeedParticipantAsync(org, ws)).Id;
        var other = (await SeedParticipantAsync(org, ws)).Id;
        await SeedRuleAsync(org, ws, audienceWide.Id, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, privateToSelected.Id, selected, VisibilityState.Visible);
        await SeedParticipantRuleAsync(org, ws, privateToOther.Id, other, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, hidden.Id, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        foreach (var participant in new[] { selected, other })
        {
            await using var context = CreateContext();
            var service = CreateService(context);

            var actual = (await service.SearchAsync(
                    org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None))
                .Items.Select(entity => entity.Id).ToHashSet();

            var expected = await PerCandidateVisibleEntityIdsAsync(org, ws, session, participant);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Audience_search_matches_the_per_candidate_computation_across_sessions()
    {
        // CORE-PERF-008 equivalence, cross-session: an entity revealed audience-wide in session A matches the
        // per-candidate computation when searching A, and is equally absent (under both algorithms) when
        // searching a sibling session B of the same workspace — the cross-session guarantee.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var revealed = await SeedEntityAsync(org, ws, typeId, "alpha");
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        await SeedRuleInSessionAsync(org, ws, sessionA, revealed.Id, VisibilityState.Visible);
        var participant = (await SeedParticipantAsync(org, ws)).Id;

        foreach (var session in new[] { sessionA, sessionB })
        {
            await using var context = CreateContext();
            var service = CreateService(context);

            var actual = (await service.SearchAsync(
                    org, ws, session, MembershipRole.Participant, participant, EntitySearchCriteria.MatchAll, CancellationToken.None))
                .Items.Select(entity => entity.Id).ToHashSet();

            var expected = await PerCandidateVisibleEntityIdsAsync(org, ws, session, participant);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Host_search_returns_the_full_candidate_set_regardless_of_rules()
    {
        // CORE-PERF-008 equivalence, host case: the host path is unchanged by the refactor — it never
        // consults the visibility gate, so it still returns every matching entity regardless of any reveal.
        // Equivalence here is that the host result equals the full workspace candidate set.
        var (org, ws, typeId) = await SeedWorkspaceWithTypeAsync();
        var a = await SeedEntityAsync(org, ws, typeId, "alpha");
        var b = await SeedEntityAsync(org, ws, typeId, "beta");
        var c = await SeedEntityAsync(org, ws, typeId, "gamma");
        // Rules of every flavour exist but must not affect the host view.
        await SeedRuleAsync(org, ws, a.Id, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, b.Id, VisibilityState.Hidden);
        var session = await SessionIdAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        var actual = (await service.SearchAsync(
                org, ws, session, MembershipRole.Host, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None))
            .Items.Select(entity => entity.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { a.Id, b.Id, c.Id }, actual);
    }

    /// <summary>
    /// The OLD per-candidate computation (CORE-API-006): every workspace entity routed individually through
    /// <see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>
    /// — the algorithm CORE-PERF-008 replaces with the single-load in-memory gate. Used as the equivalence
    /// reference: the new result must match this set exactly.
    /// </summary>
    private async Task<HashSet<Guid>> PerCandidateVisibleEntityIdsAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
    {
        await using var context = CreateContext();
        var policy = new VisibilityPolicy(new VisibilityRuleRepository(context));
        var entities = await new EntityRepository(context)
            .ListByWorkspaceAsync(organizationId, workspaceId, CancellationToken.None);

        var visible = new HashSet<Guid>();
        foreach (var entity in entities)
        {
            var canView = await policy.CanParticipantViewResourceAsync(
                organizationId, workspaceId, sessionId, participantId, VisibilityResourceType.Entity, entity.Id, CancellationToken.None);
            if (canView)
            {
                visible.Add(entity.Id);
            }
        }

        return visible;
    }

    // --- Guards ---------------------------------------------------------------------

    [Fact]
    public async Task Empty_organization_id_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            Guid.Empty, Guid.NewGuid(), sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None));
        Assert.Equal("organizationId", exception.ParamName);
    }

    [Fact]
    public async Task Empty_workspace_id_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            Guid.NewGuid(), Guid.Empty, sessionId: null, MembershipRole.Owner, participantId: null, EntitySearchCriteria.MatchAll, CancellationToken.None));
        Assert.Equal("workspaceId", exception.ParamName);
    }

    [Fact]
    public async Task Null_criteria_is_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SearchAsync(
            Guid.NewGuid(), Guid.NewGuid(), sessionId: null, MembershipRole.Owner, participantId: null, null!, CancellationToken.None));
    }

    // --- Counting visibility-rule repository (CORE-PERF-008 query-shape proof) -------

    /// <summary>
    /// Counts the WORKSPACE-level rule loads (the single-load gate) and the per-RESOURCE rule loads (the old
    /// per-candidate fan-out) a search issues, so the bounded-query test can assert the audience path's
    /// query shape. Thread-safe so it is robust even if a future change parallelizes the gate.
    /// </summary>
    private sealed class RuleLookupCounter
    {
        private int _workspaceLoads;
        private int _resourceLoads;

        public int WorkspaceLoads => Volatile.Read(ref _workspaceLoads);

        public int ResourceLoads => Volatile.Read(ref _resourceLoads);

        public void IncrementWorkspaceLoads() => Interlocked.Increment(ref _workspaceLoads);

        public void IncrementResourceLoads() => Interlocked.Increment(ref _resourceLoads);
    }

    /// <summary>
    /// Wraps a real <see cref="IVisibilityRuleRepository"/> and counts the rule lookups (delegating to the
    /// real implementation), so a test can prove the search's query shape (CORE-PERF-008). The
    /// workspace-scoped <see cref="ListByWorkspaceAsync"/> (the single-load gate) and the per-resource
    /// <see cref="ListByResourceAsync"/> / <see cref="ListByResourcesAsync"/> (the old fan-out) are counted
    /// separately; every other member delegates straight through.
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

        public Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(
            Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
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
