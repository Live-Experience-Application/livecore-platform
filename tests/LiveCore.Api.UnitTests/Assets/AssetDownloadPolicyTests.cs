using LiveCore.Api.Assets;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Tests for <see cref="AssetDownloadPolicy"/> (CORE-AST-005) — the "may this workspace role download this
/// asset?" decision the signed-download endpoint applies. The policy is a fail-closed decision service over
/// the real EF Core asset-link repository and the real <see cref="VisibilityPolicy"/> (over the
/// visibility-rule repository), driven against an in-memory SQLite database with foreign keys enforced, so
/// the linked-and-visible decision runs against genuinely persisted links and rules — exactly like the
/// <see cref="LiveCore.Api.UnitTests.Visibility.VisibilityPolicyTests"/>.
///
/// This is the NEGATIVE-AUTHORIZATION-heavy suite the story demands ("Upload/download auth tests"; the
/// epic acceptance "assets are private by default and accessed only through authorized signed URLs"; threat
/// T4 "Asset leak"; threat T2 visibility leak; threat T5 tenant isolation):
/// <list type="bullet">
///   <item>HOST-content roles (Owner/Admin/Host/CoHost) may always download — even with NO link.</item>
///   <item>AUDIENCE roles (Participant/Observer) may download ONLY when the asset is linked to a content
///   block / entity that is VISIBLE to the audience; a hidden-only link, or no link, is denied.</item>
///   <item>The audit role and any undefined role are DENIED even when a linked target is visible.</item>
///   <item>ISOLATION: a visible target in another WORKSPACE never grants audience download here (the
///   asset's own workspace scopes both the link lookup and the visibility decision; threat T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AssetDownloadPolicyTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";

    private const string _organizationSlugA = "northwind-labs";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    public AssetDownloadPolicyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private AssetDownloadPolicy CreatePolicy(LiveCoreDbContext context)
        => new(new AssetLinkRepository(context), new VisibilityPolicy(new VisibilityRuleRepository(context)));

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync(string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subjectId), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(UserProfileAddResult.Added, await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<Asset> SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdBy)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, "s3", "livecore-private-assets",
            $"assets/{organizationId}/{workspaceId}/{Guid.CreateVersion7()}", "image/png", _createdAt);
        asset.MarkAvailable(4096, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetAddResult.Added, await new AssetRepository(context).AddAsync(asset, CancellationToken.None));
        return asset;
    }

    private async Task SeedLinkAsync(
        Guid organizationId, Guid workspaceId, Guid assetId, AssetLinkTargetType targetType, Guid targetId, Guid createdBy)
    {
        var link = AssetLink.Create(organizationId, workspaceId, assetId, targetType, targetId, createdBy, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetLinkAddResult.Added, await new AssetLinkRepository(context).AddAsync(link, CancellationToken.None));
    }

    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var session = Session.Create(organizationId, workspaceId, "Live Session", _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        _sessionByWorkspace[workspaceId] = session.Id;
        return session.Id;
    }

    private async Task SeedRuleAsync(
        Guid organizationId, Guid workspaceId, VisibilityResourceType resourceType, Guid resourceId, VisibilityState visibility)
    {
        var sessionId = await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.Create(organizationId, workspaceId, sessionId, resourceType, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    // Creates a fresh, distinct session (NOT the per-workspace cached one), so the session-scoped tests can
    // arrange a "revealed" session and a "sibling" session of the same workspace.
    private async Task<Guid> SeedSessionAsync(Guid organizationId, Guid workspaceId, string title)
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<Guid> SeedParticipantAsync(Guid organizationId, Guid workspaceId)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, "Participant", _createdAt);
        await using var context = CreateContext();
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant.Id;
    }

    private async Task SeedRuleInSessionAsync(
        Guid organizationId, Guid workspaceId, Guid sessionId, VisibilityResourceType resourceType, Guid resourceId, VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(organizationId, workspaceId, sessionId, resourceType, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    private async Task SeedParticipantRuleInSessionAsync(
        Guid organizationId, Guid workspaceId, Guid sessionId, VisibilityResourceType resourceType, Guid resourceId, Guid targetParticipantId, VisibilityState visibility)
    {
        var rule = VisibilityRule.CreateForParticipant(
            organizationId, workspaceId, sessionId, resourceType, resourceId, targetParticipantId, visibility, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    private async Task<(Organization Org, Workspace Ws, UserProfile User, Asset Asset)> SeedBaseAsync(string workspaceSlug = _workspaceSlugA)
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, workspaceSlug);
        var user = await SeedUserAsync(Guid.CreateVersion7().ToString());
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        return (org, ws, user, asset);
    }

    // --- Host-content roles always download ------------------------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_content_role_can_download_even_with_no_link(MembershipRole role)
    {
        var (org, ws, _, asset) = await SeedBaseAsync();

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanDownloadAsync(asset, role, CancellationToken.None));
    }

    // --- Audience roles: only when linked to a visible target ------------------

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_can_download_when_linked_to_a_visible_content_block(MembershipRole role)
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var contentBlockId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, contentBlockId, user.Id);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.ContentBlock, contentBlockId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanDownloadAsync(asset, role, CancellationToken.None));
    }

    [Fact]
    public async Task Audience_role_can_download_when_linked_to_a_visible_entity()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var entityId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, entityId, user.Id);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.Entity, entityId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanDownloadAsync(asset, MembershipRole.Participant, CancellationToken.None));
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_cannot_download_when_linked_target_is_hidden(MembershipRole role)
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var contentBlockId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, contentBlockId, user.Id);
        // Linked, but the target is hidden from the audience -> the asset is not audience-accessible.
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.ContentBlock, contentBlockId, VisibilityState.Hidden);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanDownloadAsync(asset, role, CancellationToken.None));
    }

    [Fact]
    public async Task Audience_role_cannot_download_an_unlinked_asset()
    {
        var (_, _, _, asset) = await SeedBaseAsync();

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        // No link at all -> host-only by default -> denied to the audience (the pre-CORE-AST-005 posture).
        Assert.False(await policy.CanDownloadAsync(asset, MembershipRole.Participant, CancellationToken.None));
    }

    [Fact]
    public async Task Audience_role_can_download_when_any_of_several_links_is_visible()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var hiddenTarget = Guid.CreateVersion7();
        var visibleTarget = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, hiddenTarget, user.Id);
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, visibleTarget, user.Id);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.ContentBlock, hiddenTarget, VisibilityState.Hidden);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.Entity, visibleTarget, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanDownloadAsync(asset, MembershipRole.Observer, CancellationToken.None));
    }

    // --- Audit / undefined roles: denied even when a target is visible ---------

    [Fact]
    public async Task Auditor_cannot_download_even_a_visibly_linked_asset()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var entityId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, entityId, user.Id);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.Entity, entityId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        // Auditor is audit-only, not a live content grant: a visible link does not grant download.
        Assert.False(await policy.CanDownloadAsync(asset, MembershipRole.Auditor, CancellationToken.None));
    }

    [Fact]
    public async Task Undefined_role_cannot_download_even_a_visibly_linked_asset()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var entityId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, entityId, user.Id);
        await SeedRuleAsync(org.Id, ws.Id, VisibilityResourceType.Entity, entityId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanDownloadAsync(asset, (MembershipRole)999, CancellationToken.None));
    }

    // --- Isolation: a visible target elsewhere does not grant audience download -

    [Fact]
    public async Task A_visible_rule_in_another_workspace_does_not_grant_audience_download()
    {
        // The asset is in workspace A and linked to target T; T is visible in workspace B only. An audience
        // viewer of A must NOT be able to download — both the link lookup and the visibility decision are
        // scoped to the asset's OWN workspace (threat T5).
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var wsA = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var wsB = await SeedWorkspaceAsync(org.Id, _workspaceSlugB);
        var user = await SeedUserAsync(Guid.CreateVersion7().ToString());
        var asset = await SeedAssetAsync(org.Id, wsA.Id, user.Id);
        var targetId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, wsA.Id, asset.Id, AssetLinkTargetType.ContentBlock, targetId, user.Id);
        // The visible rule for the target is in workspace B, not the asset's workspace A.
        await SeedRuleAsync(org.Id, wsB.Id, VisibilityResourceType.ContentBlock, targetId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanDownloadAsync(asset, MembershipRole.Participant, CancellationToken.None));
    }

    [Fact]
    public async Task CanDownload_rejects_a_null_asset()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => policy.CanDownloadAsync(null!, MembershipRole.Owner, CancellationToken.None));
    }

    // =====================================================================
    // Session-scoped per-participant download (CORE-SVIS-003). A participant may download iff the asset is
    // linked to a resource VISIBLE to THEM IN THE GIVEN session — an audience-wide reveal of that session or
    // a reveal scoped to exactly them. A resource revealed only in a sibling session (or only to another
    // participant) never grants a download (threat T5/T3 cross-session leak; threat T2 visibility leak).
    // =====================================================================

    [Fact]
    public async Task Participant_can_download_when_linked_to_a_resource_revealed_in_the_session()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var contentBlockId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, contentBlockId, user.Id);
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);
        // Audience-wide visible reveal in session A.
        await SeedRuleInSessionAsync(org.Id, ws.Id, sessionA, VisibilityResourceType.ContentBlock, contentBlockId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanParticipantDownloadAsync(asset, sessionA, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task Participant_cannot_download_a_resource_revealed_only_in_a_sibling_session()
    {
        // The asset is linked to a content block revealed (audience-wide) in session A only. A participant
        // querying with session B (a sibling session of the SAME workspace) is denied — the reveal is
        // session-scoped, so it is not visible in B (the residual the story closes).
        var (org, ws, user, asset) = await SeedBaseAsync();
        var contentBlockId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, contentBlockId, user.Id);
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var sessionB = await SeedSessionAsync(org.Id, ws.Id, "Session B");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);
        await SeedRuleInSessionAsync(org.Id, ws.Id, sessionA, VisibilityResourceType.ContentBlock, contentBlockId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanParticipantDownloadAsync(asset, sessionB, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task Participant_can_download_when_a_resource_is_revealed_privately_to_them_in_the_session()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var entityId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, entityId, user.Id);
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);
        // Revealed ONLY to this participant in session A.
        await SeedParticipantRuleInSessionAsync(org.Id, ws.Id, sessionA, VisibilityResourceType.Entity, entityId, participantId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.True(await policy.CanParticipantDownloadAsync(asset, sessionA, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task Participant_cannot_download_when_a_resource_is_revealed_only_to_another_participant()
    {
        // The selected-participant guarantee for assets: the linked entity is revealed privately to a
        // DIFFERENT participant in session A. This participant, even in session A, is denied.
        var (org, ws, user, asset) = await SeedBaseAsync();
        var entityId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, entityId, user.Id);
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);
        var otherParticipantId = await SeedParticipantAsync(org.Id, ws.Id);
        await SeedParticipantRuleInSessionAsync(org.Id, ws.Id, sessionA, VisibilityResourceType.Entity, entityId, otherParticipantId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanParticipantDownloadAsync(asset, sessionA, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task Participant_cannot_download_when_the_linked_target_is_hidden_in_the_session()
    {
        var (org, ws, user, asset) = await SeedBaseAsync();
        var contentBlockId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, contentBlockId, user.Id);
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);
        await SeedRuleInSessionAsync(org.Id, ws.Id, sessionA, VisibilityResourceType.ContentBlock, contentBlockId, VisibilityState.Hidden);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanParticipantDownloadAsync(asset, sessionA, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task Participant_cannot_download_an_unlinked_asset()
    {
        var (org, ws, _, asset) = await SeedBaseAsync();
        var sessionA = await SeedSessionAsync(org.Id, ws.Id, "Session A");
        var participantId = await SeedParticipantAsync(org.Id, ws.Id);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        // No link at all -> host-only by default -> denied to a participant.
        Assert.False(await policy.CanParticipantDownloadAsync(asset, sessionA, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task A_visible_rule_in_another_workspace_does_not_grant_participant_download()
    {
        // The asset is in workspace A and linked to target T; T is revealed visible in a session of workspace
        // B only. A participant of A must NOT be able to download — both the link lookup and the session-scoped
        // visibility decision are scoped to the asset's OWN workspace (threat T5).
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var wsA = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var wsB = await SeedWorkspaceAsync(org.Id, _workspaceSlugB);
        var user = await SeedUserAsync(Guid.CreateVersion7().ToString());
        var asset = await SeedAssetAsync(org.Id, wsA.Id, user.Id);
        var targetId = Guid.CreateVersion7();
        await SeedLinkAsync(org.Id, wsA.Id, asset.Id, AssetLinkTargetType.ContentBlock, targetId, user.Id);
        var sessionInB = await SeedSessionAsync(org.Id, wsB.Id, "Session in B");
        var participantId = await SeedParticipantAsync(org.Id, wsA.Id);
        // The visible rule for the target is in a session of workspace B, not the asset's workspace A.
        await SeedRuleInSessionAsync(org.Id, wsB.Id, sessionInB, VisibilityResourceType.ContentBlock, targetId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        Assert.False(await policy.CanParticipantDownloadAsync(asset, sessionInB, participantId, CancellationToken.None));
    }

    [Fact]
    public async Task CanParticipantDownload_rejects_a_null_asset()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => policy.CanParticipantDownloadAsync(null!, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
    }
}
