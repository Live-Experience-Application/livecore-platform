// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the asset confirm-upload route (CORE-ALC-001, the "Asset Lifecycle and
/// Attachment Completeness" epic): <c>POST /api/v1/assets/{assetId}/confirm-upload</c>. They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core
/// SQLite, foreign keys ON), so the documented request flow (authentication -&gt; tenant context resolver -&gt;
/// asset resolution -&gt; endpoint -&gt; inline authorization -&gt; confirm service) is exercised end-to-end
/// exactly as in production. The confirm flow touches NO object storage, so most tests run against the
/// unmodified app; the one test that also exercises the signed download after confirm substitutes a conforming
/// fake <see cref="IAssetStorage"/> so the download URL can be minted.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: a host confirms a pending asset -&gt; 200 with the now-Available asset carrying the
///   recorded size/checksum; the persisted asset is Available; a download-url that was 409 while Pending then
///   succeeds; an <c>AssetConfirmed</c> audit fact (Pending-&gt;Available) is appended.</item>
///   <item>CONFIRM-ROLE SWEEP: allowed {Owner, Admin, Host, CoHost} -&gt; 200 vs denied
///   {Participant, Observer, Auditor} -&gt; 403, every denial asserting the asset stays Pending, that no audit
///   fact was appended and no rationale leaks.</item>
///   <item>409: confirming a non-Pending (already Available) asset changes nothing and appends no second
///   audit fact.</item>
///   <item>404 (hidden, fail-closed): a foreign-tenant asset, a non-member of the asset's workspace, an unknown
///   asset and a malformed/empty asset id — each asserting the asset (where one exists) stays Pending and no
///   audit fact is appended.</item>
///   <item>VALIDATION: 401 unauthenticated; 400 missing organizationSlug; 400 invalid checksum; 400 negative
///   size — each surfaced only after authorization and leaving the asset Pending.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// allowed/denied sets, never an ordering comparison. Every denial asserts the SPECIFIC status code, asserts the
/// asset is unchanged where a side effect was possible, and asserts the Problem Details body leaks no
/// existence/rationale (threats T1/T5/T7). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AssetConfirmUploadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const long _sizeBytes = 4096;
    private const string _checksum = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The asset confirm roles (csv/api_routes.csv "Host,CoHost,Owner,Admin", same set as upload-intent).</summary>
    public static TheoryData<MembershipRole> ConfirmRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>The workspace-member roles that may NOT confirm an upload (the audience and audit roles).</summary>
    public static TheoryData<MembershipRole> NonConfirmRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, Guid.CreateVersion7(), Body(_orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // HAPPY PATH — pending asset becomes Available and downloadable, audited.
    // =====================================================================

    [Fact]
    public async Task Host_confirms_a_pending_asset_and_it_becomes_available_downloadable_and_audited()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // A download URL stays 409 while the asset is still Pending (CORE-AST-004).
        var beforeDownload = await client.GetAsync(
            $"/api/v1/assets/{seed.AssetId}/download-url?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.Conflict, beforeDownload.StatusCode);

        var response = await PostAsync(client, seed.AssetId, Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConfirmUploadDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(seed.AssetId, body.AssetId);
        Assert.Equal(nameof(AssetStatus.Available), body.Status);
        Assert.Equal("image/png", body.ContentType);
        Assert.Equal(_sizeBytes, body.SizeBytes);
        Assert.Equal(_checksum, body.Checksum);

        // The persisted asset is now Available with the recorded size and checksum.
        var asset = await GetAssetAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId);
        Assert.NotNull(asset);
        Assert.Equal(AssetStatus.Available, asset.Status);
        Assert.Equal(_sizeBytes, asset.SizeBytes);
        Assert.Equal(_checksum, asset.Checksum);

        // The confirmation is audited as a single AssetConfirmed fact (Pending -> Available) for the asset.
        var entry = Assert.Single(
            await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
        Assert.Equal(seed.OrganizationId, entry.OrganizationId);
        Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seed.AssetId, entry.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.Assets.Asset), entry.ResourceType);
        Assert.Equal(nameof(AssetStatus.Pending), entry.PreviousState);
        Assert.Equal(nameof(AssetStatus.Available), entry.NewState);

        // A download URL now succeeds (the asset is Available).
        var afterDownload = await client.GetAsync(
            $"/api/v1/assets/{seed.AssetId}/download-url?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, afterDownload.StatusCode);
    }

    // =====================================================================
    // CONFIRM-ROLE SWEEP — allowed roles get 200.
    // =====================================================================

    [Theory]
    [MemberData(nameof(ConfirmRoles))]
    public async Task Confirm_upload_is_200_for_a_confirm_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedPendingAssetAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var asset = await GetAssetAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId);
        Assert.Equal(AssetStatus.Available, asset!.Status);
    }

    // =====================================================================
    // NON-CONFIRM-ROLE SWEEP — denied roles get 403, the asset stays Pending,
    // nothing is audited.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonConfirmRoles))]
    public async Task Confirm_upload_is_403_for_a_non_confirm_role_and_keeps_the_asset_pending(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedPendingAssetAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertStillPendingAsync(factory, seed);
        Assert.Empty(await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
    }

    // =====================================================================
    // 409 — confirming a non-Pending (already confirmed) asset.
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_of_a_non_pending_asset_is_409_and_changes_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            // Already Available (the upload was confirmed earlier).
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, 1, "deadbeef"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The asset keeps its original confirmed size/checksum — the re-confirm overwrote nothing.
        var asset = await GetAssetAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId);
        Assert.Equal(AssetStatus.Available, asset!.Status);
        Assert.NotEqual(1, asset.SizeBytes);
        // No AssetConfirmed fact was written for the rejected re-confirm.
        Assert.Empty(await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
    }

    // =====================================================================
    // FOREIGN-TENANT 404 — a real pending asset in org B addressed with org A.
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_is_404_for_an_asset_in_another_tenant_and_keeps_it_pending()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-ab";
        SeedResult seedB = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, user.Id, MembershipRole.Host);
            var assetInB = await db.AddAssetAsync(orgB.Id, workspaceInB.Id, user.Id, available: false);
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, assetInB.Id);
        });

        // The caller holds both tenants in the token but addresses org B's asset with organizationSlug = A.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await PostAsync(client, seedB.AssetId, Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertStillPendingAsync(factory, seedB);
        Assert.Empty(await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
    }

    // =====================================================================
    // NON-MEMBER 404 — an org Owner who is not a member of the asset's
    // workspace must not learn the asset exists.
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_is_404_for_an_org_member_who_is_not_a_member_of_the_assets_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, insider.Id, available: false);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoRationaleLeakAsync(response);
        await AssertStillPendingAsync(factory, seed);
        Assert.Empty(await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
    }

    // =====================================================================
    // SAFE 404 — an unknown asset, a malformed/empty asset id.
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_is_404_for_an_unknown_asset_and_audits_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Guid.CreateVersion7(), Body(_orgA, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await AuditEntriesAsync(factory, AuditAction.AssetConfirmed));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Confirm_upload_is_404_for_a_malformed_or_empty_asset_id(string assetId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/assets/{assetId}/confirm-upload",
            JsonContent.Create(Body(_orgA, _sizeBytes, _checksum), options: _json));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // 400 — request-shape validation (surfaced only after authorization).
    // =====================================================================

    [Fact]
    public async Task Confirm_upload_is_400_without_the_organization_slug_and_keeps_the_asset_pending()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(null, _sizeBytes, _checksum));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillPendingAsync(factory, seed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a checksum")]
    public async Task Confirm_upload_is_400_for_an_invalid_checksum_and_keeps_the_asset_pending(string checksum)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, _sizeBytes, checksum));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillPendingAsync(factory, seed);
    }

    [Fact]
    public async Task Confirm_upload_is_400_for_a_negative_size_and_keeps_the_asset_pending()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedPendingAssetAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, -1, _checksum));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertStillPendingAsync(factory, seed);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, long sizeBytes = _sizeBytes, string? checksum = _checksum)
        => new { organizationSlug, sizeBytes, checksum };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid assetId, object body)
        => await client.PostAsync(
            $"/api/v1/assets/{assetId}/confirm-upload",
            JsonContent.Create(body, options: _json));

    /// <summary>Seeds an org + a caller with the given role in both the org and a workspace, plus a PENDING asset.</summary>
    private static async Task<SeedResult> SeedPendingAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: false);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id);
        });
        return seed;
    }

    private static async Task<Asset?> GetAssetAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        Guid assetId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Assets.AsNoTracking()
            .FirstOrDefaultAsync(asset =>
                asset.OrganizationId == organizationId
                && asset.WorkspaceId == workspaceId
                && asset.Id == assetId);
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> AuditEntriesAsync(
        WorkspaceApiFactory factory,
        AuditAction action)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == action)
            .ToListAsync();
    }

    private static async Task AssertStillPendingAsync(WorkspaceApiFactory factory, SeedResult seed)
    {
        var asset = await GetAssetAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId);
        Assert.NotNull(asset);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Null(asset.Checksum);
    }

    /// <summary>
    /// Asserts the Problem Details body of a denial leaks no asset/tenant existence or authorization rationale:
    /// it carries only the generic title/detail used for every denial, with no slug, role or "why" wording
    /// (threats T1/T5/T7).
    /// </summary>
    private static async Task AssertNoRationaleLeakAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid AssetId);

    private sealed record ConfirmUploadDto(
        Guid AssetId,
        string Status,
        string ContentType,
        long SizeBytes,
        string Checksum,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a conforming fake <see cref="IAssetStorage"/> for
    /// the production fail-closed default, so the post-confirm signed download can mint a URL without a real,
    /// deployment-supplied S3-compatible adapter. The confirm flow itself uses no storage; only the download
    /// verification step does. Only the storage seam is swapped; every other production behavior runs unchanged.
    /// </summary>
    private sealed class FakeStorageApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAssetStorage>();
                services.AddSingleton<IAssetStorage, FakeSignedUrlAssetStorage>();
            });
        }
    }

    /// <summary>
    /// A minimal conforming <see cref="IAssetStorage"/> standing in for the deployment-supplied S3-compatible
    /// adapter: it mints a short-lived signed download URL for the asset's own coordinates and can never produce
    /// a public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that unrepresentable. Not a
    /// production signer; the confirm flow never calls it.
    /// </summary>
    private sealed class FakeSignedUrlAssetStorage : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(15);

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=put&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Upload, TestData.SeedTime, _lifetime));
        }

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=get&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Download, TestData.SeedTime, _lifetime));
        }

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
