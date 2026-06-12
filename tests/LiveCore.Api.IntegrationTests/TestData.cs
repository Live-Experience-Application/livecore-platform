using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Seeding helpers for the workspace API integration tests. They write directly
/// through the public aggregate factories and the public DbContext, so the test
/// arrangement uses the same domain invariants the production code does.
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset SeedTime = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Creates and persists a user profile for a given OIDC identity.</summary>
    public static async Task<UserProfile> AddUserAsync(
        this LiveCoreDbContext context,
        string issuer,
        string subject)
    {
        var principal = new OidcPrincipal(PrincipalType.User, issuer, subject);
        var profile = UserProfile.CreateFromPrincipal(principal, SeedTime);
        context.UserProfiles.Add(profile);
        await context.SaveChangesAsync();
        return profile;
    }

    /// <summary>Creates and persists an organization.</summary>
    public static async Task<Organization> AddOrganizationAsync(
        this LiveCoreDbContext context,
        string slug)
    {
        var organization = Organization.Create(slug, slug, SeedTime);
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        return organization;
    }

    /// <summary>Creates and persists an organization membership.</summary>
    public static async Task<OrganizationMember> AddOrganizationMemberAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = OrganizationMember.Create(organizationId, userProfileId, role, SeedTime);
        context.OrganizationMembers.Add(member);
        await context.SaveChangesAsync();
        return member;
    }

    /// <summary>Creates and persists a workspace.</summary>
    public static async Task<Workspace> AddWorkspaceAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        string slug,
        string name)
    {
        var workspace = Workspace.Create(organizationId, slug, name, SeedTime);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        return workspace;
    }

    /// <summary>Creates and persists a workspace membership.</summary>
    public static async Task<WorkspaceMember> AddWorkspaceMemberAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = WorkspaceMember.Create(organizationId, workspaceId, userProfileId, role, SeedTime);
        context.WorkspaceMembers.Add(member);
        await context.SaveChangesAsync();
        return member;
    }

    /// <summary>
    /// Creates and persists a participant in the given workspace, optionally linked
    /// to a user (pass <paramref name="userProfileId"/> as <see langword="null"/> for
    /// an anonymous participant). The participant is created Active by the real
    /// aggregate factory; when <paramref name="removed"/> is <see langword="true"/>
    /// it is then soft-removed through <see cref="Participant.Remove"/>, so the seeded
    /// row has exactly the status the production transition would produce.
    /// </summary>
    public static async Task<Participant> AddParticipantAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid? userProfileId,
        string displayName = "Participant",
        bool removed = false)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId, displayName, SeedTime);

        if (removed)
        {
            participant.Remove(SeedTime);
        }

        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    /// <summary>
    /// Creates and persists a session in the given lifecycle status by driving the
    /// real aggregate state machine (Create, then Start/End as required), so the
    /// seeded session has exactly the timestamps and status the production
    /// transitions would produce. A <see cref="SessionStatus.Prepared"/> session is
    /// just created; a <see cref="SessionStatus.Live"/> session is created and
    /// started; an <see cref="SessionStatus.Ended"/> session is created, started and
    /// ended.
    /// </summary>
    public static async Task<Session> AddSessionAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        string title,
        SessionStatus status)
    {
        var session = Session.Create(organizationId, workspaceId, title, SeedTime);

        if (status is SessionStatus.Live or SessionStatus.Ended)
        {
            session.Start(SeedTime);
        }

        if (status is SessionStatus.Ended)
        {
            session.End(SeedTime);
        }

        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Creates and persists a scene in the given workspace at an explicit order, driving
    /// the real <see cref="Scene.Create"/> aggregate factory so the seeded row has exactly
    /// the invariants production would produce. Used to arrange a workspace's existing
    /// scenes (and their ordering) for the list and append-to-end tests.
    /// </summary>
    public static async Task<Scene> AddSceneAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        string title,
        int order)
    {
        var scene = Scene.Create(organizationId, workspaceId, title, order, SeedTime);
        context.Scenes.Add(scene);
        await context.SaveChangesAsync();
        return scene;
    }

    /// <summary>
    /// Creates and persists a content block in the given scene, driving the real
    /// <see cref="ContentBlock.Create"/> aggregate factory so the seeded row starts at the
    /// initial revision exactly as production would. Used to assert content-block creates
    /// did or did not happen.
    /// </summary>
    public static async Task<ContentBlock> AddContentBlockAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        ContentBlockType type,
        string body)
    {
        var contentBlock = ContentBlock.Create(organizationId, workspaceId, sceneId, type, body, SeedTime);
        context.ContentBlocks.Add(contentBlock);
        await context.SaveChangesAsync();
        return contentBlock;
    }

    /// <summary>
    /// Creates and persists an asset in the given workspace, driving the real <see cref="Asset.Create"/>
    /// aggregate factory so the seeded row has exactly the invariants production would produce. The asset
    /// starts <see cref="AssetStatus.Pending"/>; when <paramref name="available"/> is
    /// <see langword="true"/> it is then confirmed through <see cref="Asset.MarkAvailable"/> (recording a
    /// size and checksum), so the seeded row has exactly the status the production confirm transition would
    /// produce. Used to arrange a downloadable (or not-yet-downloadable) asset for the signed download
    /// tests. The storage coordinates are generic, tenant- and workspace-scoped naming (never client input).
    /// </summary>
    public static async Task<Asset> AddAssetAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        Guid createdByUserProfileId,
        string contentType = "image/png",
        bool available = true)
    {
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            "s3",
            "livecore-private-assets",
            $"assets/{organizationId}/{workspaceId}/{Guid.CreateVersion7()}",
            contentType,
            SeedTime);

        if (available)
        {
            asset.MarkAvailable(
                4096,
                "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
                SeedTime);
        }

        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        return asset;
    }

    /// <summary>
    /// Creates and persists a visibility rule for the given resource in the given workspace, driving
    /// the real <see cref="VisibilityRule.Create"/> aggregate factory. Used to arrange a resource's
    /// existing visibility (for example a Hidden rule the reveal command flips to Visible).
    /// </summary>
    public static async Task<VisibilityRule> AddVisibilityRuleAsync(
        this LiveCoreDbContext context,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(organizationId, workspaceId, resourceType, resourceId, visibility, SeedTime);
        context.VisibilityRules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }
}
