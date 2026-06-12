using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Templates;
using LiveCore.Api.Visibility;
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
/// <c>organization_members</c> tables, the Workspaces <c>workspaces</c>,
/// <c>workspace_members</c> and <c>workspace_invitations</c> tables, the
/// Participants <c>participants</c> table, the Sessions <c>sessions</c> table, the
/// Scenes <c>scenes</c> table, the Content <c>content_blocks</c> table, the
/// Entities <c>entity_types</c>, <c>entities</c> and <c>entity_relationships</c> tables, the
/// Templates <c>templates</c> table, the Visibility <c>visibility_rules</c> table, the
/// System <c>idempotency_keys</c> table, the Audit <c>audit_logs</c> table, the
/// Realtime <c>session_events</c> table, the Assets <c>assets</c> and
/// <c>asset_links</c> tables, the Exports <c>export_jobs</c>, <c>export_manifests</c> and
/// <c>export_manifest_entries</c> tables, the Recaps <c>recaps</c> table and the
/// Entitlements <c>entitlement_definitions</c>, <c>plan_definitions</c> and
/// <c>plan_entitlements</c> tables)
/// and other modules never query foreign tables directly (docs/02_ARCHITECTURE.md:
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

    /// <summary>Workspace memberships owned by the Workspaces module.</summary>
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    /// <summary>Workspace invitations owned by the Workspaces module.</summary>
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();

    /// <summary>Participants owned by the Participants module.</summary>
    public DbSet<Participant> Participants => Set<Participant>();

    /// <summary>Sessions owned by the Sessions module.</summary>
    public DbSet<Session> Sessions => Set<Session>();

    /// <summary>Scenes owned by the Scenes module.</summary>
    public DbSet<Scene> Scenes => Set<Scene>();

    /// <summary>Content blocks owned by the Content module.</summary>
    public DbSet<ContentBlock> ContentBlocks => Set<ContentBlock>();

    /// <summary>Entity types owned by the Entities module.</summary>
    public DbSet<EntityType> EntityTypes => Set<EntityType>();

    /// <summary>Entity instances owned by the Entities module.</summary>
    public DbSet<Entity> Entities => Set<Entity>();

    /// <summary>Entity relationships (graph edges) owned by the Entities module.</summary>
    public DbSet<EntityRelationship> EntityRelationships => Set<EntityRelationship>();

    /// <summary>Template registry entries owned by the Templates module.</summary>
    public DbSet<Template> Templates => Set<Template>();

    /// <summary>Visibility rules owned by the Visibility module.</summary>
    public DbSet<VisibilityRule> VisibilityRules => Set<VisibilityRule>();

    /// <summary>Idempotency keys owned by the System module.</summary>
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    /// <summary>Append-only audit log entries owned by the Audit module.</summary>
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <summary>Append-only session events owned by the Realtime module.</summary>
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    /// <summary>Asset metadata records owned by the Assets module.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Asset-to-content/entity links owned by the Assets module.</summary>
    public DbSet<AssetLink> AssetLinks => Set<AssetLink>();

    /// <summary>Async export jobs owned by the Exports module.</summary>
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    /// <summary>Produced workspace export manifests owned by the Exports module.</summary>
    public DbSet<ExportManifest> ExportManifests => Set<ExportManifest>();

    /// <summary>Export manifest inventory entries owned by the Exports module.</summary>
    public DbSet<ExportManifestEntry> ExportManifestEntries => Set<ExportManifestEntry>();

    /// <summary>Produced session recaps owned by the Recaps module.</summary>
    public DbSet<Recap> Recaps => Set<Recap>();

    /// <summary>Generic entitlement catalog definitions owned by the Entitlements module.</summary>
    public DbSet<EntitlementDefinition> EntitlementDefinitions => Set<EntitlementDefinition>();

    /// <summary>Generic plan catalog definitions owned by the Entitlements module.</summary>
    public DbSet<PlanDefinition> PlanDefinitions => Set<PlanDefinition>();

    /// <summary>Plan-to-entitlement grants owned by the Entitlements module.</summary>
    public DbSet<PlanEntitlement> PlanEntitlements => Set<PlanEntitlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserProfileConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationMemberConfiguration());
        modelBuilder.ApplyConfiguration(new WorkspaceConfiguration());
        modelBuilder.ApplyConfiguration(new WorkspaceMemberConfiguration());
        modelBuilder.ApplyConfiguration(new WorkspaceInvitationConfiguration());
        modelBuilder.ApplyConfiguration(new ParticipantConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new SceneConfiguration());
        modelBuilder.ApplyConfiguration(new ContentBlockConfiguration());
        modelBuilder.ApplyConfiguration(new EntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new EntityConfiguration());
        modelBuilder.ApplyConfiguration(new EntityRelationshipConfiguration());
        modelBuilder.ApplyConfiguration(new TemplateConfiguration());
        modelBuilder.ApplyConfiguration(new VisibilityRuleConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyKeyConfiguration());
        modelBuilder.ApplyConfiguration(new AuditLogConfiguration());
        modelBuilder.ApplyConfiguration(new SessionEventConfiguration());
        modelBuilder.ApplyConfiguration(new AssetConfiguration());
        modelBuilder.ApplyConfiguration(new AssetLinkConfiguration());
        modelBuilder.ApplyConfiguration(new ExportJobConfiguration());
        modelBuilder.ApplyConfiguration(new ExportManifestConfiguration());
        modelBuilder.ApplyConfiguration(new ExportManifestEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RecapConfiguration());
        modelBuilder.ApplyConfiguration(new EntitlementDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new PlanDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new PlanEntitlementConfiguration());
    }
}
