using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Store;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for honoring an OPTIONAL client <c>Idempotency-Key</c> on the create and
/// purchase-verification routes (CORE-DX-004). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// retry-safe replay behavior is exercised end-to-end.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>CREATE REPLAY: a repeated create with the SAME key returns the ORIGINAL resource and creates
///   exactly one (session create, and workspace create — whose quota/slug path is the most involved).</item>
///   <item>DIFFERENT KEY: a create with a different key creates a NEW resource.</item>
///   <item>PURCHASE REPLAY: a retried Apple/Google verify under the same key does NOT re-run the external
///   verifier and does NOT re-append the audit facts — it returns the originally recorded transaction.</item>
///   <item>VALIDATION: a malformed key is a 400 that creates nothing; an absent key preserves the prior
///   behavior (covered by the other create tests, which omit the header).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class IdempotencyKeyEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _header = "Idempotency-Key";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // CREATE — session create replay / different key / malformed key.
    // =====================================================================

    [Fact]
    public async Task Session_create_replayed_with_the_same_key_returns_the_original_and_creates_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var url = $"/api/v1/workspaces/{workspaceId}/sessions";
        var body = new CreateSessionRequest(_orgA, "Opening Night");

        var first = await PostWithKeyAsync(client, url, body, "key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await ReadSessionAsync(first)).Id;

        // The retry under the SAME key returns the original session (200, same id), not a second 201.
        var replay = await PostWithKeyAsync(client, url, body, "key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayId = (await ReadSessionAsync(replay)).Id;

        Assert.Equal(firstId, replayId);
        Assert.Equal(1, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Session_create_with_a_different_key_creates_a_new_resource()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var url = $"/api/v1/workspaces/{workspaceId}/sessions";
        var body = new CreateSessionRequest(_orgA, "Opening Night");

        var first = await PostWithKeyAsync(client, url, body, "key-1");
        var second = await PostWithKeyAsync(client, url, body, "key-2");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual((await ReadSessionAsync(first)).Id, (await ReadSessionAsync(second)).Id);
        Assert.Equal(2, await CountSessionsAsync(factory, workspaceId));
    }

    [Fact]
    public async Task Session_create_with_a_malformed_key_is_400_and_creates_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // An over-long key (> 256) violates the key invariants -> 400.
        var malformedKey = new string('k', 300);
        var response = await PostWithKeyAsync(
            client,
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new CreateSessionRequest(_orgA, "Opening Night"),
            malformedKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountSessionsAsync(factory, workspaceId));
    }

    // =====================================================================
    // CREATE — workspace create replay (the quota + unique-slug path).
    // =====================================================================

    [Fact]
    public async Task Workspace_create_replayed_with_the_same_key_returns_the_original_and_creates_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var body = new CreateWorkspaceRequest(_orgA, "autumn-show", "Autumn Show");

        var first = await PostWithKeyAsync(client, "/api/v1/workspaces", body, "ws-key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await ReadWorkspaceAsync(first)).Id;

        var replay = await PostWithKeyAsync(client, "/api/v1/workspaces", body, "ws-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, (await ReadWorkspaceAsync(replay)).Id);

        Assert.Equal(1, await CountWorkspacesAsync(factory, _orgA));
    }

    // =====================================================================
    // CREATE — content-block create replay (scene-scoped replay load).
    // =====================================================================

    [Fact]
    public async Task Content_block_create_replayed_with_the_same_key_returns_the_original_and_creates_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene 1", 0);
            sceneId = scene.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var url = $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={_orgA}";
        var body = new { type = "Text", body = "Hello" };

        var first = await PostWithKeyAsync(client, url, body, "cb-key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await ReadIdAsync(first)).Id;

        var replay = await PostWithKeyAsync(client, url, body, "cb-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, (await ReadIdAsync(replay)).Id);

        Assert.Equal(1, await CountContentBlocksAsync(factory, sceneId));
    }

    // =====================================================================
    // CREATE — asset-link create replay (struct result + workspace-scoped load).
    // =====================================================================

    [Fact]
    public async Task Asset_link_create_replayed_with_the_same_key_returns_the_original_and_creates_one()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid assetId = Guid.Empty;
        Guid targetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene 1", 0);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id);
            var target = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Body");
            assetId = asset.Id;
            targetId = target.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var url = $"/api/v1/assets/{assetId}/links";
        var body = new CreateAssetLinkRequest(_orgA, "ContentBlock", targetId);

        var first = await PostWithKeyAsync(client, url, body, "al-key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await ReadAssetLinkAsync(first)).LinkId;
        Assert.NotEqual(Guid.Empty, firstId);

        var replay = await PostWithKeyAsync(client, url, body, "al-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, (await ReadAssetLinkAsync(replay)).LinkId);

        Assert.Equal(1, await CountAssetLinksAsync(factory, assetId));
    }

    // =====================================================================
    // PURCHASE — replay does not re-run the verifier or re-append audit facts.
    // =====================================================================

    [Fact]
    public async Task Apple_verify_replayed_with_the_same_key_does_not_rerun_the_verifier_or_reappend_audit()
    {
        await using var factory = new CountingVerifierApiFactory(PurchaseProvider.Apple);
        using var client = factory.CreateClientFor("buyer-a", _issuer);
        var body = new { transaction = "genuine-proof", productReference = "product.premium" };
        const string route = "/api/v1/purchases/apple/transactions";

        var first = await PostWithKeyAsync(client, route, body, "buy-key-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstTxn = (await ReadVerificationAsync(first)).ProviderTransactionId;

        var replay = await PostWithKeyAsync(client, route, body, "buy-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstTxn, (await ReadVerificationAsync(replay)).ProviderTransactionId);

        // The retry short-circuited BEFORE the verifier and BEFORE any audit fact: the verifier ran exactly
        // once, exactly one transaction is recorded, and the audit trail is unchanged (one Submitted + one
        // Succeeded fact from the first request, none from the replay).
        Assert.Equal(1, factory.Verifier.CallCount);
        Assert.Equal(1, await CountTransactionsAsync(factory));
        Assert.Equal(2, await CountAuditLogsAsync(factory));
    }

    [Fact]
    public async Task Google_verify_replayed_with_the_same_key_does_not_rerun_the_verifier_or_reappend_audit()
    {
        await using var factory = new CountingVerifierApiFactory(PurchaseProvider.Google);
        using var client = factory.CreateClientFor("buyer-a", _issuer);
        var body = new { purchaseToken = "genuine-token", productReference = "product.premium" };
        const string route = "/api/v1/purchases/google/tokens";

        var first = await PostWithKeyAsync(client, route, body, "buy-key-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstTxn = (await ReadVerificationAsync(first)).ProviderTransactionId;

        var replay = await PostWithKeyAsync(client, route, body, "buy-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstTxn, (await ReadVerificationAsync(replay)).ProviderTransactionId);

        Assert.Equal(1, factory.Verifier.CallCount);
        Assert.Equal(1, await CountTransactionsAsync(factory));
        Assert.Equal(2, await CountAuditLogsAsync(factory));
    }

    [Fact]
    public async Task Apple_verify_with_a_different_key_reruns_the_verifier()
    {
        // A DIFFERENT key is a distinct logical request: the verifier runs again. The (provider, transaction
        // id) idempotency of the recording still keeps a replayed-but-genuine proof to a single transaction,
        // so this proves the key — not the proof alone — gates the short-circuit.
        await using var factory = new CountingVerifierApiFactory(PurchaseProvider.Apple);
        using var client = factory.CreateClientFor("buyer-a", _issuer);
        var body = new { transaction = "genuine-proof", productReference = "product.premium" };
        const string route = "/api/v1/purchases/apple/transactions";

        await PostWithKeyAsync(client, route, body, "buy-key-1");
        await PostWithKeyAsync(client, route, body, "buy-key-2");

        Assert.Equal(2, factory.Verifier.CallCount);
        Assert.Equal(1, await CountTransactionsAsync(factory));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client,
        string url,
        object body,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(_header, idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<int> CountSessionsAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Sessions.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId);
    }

    private static async Task<int> CountWorkspacesAsync(WorkspaceApiFactory factory, string organizationSlug)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var org = await context.Organizations.AsNoTracking().SingleAsync(o => o.Slug == organizationSlug);
        return await context.Workspaces.AsNoTracking().CountAsync(w => w.OrganizationId == org.Id);
    }

    private static async Task<int> CountContentBlocksAsync(WorkspaceApiFactory factory, Guid sceneId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.ContentBlocks.AsNoTracking().CountAsync(b => b.SceneId == sceneId);
    }

    private static async Task<int> CountAssetLinksAsync(WorkspaceApiFactory factory, Guid assetId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AssetLinks.AsNoTracking().CountAsync(l => l.AssetId == assetId);
    }

    private static async Task<int> CountTransactionsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private static async Task<int> CountAuditLogsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking().CountAsync();
    }

    private static async Task<SessionDto> ReadSessionAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<WorkspaceDto> ReadWorkspaceAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<VerificationDto> ReadVerificationAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<VerificationDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<IdDto> ReadIdAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<IdDto>(_json);
        Assert.NotNull(dto);
        Assert.NotEqual(Guid.Empty, dto.Id);
        return dto;
    }

    private sealed record IdDto(Guid Id);

    private static async Task<AssetLinkDto> ReadAssetLinkAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<AssetLinkDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private sealed record AssetLinkDto(Guid LinkId);

    private sealed record SessionDto(Guid Id, Guid WorkspaceId, string Title, string Status);

    private sealed record WorkspaceDto(Guid Id, string Slug, string Name);

    private sealed record VerificationDto(
        string Provider,
        string ProviderTransactionId,
        string ProductReference,
        string Status);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that registers a <see cref="CountingVerifier"/> for the chosen
    /// provider, so a verify replay can assert the external verifier was NOT re-run. Every other production
    /// behavior (auth, the recording service, persistence) runs unchanged; the verifier reports the
    /// production environment so the purchase is honored on the default (non-production) test deployment.
    /// </summary>
    private sealed class CountingVerifierApiFactory : WorkspaceApiFactory
    {
        private readonly PurchaseProvider _provider;

        public CountingVerifierApiFactory(PurchaseProvider provider)
        {
            _provider = provider;
            Verifier = new CountingVerifier(provider);
        }

        public CountingVerifier Verifier { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IPurchaseVerificationProvider>(Verifier));
        }
    }

    /// <summary>
    /// A deterministic in-memory verifier that COUNTS its invocations, so a test can prove an idempotent
    /// replay short-circuited before reaching it. It verifies any proof to a stable provider transaction id
    /// derived from the proof (so a replayed-but-genuine proof maps to one purchase) in the production
    /// environment. It never contacts a real store API and is NOT a production verifier.
    /// </summary>
    private sealed class CountingVerifier : IPurchaseVerificationProvider
    {
        private int _callCount;

        public CountingVerifier(PurchaseProvider provider) => Provider = provider;

        public PurchaseProvider Provider { get; }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<PurchaseVerificationResult> VerifyAsync(
            PurchaseVerificationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref _callCount);

            var purchase = VerifiedPurchase.Create(
                Provider,
                $"txn-{Provider}-{request.Proof}",
                request.ProductReference ?? "product.unknown",
                PurchaseEnvironment.Production);

            return Task.FromResult(PurchaseVerificationResult.Verified(purchase));
        }
    }
}
