// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the asset upload-intent content-type allowlist and absolute per-object size
/// ceiling (CORE-AST-007, the "Audit Integrity and Security Hardening" epic). They drive the real application
/// over real HTTP through a <see cref="WorkspaceApiFactory"/> configured with a RESTRICTIVE upload policy
/// (allowlist = <c>image/png</c> only; ceiling = 1,000,000 bytes), exercising the documented request flow
/// (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; inline authorization -&gt; command) for
/// the new hardening checks.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>A disallowed content type is rejected (<c>422</c>) and NO asset is created. The factory keeps the
///   production fail-closed <see cref="UnconfiguredAssetStorage"/>, so a <c>422</c> (not a <c>503</c>) PROVES
///   the rejection happens BEFORE the storage adapter is ever consulted — no URL is minted.</item>
///   <item>An over-ceiling object is rejected (<c>413</c>) BEFORE a URL is minted and NO asset is created,
///   again proven by the <c>422</c>/<c>413</c>-not-<c>503</c> ordering against unconfigured storage.</item>
///   <item>An allowed, in-limit upload still succeeds (<c>201</c>) with a pending asset and a signed URL (this
///   case substitutes a conforming fake <see cref="IAssetStorage"/>).</item>
///   <item>The rejection leaks no storage detail: the <c>422</c>/<c>413</c> Problem Details bodies carry no
///   bucket, provider, object key, signed URL or signature (threats T4/T7).</item>
///   <item>NEGATIVE AUTHORIZATION: a non-upload role and a foreign-tenant caller are still denied (<c>403</c> /
///   hidden <c>404</c>) for a disallowed/over-ceiling body — authorization precedes the new checks, so an
///   unauthorized caller never receives request-shape feedback and no asset is created.</item>
/// </list>
/// Every denial asserts NO asset was created. All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class AssetUploadConstraintsEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // The restrictive policy the tests enforce against.
    private const string _allowedContentType = "image/png";
    private const string _disallowedContentType = "application/zip";
    private const long _ceilingBytes = 1_000_000;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Disallowed_content_type_is_422_before_storage_and_creates_no_asset()
    {
        // Unconfigured storage: a 422 (not a 503) proves the allowlist check rejects BEFORE the storage adapter
        // is consulted, so no signed URL is ever minted for a disallowed type.
        await using var factory = new ConstrainedUnconfiguredStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, _disallowedContentType, 100));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Over_ceiling_object_is_413_before_any_url_is_minted_and_creates_no_asset()
    {
        // Unconfigured storage: a 413 (not a 503) proves the ceiling check rejects BEFORE the storage adapter is
        // consulted, so no signed URL is ever minted for an over-ceiling object (the story's "rejected before a
        // URL is minted").
        await using var factory = new ConstrainedUnconfiguredStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(
            client, Body(_orgA, seed.WorkspaceId, _allowedContentType, _ceilingBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Allowed_in_limit_upload_is_201_and_registers_a_pending_asset()
    {
        await using var factory = new ConstrainedFakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, _allowedContentType, 500_000));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadIntentDto>(_json);
        Assert.NotNull(body);
        Assert.Equal(nameof(AssetStatus.Pending), body.Status);
        Assert.True(Uri.TryCreate(body.UploadUrl, UriKind.Absolute, out _));

        var asset = Assert.Single(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Equal(_allowedContentType, asset.ContentType);
        Assert.Equal(500_000, asset.SizeBytes);
    }

    [Fact]
    public async Task An_object_exactly_at_the_ceiling_is_accepted()
    {
        // The ceiling is inclusive: a declared size equal to the ceiling is admitted.
        await using var factory = new ConstrainedFakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, _allowedContentType, _ceilingBytes));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task A_rejection_leaks_no_storage_detail()
    {
        // Both rejection bodies (the disallowed type and the over-ceiling object) must carry no storage
        // coordinate — no bucket, provider, object key, signed URL or signature (threats T4/T7).
        await using var factory = new ConstrainedUnconfiguredStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var disallowed = await PostAsync(client, Body(_orgA, seed.WorkspaceId, _disallowedContentType, 100));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, disallowed.StatusCode);
        AssertNoStorageDetail(await disallowed.Content.ReadAsStringAsync());

        var oversize = await PostAsync(client, Body(_orgA, seed.WorkspaceId, _allowedContentType, _ceilingBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);
        AssertNoStorageDetail(await oversize.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_non_upload_role_is_403_even_for_a_disallowed_type_and_creates_no_asset()
    {
        // Authorization runs BEFORE the allowlist/ceiling checks, so an unauthorized caller is denied 403 and
        // never receives request-shape feedback (it never learns the type would have been rejected) — and no
        // asset is created.
        await using var factory = new ConstrainedFakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Participant);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(
            client, Body(_orgA, seed.WorkspaceId, _disallowedContentType, _ceilingBytes + 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task A_workspace_in_another_tenant_is_404_even_for_an_over_ceiling_object_and_creates_no_asset()
    {
        // Tenant isolation precedes the new checks: addressing another tenant's workspace is hidden as 404, never
        // distinguishable from a ceiling/allowlist rejection, and no asset is created (threats T1/T5).
        await using var factory = new ConstrainedFakeStorageApiFactory();
        const string subject = "user-a";
        SeedResult seedB = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            seedB = new SeedResult(orgB.Id, workspaceInB.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(
            client, Body(_orgA, seedB.WorkspaceId, _allowedContentType, _ceilingBytes + 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seedB.OrganizationId, seedB.WorkspaceId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    // Storage markers that must NEVER appear in a rejection body. The default private bucket name, the storage
    // host the fake signer uses, and the structural coordinate field names — none belong in a fail-closed
    // rejection that never reached the storage adapter.
    private static readonly string[] _storageMarkers =
    [
        "bucket",
        "livecore-assets",
        "object_key",
        "objectKey",
        "storage.example.com",
        "signature",
        "provider",
    ];

    private static void AssertNoStorageDetail(string body)
    {
        foreach (var marker in _storageMarkers)
        {
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object Body(string? organizationSlug, Guid workspaceId, string? contentType, long sizeBytes)
        => new { organizationSlug, workspaceId, contentType, sizeBytes };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
        => await client.PostAsync("/api/v1/assets/upload-intent", JsonContent.Create(body, options: _json));

    private static async Task<SeedResult> SeedWorkspaceAsync(
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
            seed = new SeedResult(org.Id, workspace.Id);
        });
        return seed;
    }

    private static async Task<IReadOnlyList<Asset>> AssetsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Assets.AsNoTracking()
            .Where(asset => asset.OrganizationId == organizationId && asset.WorkspaceId == workspaceId)
            .OrderBy(asset => asset.Id)
            .ToListAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId);

    private sealed record UploadIntentDto(
        Guid AssetId,
        string Status,
        string ContentType,
        string UploadUrl,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that configures the restrictive CORE-AST-007 upload policy
    /// (<c>Assets:Upload:*</c>) while leaving the production fail-closed <see cref="UnconfiguredAssetStorage"/>
    /// in place. Used by the rejection tests so a <c>422</c>/<c>413</c> (rather than a <c>503</c>) proves the
    /// allowlist/ceiling check rejects BEFORE the storage adapter is consulted.
    /// </summary>
    private class ConstrainedUnconfiguredStorageApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting($"{AssetUploadConstraints.ConfigurationSection}:AllowedContentTypes:0", _allowedContentType);
            builder.UseSetting(
                $"{AssetUploadConstraints.ConfigurationSection}:MaxObjectSizeBytes",
                _ceilingBytes.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The constrained factory above, additionally substituting a conforming fake <see cref="IAssetStorage"/> for
    /// the production fail-closed default, so an allowed, in-limit upload can mint a signed URL.
    /// </summary>
    private sealed class ConstrainedFakeStorageApiFactory : ConstrainedUnconfiguredStorageApiFactory
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
    /// adapter: it mints a short-lived signed URL for the asset's own coordinates and can never produce a public
    /// or non-expiring URL because <see cref="SignedAssetUrl"/> makes that unrepresentable. Not a production signer.
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
