using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Persistence;

/// <summary>
/// EF Core context of the Core API (CORE-ID-002). PostgreSQL is the primary
/// database and EF Core the relational persistence layer per
/// docs/02_ARCHITECTURE.md and docs/10_DATABASE_SCHEMA.md.
///
/// The context is shared infrastructure of the modular monolith: each
/// module contributes only its own table mappings (the IdentityAccess
/// <c>users</c> table, the Organizations <c>organizations</c> and
/// <c>organization_members</c> tables, and the Workspaces <c>workspaces</c>
/// table) and
/// other modules never query foreign tables directly (docs/02_ARCHITECTURE.md:
/// module boundaries). Schema
/// changes ship as checked-in migrations under
/// <c>apps/api/Persistence/Migrations</c>; migrations are applied as a
/// deployment step, never implicitly at host startup.
/// </summary>
public sealed class LiveCoreDbContext : DbContext
{
    public LiveCoreDbContext(DbContextOptions<LiveCoreDbContext> options)
        : base(options)
    {
    }

    /// <summary>User profile references owned by the IdentityAccess module.</summary>
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    /// <summary>Organization tenant roots owned by the Organizations module.</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Organization memberships owned by the Organizations module.</summary>
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    /// <summary>Workspaces owned by the Workspaces module.</summary>
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserProfileConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationMemberConfiguration());
        modelBuilder.ApplyConfiguration(new WorkspaceConfiguration());
    }
}
