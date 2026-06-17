// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections.Concurrent;
using System.Net;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the host-initiated asset deletion route (CORE-LIFE-006, the "Resource Lifecycle
/// and Deletion" epic): <c>DELETE /api/v1/assets/{assetId}</c>. They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so
/// the documented request flow (authentication -&gt; tenant context resolver -&gt; asset resolution -&gt;
/// endpoint -&gt; inline authorization -&gt; deletion service) is exercised end-to-end exactly as in production.
/// The happy-path / role / tenant tests substitute a RECORDING fake <see cref="IAssetStorage"/> for the
/// production fail-closed default (the deployment supplies the real S3-compatible adapter) so the storage object
/// deletion is observable (the story's "Integration with a fake IAssetStorage recording the delete"); one test
/// runs against the unmodified app to prove the storage-unconfigured fail-closed path.
///
/// Coverage, per the story's required tests ("Integration with a fake IAssetStorage recording the delete;
/// asset-link cascade; negative role/tenant tests"):
/// <list type="bullet">
///   <item>HAPPY PATH / "its links are removed and the underlying storage object is deleted": a host deletes an
///   asset -&gt; 204; the asset and its links are gone and its storage object was deleted via the fake, while an
///   unrelated asset, its link and the linked content block survive; an <c>AssetDeleted</c> audit record is
///   appended.</item>
///   <item>401 unauthenticated.</item>
///   <item>The WORKSPACE-membership role sweep: allowed {Owner, Admin, Host, CoHost} -&gt; 204 vs denied
///   {Participant, Observer, Auditor} -&gt; 403, every denial asserting the asset still exists (no side effect),
///   that no storage delete was recorded and no rationale/body leak.</item>
///   <item>"foreign-tenant 404": a real asset in org B addressed with organizationSlug = A -&gt; 404 and the
///   asset survives.</item>
///   <item>A non-member of the asset's workspace (an org Owner who is not a workspace member) -&gt; 404-hidden,
///   never 403.</item>
///   <item>"non-existent asset is a safe 404" (and audits/records nothing); 400 missing organizationSlug; 404
///   for a malformed/empty asset id.</item>
///   <item>FAIL-CLOSED STORAGE: with no storage adapter configured (the production default) the delete is 503
///   and the asset, its links and the absence of an audit record all hold — no dangling row (threat T4).</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code, asserts the
/// asset is unchanged where a side effect was possible, and asserts the Problem Details body leaks no
/// existence/rationale or storage coordinate (threats T1/T4/T5/T7).
/// </summary>
public sealed class AssetDeletionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    /// <summary>The asset-deletion roles (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> DeleteRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT delete an asset (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonDeleteRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_unauthenticated_is_401()
    {
        await using var factory = new RecordingStorageApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync($"/api/v1/assets/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — asset deleted, links removed, storage object deleted,
    // unrelated rows survive, audit appended.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_204_and_removes_links_and_storage_object_and_audits_while_unrelated_survive()
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "host-a";
        Guid orgId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid assetToDeleteId = Guid.Empty;
        Guid link1Id = Guid.Empty;
        Guid link2Id = Guid.Empty;
        Guid otherAssetId = Guid.Empty;
        Guid otherLinkId = Guid.Empty;
        Guid contentBlockId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);

            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 0);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "body");
            contentBlockId = block.Id;

            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            assetToDeleteId = asset.Id;
            var link1 = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            link1Id = link1.Id;
            var link2 = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
            link2Id = link2.Id;

            // An unrelated asset + link that must survive.
            var other = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            otherAssetId = other.Id;
            var otherLink = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, other.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            otherLinkId = otherLink.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetToDeleteId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The storage object of exactly the deleted asset was deleted via IAssetStorage (recorded by the fake).
        Assert.Equal(new[] { assetToDeleteId }, factory.Storage.DeletedAssetIds.ToArray());

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The asset and both of its links are gone.
        Assert.False(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetToDeleteId));
        Assert.False(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == link1Id));
        Assert.False(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == link2Id));

        // The unrelated asset, its link and the linked content block survive.
        Assert.True(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == otherAssetId));
        Assert.True(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == otherLinkId));
        Assert.True(await context.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == contentBlockId));

        // The deletion is audited as a single AssetDeleted fact for the deleted asset.
        var entry = Assert.Single(
            await context.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditAction.AssetDeleted)
                .ToListAsync());
        Assert.Equal(orgId, entry.OrganizationId);
        Assert.Equal(workspaceId, entry.WorkspaceId);
        Assert.Equal(assetToDeleteId, entry.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.Assets.Asset), entry.ResourceType);
    }

    // =====================================================================
    // DELETE-ROLE SWEEP — allowed roles get 204.
    // =====================================================================

    [Theory]
    [MemberData(nameof(DeleteRoles))]
    public async Task Delete_asset_is_204_for_a_delete_workspace_role(MembershipRole role)
    {
        await using var factory = new RecordingStorageApiFactory();
        var subject = $"member-{role}";
        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            assetId = asset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { assetId }, factory.Storage.DeletedAssetIds.ToArray());
        await AssertAssetExistsAsync(factory, assetId, exists: false);
    }

    // =====================================================================
    // NON-DELETE-ROLE SWEEP — denied roles get 403, the asset survives and
    // no storage delete is recorded.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonDeleteRoles))]
    public async Task Delete_asset_is_403_for_a_non_delete_role_and_keeps_the_asset(MembershipRole role)
    {
        await using var factory = new RecordingStorageApiFactory();
        var subject = $"member-{role}";
        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            assetId = asset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        Assert.Empty(factory.Storage.DeletedAssetIds);
        await AssertAssetExistsAsync(factory, assetId, exists: true);
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real asset in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_404_for_an_asset_in_another_tenant_and_keeps_the_asset()
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "user-ab";
        Guid assetInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var assetInB = await db.AddAssetAsync(orgB.Id, workspaceInB.Id, user.Id, available: true);
            assetInBId = assetInB.Id;
        });

        // The caller holds both tenants in the token but addresses org B's asset with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        Assert.Empty(factory.Storage.DeletedAssetIds);
        await AssertAssetExistsAsync(factory, assetInBId, exists: true);
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the asset's
    // workspace must not learn the asset exists.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_404_for_an_org_member_who_is_not_a_member_of_the_assets_workspace()
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "outsider-a";
        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, insider.Id, available: true);
            assetId = asset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        Assert.Empty(factory.Storage.DeletedAssetIds);
        await AssertAssetExistsAsync(factory, assetId, exists: true);
    }

    // =====================================================================
    // SAFE 404 — deleting a non-existent asset changes nothing and audits
    // nothing.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_404_for_an_unknown_asset_and_audits_nothing()
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Storage.DeletedAssetIds);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetDeleted));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Delete_asset_is_404_for_a_malformed_or_empty_asset_id(string assetId)
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — missing organizationSlug query parameter.
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_400_without_the_organization_query_parameter_and_keeps_the_asset()
    {
        await using var factory = new RecordingStorageApiFactory();
        const string subject = "host-a";
        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            assetId = asset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertAssetExistsAsync(factory, assetId, exists: true);
    }

    // =====================================================================
    // FAIL-CLOSED STORAGE — 503 when storage is unconfigured; nothing is
    // deleted (no dangling row).
    // =====================================================================

    [Fact]
    public async Task Delete_asset_is_503_when_storage_is_unconfigured_and_changes_nothing()
    {
        // No fake adapter: the unmodified app keeps the production fail-closed UnconfiguredAssetStorage, so the
        // storage object delete (which precedes the row delete) throws and the whole transaction rolls back —
        // the asset, its link and the absence of an audit record all hold (no dangling row; threat T4).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid assetId = Guid.Empty;
        Guid linkId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            assetId = asset.Id;
            var link = await db.AddAssetLinkAsync(
                org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
            linkId = link.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{assetId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.True(await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId));
        Assert.True(await context.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == linkId));
        Assert.False(await context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.AssetDeleted));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task AssertAssetExistsAsync(WorkspaceApiFactory factory, Guid assetId, bool exists)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var found = await context.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId);
        Assert.Equal(exists, found);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no asset/tenant existence or authorization rationale:
    /// it carries only the generic title/detail used for every denial, with no slug, role, "why" wording or
    /// storage coordinate (threats T1/T4/T5/T7).
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucket", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a RECORDING fake <see cref="IAssetStorage"/> for the
    /// production fail-closed default, so the deletion flow can delete the storage object (and the test can
    /// assert which object was deleted) without a real, deployment-supplied S3-compatible adapter. Only the
    /// storage seam is swapped; every other production behavior (auth, tenant resolution, the repositories,
    /// persistence, the deletion service) runs unchanged.
    /// </summary>
    private sealed class RecordingStorageApiFactory : WorkspaceApiFactory
    {
        public RecordingAssetStorage Storage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAssetStorage>();
                services.AddSingleton<IAssetStorage>(Storage);
            });
        }
    }

    /// <summary>
    /// A conforming fake <see cref="IAssetStorage"/> that RECORDS each object deletion (the story's "fake
    /// IAssetStorage recording the delete"). It signs no URLs (the deletion flow never mints one) and can never
    /// serve bytes, so it cannot weaken the private-by-default posture. Not a production adapter. The recording
    /// list is thread-safe because the test host may serve requests on a thread-pool thread.
    /// </summary>
    private sealed class RecordingAssetStorage : IAssetStorage
    {
        public ConcurrentQueue<Guid> DeletedAssetIds { get; } = new();

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException("The deletion flow never mints an upload URL.");

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException("The deletion flow never mints a download URL.");

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            DeletedAssetIds.Enqueue(asset.Id);
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
