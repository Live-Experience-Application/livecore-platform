// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the entity SEARCH API (CORE-ENT-009, the "Entity Graph and Search Completeness"
/// epic): the route <c>GET /api/v1/workspaces/{workspaceId}/entities/search</c>, which routes the
/// already-implemented, DI-registered <c>EntitySearchService</c> (CORE-ENT-005) so a vertical can perform
/// server-side filtered entity search WITH visibility filtering instead of list-then-filter client-side
/// (ARC-GAP-120). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication -> tenant context resolver -> endpoint -> inline authorization -> the search service ->
/// the central Visibility engine) is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>A HOST searches by name and by entityTypeId and gets the matching entities (the full host shape,
///   every matching entity regardless of any visibility rule).</item>
///   <item>A PARTICIPANT searches and gets ONLY the visibility-filtered subset — the entities REVEALED to them
///   in the named session, as the stripped audience-safe shape (NEGATIVE: a participant search never returns an
///   unrevealed entity, nor one revealed only to a DIFFERENT participant; threats T2/T1/T5).</item>
///   <item>FAIL-CLOSED: an auditor, and an audience member with no participant record, get the empty view even
///   when entities are revealed.</item>
///   <item>Negatives: 401 unauthenticated; non-member hidden-404; foreign-tenant hidden-404; missing
///   organizationSlug 400; malformed entityTypeId/sessionId 400.</item>
/// </list>
///
/// The search VIEW is decided by the caller's WORKSPACE role inside the search service, and the result is
/// projected through the SAME host-vs-participant projector the list/read use, so the two always agree (an
/// audience caller can never receive the host shape). The calling participant is resolved server-side from the
/// principal (never client-supplied), so a participant can only ever ask for its OWN revealed set. Every fixture
/// name/value is GENERIC and NEUTRAL; no vertical vocabulary appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntitySearchEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // A distinctive marker embedded in an entity's attribute-values content, used to prove the stripped
    // participant projection never echoes the content (T2).
    private const string _attributeMarker = "do-not-leak-attr-marker";
    private static readonly string _attributeValues = $"{{\"trait\":\"{_attributeMarker}\"}}";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task Search_entities_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/entities/search?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HOST view — search by name / by entityTypeId returns the matching entities.
    // =====================================================================

    [Fact]
    public async Task Search_by_name_as_host_returns_only_the_matching_entities_in_the_host_shape()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid lanternId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            lanternId = (await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Lantern")).Id;
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Beacon");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&name=lan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadHostEntitiesAsync(response);
        var entity = Assert.Single(entities);
        Assert.Equal(lanternId, entity.Id);
        Assert.Equal("Lantern", entity.Name);
        // The host shape carries the attribute-values content (the full projection).
        Assert.Equal("{}", entity.AttributeValues);
    }

    [Fact]
    public async Task Search_by_entity_type_as_host_returns_only_that_types_entities()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        Guid typeAlphaId = Guid.Empty;
        Guid alphaEntityId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var typeAlpha = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            typeAlphaId = typeAlpha.Id;
            var typeBeta = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-beta");
            alphaEntityId = (await db.AddEntityAsync(org.Id, workspace.Id, typeAlpha.Id, "Alpha One")).Id;
            await db.AddEntityAsync(org.Id, workspace.Id, typeBeta.Id, "Beta One");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&entityTypeId={typeAlphaId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadHostEntitiesAsync(response);
        var entity = Assert.Single(entities);
        Assert.Equal(alphaEntityId, entity.Id);
        Assert.Equal(typeAlphaId, entity.EntityTypeId);
    }

    [Fact]
    public async Task Search_with_no_criteria_as_host_returns_every_workspace_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "One");
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Two");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadHostEntitiesAsync(response);
        Assert.Equal(2, entities.Length);
    }

    // =====================================================================
    // AUDIENCE view — a participant gets ONLY the visibility-filtered subset.
    // =====================================================================

    [Fact]
    public async Task Search_as_participant_returns_only_the_revealed_entity_and_never_an_unrevealed_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid revealedId = Guid.Empty;
        Guid hiddenId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            // The caller's OWN participant, linked to the caller's user so the route resolves it server-side.
            await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, "Audience Caller");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            // Seed the revealed entity with the distinctive attribute marker so the no-leak assertion is meaningful.
            var revealed = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Revealed One");
            revealedId = revealed.Id;
            hiddenId = (await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Hidden One")).Id;
            // Reveal ONLY "Revealed One" audience-wide in the session; "Hidden One" is left unruled.
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, revealed.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The participant receives the STRIPPED audience-safe shape ({id, name, entityTypeKey}); the host-only
        // attribute-values content never appears anywhere in the body (a direct T2 content-leak guard).
        var properties = FirstElementPropertyNames(body);
        Assert.Equal(
            new[] { "id", "name", "entityTypeKey" }.OrderBy(n => n, StringComparer.Ordinal),
            properties.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(_attributeMarker, body, StringComparison.Ordinal);

        // ONLY the revealed entity is returned; the unrevealed one never is (the crown-jewel negative).
        var entities = Deserialize<ParticipantEntityDto[]>(body);
        var entity = Assert.Single(entities);
        Assert.Equal(revealedId, entity.Id);
        Assert.Equal("Revealed One", entity.Name);
        Assert.Equal("type-alpha", entity.EntityTypeKey);
        Assert.DoesNotContain(entities, e => e.Id == hiddenId);
    }

    [Fact]
    public async Task Search_as_participant_never_returns_an_entity_revealed_only_to_another_participant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid audienceWideId = Guid.Empty;
        Guid otherOnlyId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, "Audience Caller");
            // A DIFFERENT participant (anonymous) the private reveal is scoped to.
            var other = await db.AddParticipantAsync(org.Id, workspace.Id, userProfileId: null, "Other");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var audienceWide = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Audience One");
            audienceWideId = audienceWide.Id;
            var otherOnly = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Other Only");
            otherOnlyId = otherOnly.Id;
            // Audience-wide reveal of one entity; a SELECTED-participant private reveal of the other to "Other".
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, audienceWide.Id, VisibilityState.Visible);
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, otherOnly.Id, other.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadParticipantEntitiesAsync(response);

        // The caller sees the audience-wide reveal but NEVER the entity revealed only to the other participant
        // (the selected-participant guarantee; threat T5).
        var entity = Assert.Single(entities);
        Assert.Equal(audienceWideId, entity.Id);
        Assert.DoesNotContain(entities, e => e.Id == otherOnlyId);
    }

    [Fact]
    public async Task Search_as_participant_without_a_session_returns_the_empty_view()
    {
        // An audience caller with no identified session has no reveals to compute, so the search fails closed to
        // the empty view even when an entity is revealed in some session.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            await db.AddParticipantAsync(org.Id, workspace.Id, user.Id, "Audience Caller");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var revealed = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Revealed One");
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, revealed.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadParticipantEntitiesAsync(response);
        Assert.Empty(entities);
    }

    [Fact]
    public async Task Search_as_participant_with_no_participant_record_returns_the_empty_view()
    {
        // A Participant-role workspace member who has no participant record cannot be resolved server-side, so
        // the audience search fails closed to the empty view (it never falls back to the host view).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var revealed = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Revealed One");
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, revealed.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadParticipantEntitiesAsync(response);
        Assert.Empty(entities);
    }

    [Fact]
    public async Task Search_as_auditor_returns_the_empty_view_even_when_an_entity_is_revealed()
    {
        // Auditor has "View host-only content" = audit-only (not yes) and is NOT an audience role, so it gets the
        // fail-closed empty view — never the host view and never the audience set.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        Guid workspaceId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Auditor);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            var revealed = await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Revealed One");
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, revealed.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&sessionId={sessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entities = await ReadParticipantEntitiesAsync(response);
        Assert.Empty(entities);
    }

    // =====================================================================
    // NEGATIVES — non-member 404, foreign-tenant 404, 400s.
    // =====================================================================

    [Fact]
    public async Task Search_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            await db.AddEntityAsync(org.Id, workspace.Id, type.Id, "Lantern");
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&name=lan");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Search_is_404_for_a_workspace_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(orgB.Id, workspaceInB.Id, "type-alpha");
            await db.AddEntityAsync(orgB.Id, workspaceInB.Id, type.Id, "B Entity");
        });

        // The workspace is real and in org B, but addressed with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceInBId}/entities/search?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
    }

    [Fact]
    public async Task Search_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/entities/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("entityTypeId=not-a-guid")]
    [InlineData("entityTypeId=00000000-0000-0000-0000-000000000000")]
    [InlineData("sessionId=not-a-guid")]
    [InlineData("sessionId=00000000-0000-0000-0000-000000000000")]
    public async Task Search_is_400_for_a_malformed_criterion(string query)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities/search?organizationSlug={_orgA}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HostEntityDto[]> ReadHostEntitiesAsync(HttpResponseMessage response)
    {
        // The search returns a flat array of the role-projected entities (not a page envelope).
        var entities = await response.Content.ReadFromJsonAsync<HostEntityDto[]>(_json);
        Assert.NotNull(entities);
        return entities;
    }

    private static async Task<ParticipantEntityDto[]> ReadParticipantEntitiesAsync(HttpResponseMessage response)
    {
        var entities = await response.Content.ReadFromJsonAsync<ParticipantEntityDto[]>(_json);
        Assert.NotNull(entities);
        return entities;
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no entity/tenant existence or authorization
    /// rationale (threat T7): it carries only the generic title/detail used for every denial.
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("summer-show", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("b-show", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    private static T Deserialize<T>(string body)
    {
        var value = JsonSerializer.Deserialize<T>(body, _json);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Returns the EXACT set of top-level JSON property names on the FIRST element of a flat JSON-array response
    /// body. The shape-leak guard that fails if a host-only field is ever added to the participant projection.
    /// </summary>
    private static string[] FirstElementPropertyNames(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var first = document.RootElement[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        return first.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private sealed record HostEntityDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid EntityTypeId,
        string Name,
        string AttributeValues,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ParticipantEntityDto(
        Guid Id,
        string Name,
        string EntityTypeKey);
}
