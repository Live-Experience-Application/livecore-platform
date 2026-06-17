// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="WorkspaceExportInventoryReader"/> (CORE-JOB-002) —
/// the system read that counts a workspace's resources per generic <see cref="ExportResourceKind"/> so the
/// export processing job can produce the workspace export manifest's inventory.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// the same harness as the other Exports repository tests, so the real model mapping and the per-kind
/// <c>COUNT</c> translation are exercised on every run without a database server. The behaviors under test are
/// the inventory's acceptance-relevant properties:
/// <list type="bullet">
///   <item>COMPLETE + CORRECT: every defined kind is present (absent kinds count 0) and each count matches the
///   seeded rows.</item>
///   <item>TENANT-SCOPED (threat T5): the count is scoped to exactly the given (organization, workspace) pair —
///   a second workspace's, and a second TENANT's, resources are never counted; addressing a workspace under the
///   WRONG tenant counts nothing.</item>
///   <item>COUNTS ONLY: the inventory is row counts, never content (asserted by construction — the reader reads
///   no content column).</item>
///   <item>Empty ids are rejected.</item>
/// </list>
/// These negative tenant-isolation cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md). All fixtures
/// are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class WorkspaceExportInventoryReaderTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public WorkspaceExportInventoryReaderTests()
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

    [Fact]
    public async Task Counts_each_kind_in_the_workspace_and_includes_every_kind()
    {
        var (organization, workspace) = await SeedTenantAsync("northwind-labs", "summer-show");
        var user = await SeedUserAsync("subject-a");

        // 2 sessions, 1 scene (with 1 content block under it), 1 entity (of 1 type), 1 participant, 1 asset.
        await SeedSessionsAsync(organization.Id, workspace.Id, count: 2);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id);
        await SeedContentBlockAsync(organization.Id, workspace.Id, scene.Id);
        await SeedEntityAsync(organization.Id, workspace.Id);
        await SeedParticipantAsync(organization.Id, workspace.Id);
        await SeedAssetAsync(organization.Id, workspace.Id, user.Id);

        await using var context = CreateContext();
        var reader = new WorkspaceExportInventoryReader(context);
        var inventory = await reader.CountWorkspaceResourcesAsync(organization.Id, workspace.Id, CancellationToken.None);

        // Every defined kind is present in the inventory.
        Assert.Equal(6, inventory.Count);
        Assert.Equal(2, inventory[ExportResourceKind.Session]);
        Assert.Equal(1, inventory[ExportResourceKind.Scene]);
        Assert.Equal(1, inventory[ExportResourceKind.ContentBlock]);
        Assert.Equal(1, inventory[ExportResourceKind.Entity]);
        Assert.Equal(1, inventory[ExportResourceKind.Participant]);
        Assert.Equal(1, inventory[ExportResourceKind.Asset]);
    }

    [Fact]
    public async Task An_empty_workspace_inventories_every_kind_as_zero()
    {
        var (organization, workspace) = await SeedTenantAsync("northwind-labs", "summer-show");

        await using var context = CreateContext();
        var reader = new WorkspaceExportInventoryReader(context);
        var inventory = await reader.CountWorkspaceResourcesAsync(organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(6, inventory.Count);
        Assert.All(inventory.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task Counts_only_the_target_workspaces_resources_not_another_workspace_in_the_same_tenant()
    {
        // Workspace isolation (threat T5): two workspaces in the SAME tenant, each with its own sessions. The
        // inventory of one never borrows the other's rows.
        var (organization, workspaceA) = await SeedTenantAsync("northwind-labs", "summer-show");
        var workspaceB = await SeedWorkspaceAsync(organization.Id, "winter-show");
        await SeedSessionsAsync(organization.Id, workspaceA.Id, count: 3);
        await SeedSessionsAsync(organization.Id, workspaceB.Id, count: 7);

        await using var context = CreateContext();
        var reader = new WorkspaceExportInventoryReader(context);

        var inventoryA = await reader.CountWorkspaceResourcesAsync(organization.Id, workspaceA.Id, CancellationToken.None);
        var inventoryB = await reader.CountWorkspaceResourcesAsync(organization.Id, workspaceB.Id, CancellationToken.None);

        Assert.Equal(3, inventoryA[ExportResourceKind.Session]);
        Assert.Equal(7, inventoryB[ExportResourceKind.Session]);
    }

    [Fact]
    public async Task Never_counts_another_tenants_resources()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md: organization
        // boundary checked before workspace boundary). Two tenants each have a workspace with sessions. Counting
        // tenant A's workspace returns only A's sessions; addressing tenant A's WORKSPACE under tenant B's id —
        // and vice versa — counts NOTHING, so an export can never count across the tenant boundary.
        var (organizationA, workspaceA) = await SeedTenantAsync("northwind-labs", "summer-show");
        var (organizationB, workspaceB) = await SeedTenantAsync("acme-co", "winter-show");
        await SeedSessionsAsync(organizationA.Id, workspaceA.Id, count: 4);
        await SeedSessionsAsync(organizationB.Id, workspaceB.Id, count: 9);

        await using var context = CreateContext();
        var reader = new WorkspaceExportInventoryReader(context);

        var inventoryA = await reader.CountWorkspaceResourcesAsync(organizationA.Id, workspaceA.Id, CancellationToken.None);
        var inventoryB = await reader.CountWorkspaceResourcesAsync(organizationB.Id, workspaceB.Id, CancellationToken.None);
        // Workspace A addressed under tenant B (and workspace B under tenant A) — a cross-tenant mismatch.
        var crossTenant = await reader.CountWorkspaceResourcesAsync(organizationB.Id, workspaceA.Id, CancellationToken.None);

        Assert.Equal(4, inventoryA[ExportResourceKind.Session]);
        Assert.Equal(9, inventoryB[ExportResourceKind.Session]);
        Assert.All(crossTenant.Values, count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task Rejects_empty_ids()
    {
        await using var context = CreateContext();
        var reader = new WorkspaceExportInventoryReader(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.CountWorkspaceResourcesAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.CountWorkspaceResourcesAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // Seeding helpers --------------------------------------------------------------------------------------------

    private async Task<(Organization Organization, Workspace Workspace)> SeedTenantAsync(string orgSlug, string workspaceSlug)
    {
        var organization = await SeedOrganizationAsync(orgSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization, workspace);
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            WorkspaceAddResult.Added,
            await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync(string subject)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subject), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            UserProfileAddResult.Added,
            await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task SeedSessionsAsync(Guid organizationId, Guid workspaceId, int count)
    {
        await using var context = CreateContext();
        for (var index = 0; index < count; index++)
        {
            context.Sessions.Add(Session.Create(organizationId, workspaceId, $"session-{index}", _createdAt));
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<Scene> SeedSceneAsync(Guid organizationId, Guid workspaceId)
    {
        var scene = Scene.Create(organizationId, workspaceId, "scene", order: 1, _createdAt);
        await using var context = CreateContext();
        context.Scenes.Add(scene);
        await context.SaveChangesAsync(CancellationToken.None);
        return scene;
    }

    private async Task SeedContentBlockAsync(Guid organizationId, Guid workspaceId, Guid sceneId)
    {
        var block = ContentBlock.Create(organizationId, workspaceId, sceneId, ContentBlockType.Text, "Generic content", _createdAt);
        await using var context = CreateContext();
        context.ContentBlocks.Add(block);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedEntityAsync(Guid organizationId, Guid workspaceId)
    {
        var type = EntityType.Create(organizationId, workspaceId, "type-alpha", "Type Alpha", "{}", _createdAt);
        var entity = Entity.Create(organizationId, workspaceId, type.Id, "entity-a", "{}", _createdAt);
        await using var context = CreateContext();
        context.EntityTypes.Add(type);
        context.Entities.Add(entity);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedParticipantAsync(Guid organizationId, Guid workspaceId)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, "Generic Participant", _createdAt);
        await using var context = CreateContext();
        context.Participants.Add(participant);
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdByUserProfileId)
    {
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            "s3",
            "private-bucket",
            "exports/object-key",
            "application/octet-stream",
            _createdAt);
        await using var context = CreateContext();
        context.Assets.Add(asset);
        await context.SaveChangesAsync(CancellationToken.None);
    }
}
