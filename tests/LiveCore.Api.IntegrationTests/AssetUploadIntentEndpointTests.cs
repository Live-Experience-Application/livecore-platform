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
/// HTTP integration tests for the asset upload-intent flow (CORE-AST-003,
/// <c>POST /api/v1/assets/upload-intent</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented request flow (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; inline
/// authorization -&gt; command) is exercised end-to-end. The happy-path tests substitute a conforming fake
/// <see cref="IAssetStorage"/> for the production fail-closed default (the deployment supplies the real
/// S3-compatible signer), and one test runs against the unmodified app to prove the storage-unconfigured
/// fail-closed path.
///
/// Coverage, per the story's required tests ("Upload/download auth tests and storage adapter tests") and
/// the threat model's required control "asset download denial" applied to the upload side:
/// <list type="bullet">
///   <item>HAPPY PATH: a host (and each upload role) creates an intent -&gt; 201 with a PENDING asset id and
///   a short-lived signed upload URL, and exactly one pending asset is persisted with the resolved
///   tenant/workspace/creator and the declared content type.</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the non-upload roles
///   {Participant, Observer, Auditor} -&gt; 403 with NO asset; a non-member of the target workspace and a
///   workspace in another tenant hidden as 404 with NO asset.</item>
///   <item>VALIDATION: a missing organizationSlug and an invalid content type are 400; an empty workspace
///   id is hidden as 404.</item>
///   <item>FAIL-CLOSED STORAGE: with no storage adapter configured (the production default), an authorized
///   request is 503 and NO asset is persisted (private-by-default holds even unconfigured; threat T4).</item>
/// </list>
/// Every denial/validation case asserts NO asset was created, so a wrong-status pass that also mutated
/// state is caught. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AssetUploadIntentEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> UploadRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonUploadRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    [Fact]
    public async Task Upload_intent_unauthenticated_is_401()
    {
        await using var factory = new FakeStorageApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, Body(_orgA, Guid.CreateVersion7(), "image/png"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_creates_an_upload_intent_and_a_pending_asset_is_registered()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadIntentDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.AssetId);
        Assert.Equal(nameof(AssetStatus.Pending), body.Status);
        Assert.Equal("image/png", body.ContentType);
        Assert.True(Uri.TryCreate(body.UploadUrl, UriKind.Absolute, out _));
        // The signed URL is short-lived (within the type's one-hour ceiling) and in the future.
        Assert.True(body.ExpiresAt > TestData.SeedTime);

        // Exactly one PENDING asset persisted, scoped to the resolved tenant/workspace and the host creator,
        // with no confirmed upload yet (private by default — there is no public URL field anywhere).
        var assets = await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId);
        var asset = Assert.Single(assets);
        Assert.Equal(body.AssetId, asset.Id);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(seed.OrganizationId, asset.OrganizationId);
        Assert.Equal(seed.WorkspaceId, asset.WorkspaceId);
        Assert.Equal(await SingleUserProfileIdAsync(factory), asset.CreatedByUserProfileId);
        Assert.Null(asset.SizeBytes);
        Assert.Null(asset.Checksum);
        // The object key is tenant- and workspace-scoped (threats T5/T1).
        Assert.StartsWith($"assets/{seed.OrganizationId}/{seed.WorkspaceId}/", asset.ObjectKey, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UploadRoles))]
    public async Task Upload_intent_is_201_for_an_upload_workspace_role(MembershipRole role)
    {
        await using var factory = new FakeStorageApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Theory]
    [MemberData(nameof(NonUploadRoles))]
    public async Task Upload_intent_is_403_for_a_non_upload_workspace_role_and_creates_no_asset(MembershipRole role)
    {
        await using var factory = new FakeStorageApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Upload_intent_is_404_for_an_org_member_who_is_not_a_member_of_the_target_workspace()
    {
        await using var factory = new FakeStorageApiFactory();
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
            seed = new SeedResult(org.Id, workspace.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Upload_intent_is_404_for_a_workspace_in_another_tenant()
    {
        await using var factory = new FakeStorageApiFactory();
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

        // Address the org-B workspace with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seedB.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seedB.OrganizationId, seedB.WorkspaceId));
    }

    [Fact]
    public async Task Upload_intent_is_400_without_the_organization_slug()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(null, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Upload_intent_is_404_for_an_empty_workspace_id()
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, Guid.Empty, "image/png"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a content type")]
    public async Task Upload_intent_is_400_for_an_invalid_content_type(string contentType)
    {
        await using var factory = new FakeStorageApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    [Fact]
    public async Task Upload_intent_is_503_when_storage_is_unconfigured_and_creates_no_asset()
    {
        // No fake adapter: the unmodified app keeps the production fail-closed UnconfiguredAssetStorage.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Body(_orgA, seed.WorkspaceId, "image/png"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Fail-closed leaves no orphan pending asset: the URL is minted before the row is persisted.
        Assert.Empty(await AssetsAsync(factory, seed.OrganizationId, seed.WorkspaceId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, Guid workspaceId, string? contentType)
        => new { organizationSlug, workspaceId, contentType };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
        => await client.PostAsync("/api/v1/assets/upload-intent", JsonContent.Create(body, options: _json));

    /// <summary>Seeds an org + a caller with the given role in both the org and a workspace, all in org A.</summary>
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

    /// <summary>
    /// Returns the id of the single seeded user profile (the caller). The happy-path tests seed exactly one
    /// user, so this is the creator the asset must record.
    /// </summary>
    private static async Task<Guid> SingleUserProfileIdAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.UserProfiles.AsNoTracking().Select(profile => profile.Id).SingleAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId);

    private sealed record UploadIntentDto(
        Guid AssetId,
        string Status,
        string ContentType,
        string UploadUrl,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a conforming fake <see cref="IAssetStorage"/>
    /// for the production fail-closed default, so the upload-intent happy path can mint a signed URL without
    /// a real, deployment-supplied S3-compatible adapter. Only the storage signer seam is swapped; every
    /// other production behavior (auth, tenant resolution, the upload-intent service, persistence) runs
    /// unchanged.
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
    /// A minimal conforming <see cref="IAssetStorage"/> standing in for the deployment-supplied
    /// S3-compatible adapter: it mints a short-lived signed URL for the asset's own coordinates and can
    /// never produce a public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that
    /// unrepresentable. Not a production signer.
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
    }
}
