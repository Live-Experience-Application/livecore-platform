using LiveCore.Api.IdentityAccess;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Persistence;

/// <summary>
/// EF Core context of the Core API (CORE-ID-002). PostgreSQL is the primary
/// database and EF Core the relational persistence layer per
/// docs/02_ARCHITECTURE.md and docs/10_DATABASE_SCHEMA.md.
///
/// The context is shared infrastructure of the modular monolith: each
/// module contributes only its own table mappings (currently the
/// IdentityAccess <c>users</c> table) and other modules never query foreign
/// tables directly (docs/02_ARCHITECTURE.md: module boundaries). Schema
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserProfileConfiguration());
    }
}
