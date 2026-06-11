using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
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
}
