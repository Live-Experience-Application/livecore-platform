// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the AUDIENCE-SAFE attachments list each participant-visible feed item carries
/// (CORE-ALC-002, <c>GET /api/v1/participants/{participantId}/visible-feed</c>), and the defence-in-depth
/// download path through <c>GET /api/v1/assets/{assetId}/download-url</c>. They drive the real application
/// over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite,
/// foreign keys ON), so the documented request flow (authentication -&gt; tenant context resolver -&gt;
/// endpoint -&gt; visibility projection -&gt; attachments projection) runs end-to-end exactly as in
/// production. The download happy path substitutes a conforming fake <see cref="IAssetStorage"/> for the
/// production fail-closed default (the deployment supplies the real S3-compatible signer).
///
/// Coverage, per the story's required tests (composing with CORE-APROJ-002 the enriched feed item):
/// <list type="bullet">
///   <item>ENUMERATE + DOWNLOAD: a participant who can see a resource enumerates its attachments (assetId +
///   audience-safe name + contentType) from the feed and downloads one via <c>getDownloadUrl</c>.</item>
///   <item>HIDDEN RESOURCE NEVER ENUMERATED (threat T2): an asset linked to a resource the participant
///   cannot see (a Hidden rule) is never listed — the resource is not even in the feed; the download check
///   STILL denies that hidden asset (defence in depth).</item>
///   <item>EMPTY, NEVER ERRORS: a visible resource with no attachments carries an empty list; a still-Pending
///   (not-yet-downloadable) linked asset is not listed.</item>
///   <item>AUDIENCE-SAFE SHAPE: each entry is exactly { assetId, name, contentType } with a null
///   forward-compatible name, and no host-only field (the storage object key / bucket / checksum) ever
///   appears anywhere in the feed (threats T2/T4/T7).</item>
/// </list>
/// All fixtures are generic, product-neutral (AGENTS.md).
/// </summary>
public sealed class ParticipantFeedAttachmentsEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // ENUMERATE + DOWNLOAD — the headline path.
    // =====================================================================

    [Fact]
    public async Task Participant_enumerates_a_visible_resources_attachments_and_downloads_one()
    {
        // A content block is revealed audience-wide in the participant's session, and an Available asset is
        // linked to it. The participant's own feed carries the content-block item with an attachments list of
        // exactly that asset (assetId + null name + contentType), and getDownloadUrl then mints a signed URL.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedVisibleLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // 1) Enumerate the attachments from the feed.
        var feedResponse = await GetFeedAsync(client, seed.ParticipantId, seed.SessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        var items = await ReadFeedItemsByIdAsync(feedResponse);
        var contentItem = Assert.Contains(seed.ContentBlockId, items);
        var attachments = contentItem.GetProperty("attachments");
        Assert.Equal(JsonValueKind.Array, attachments.ValueKind);
        var attachment = Assert.Single(attachments.EnumerateArray());

        // The entry is exactly { assetId, name, contentType } — the assetId is the download handle, the name
        // is the forward-compatible audience-safe slot (null today), and the content type is the asset's MIME.
        AssertAttachmentPropertySet(attachment);
        Assert.Equal(seed.AssetId, attachment.GetProperty("assetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("name").ValueKind);
        Assert.Equal("image/png", attachment.GetProperty("contentType").GetString());

        // 2) Download the enumerated asset through the server-side getDownloadUrl check (defence in depth).
        var downloadResponse = await GetDownloadUrlAsync(client, seed.AssetId, seed.SessionId);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var download = await downloadResponse.Content.ReadFromJsonAsync<DownloadDto>(_json);
        Assert.NotNull(download);
        Assert.Equal(seed.AssetId, download.AssetId);
        Assert.True(Uri.TryCreate(download.DownloadUrl, UriKind.Absolute, out _));
    }

    [Fact]
    public async Task Attachments_carry_no_host_only_storage_field()
    {
        // The host-only storage coordinates (object key, bucket) and the checksum must never appear anywhere
        // in the participant feed — an attachment is audience-safe metadata only (threats T2/T4/T7).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedVisibleLinkedAssetAsync(factory, subject, VisibilityState.Visible);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, seed.ParticipantId, seed.SessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        var payload = await feedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(seed.ObjectKey, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.Bucket, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.Checksum, payload, StringComparison.Ordinal);
    }

    // =====================================================================
    // HIDDEN RESOURCE NEVER ENUMERATED + download denied (threat T2; defence in depth).
    // =====================================================================

    [Fact]
    public async Task An_asset_linked_to_a_hidden_resource_is_never_listed()
    {
        // The content block carries a Hidden rule, so the participant cannot see it: the resource is not even
        // in the feed, so its linked asset is never enumerated anywhere.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedVisibleLinkedAssetAsync(factory, subject, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, seed.ParticipantId, seed.SessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        // The hidden content block is absent from the feed entirely...
        var items = await ReadFeedItemsByIdAsync(feedResponse);
        Assert.DoesNotContain(seed.ContentBlockId, items.Keys);

        // ...and the asset id never appears anywhere in the payload (no attachments leak).
        var payload = await feedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(seed.AssetId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_download_check_still_denies_a_hidden_asset()
    {
        // Defence in depth: even though the asset is never enumerated, a participant who guesses its id and
        // calls getDownloadUrl is STILL denied, because the linked content block is Hidden in their session.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedVisibleLinkedAssetAsync(factory, subject, VisibilityState.Hidden);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var downloadResponse = await GetDownloadUrlAsync(client, seed.AssetId, seed.SessionId);

        Assert.Equal(HttpStatusCode.Forbidden, downloadResponse.StatusCode);
    }

    // =====================================================================
    // EMPTY (never errors): no attachments, and a still-pending asset is not listed.
    // =====================================================================

    [Fact]
    public async Task A_visible_resource_with_no_attachments_carries_an_empty_list()
    {
        // A content block revealed audience-wide with NO linked asset: the feed item is present and its
        // attachments list is an empty array, never null and never an error.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid contentBlockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            contentBlockId = block.Id;
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, block.Id, VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, participantId, sessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        var items = await ReadFeedItemsByIdAsync(feedResponse);
        var item = Assert.Contains(contentBlockId, items);
        var attachments = item.GetProperty("attachments");
        Assert.Equal(JsonValueKind.Array, attachments.ValueKind);
        Assert.Empty(attachments.EnumerateArray());
    }

    [Fact]
    public async Task A_still_pending_linked_asset_is_not_listed()
    {
        // An asset whose upload is not yet confirmed (Pending) is not downloadable (getDownloadUrl is 409), so
        // it is not advertised as an attachment even though it is linked to a visible resource.
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        var seed = await SeedVisibleLinkedAssetAsync(factory, subject, VisibilityState.Visible, available: false);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, seed.ParticipantId, seed.SessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        var items = await ReadFeedItemsByIdAsync(feedResponse);
        var item = Assert.Contains(seed.ContentBlockId, items);
        Assert.Empty(item.GetProperty("attachments").EnumerateArray());
    }

    [Fact]
    public async Task A_selected_participant_reveal_carries_its_entity_attachments()
    {
        // The attachments inherit a SELECTED-participant reveal too: an entity revealed privately to the
        // participant carries its linked asset, proving the attachments follow the per-participant visibility
        // decision (not just an audience-wide one).
        await using var factory = new FakeStorageApiFactory();
        const string subject = "participant-a";
        Guid participantId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid entityId = Guid.Empty;
        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            participantId = participant.Id;
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id);
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, entityType.Id, "Private Clue");
            entityId = entity.Id;
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, contentType: "application/pdf", available: true);
            assetId = asset.Id;
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entity.Id, user.Id);
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, entity.Id, participant.Id,
                VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, participantId, sessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        var items = await ReadFeedItemsByIdAsync(feedResponse);
        var item = Assert.Contains(entityId, items);
        Assert.Equal("SelectedParticipant", item.GetProperty("revealScope").GetString());
        var attachment = Assert.Single(item.GetProperty("attachments").EnumerateArray());
        Assert.Equal(assetId, attachment.GetProperty("assetId").GetGuid());
        Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Another_participants_private_reveal_attachments_are_not_enumerated()
    {
        // The selected-participant guarantee for attachments: an entity (with a linked asset) is revealed
        // privately to a DIFFERENT participant. This participant's own feed lists neither the entity nor its
        // asset (threat T2/T5).
        await using var factory = new FakeStorageApiFactory();
        const string subjectP = "participant-p-owner";
        Guid participantPId = Guid.Empty;
        Guid sessionId = Guid.Empty;
        Guid privateAssetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var ownerP = await db.AddUserAsync(_issuer, subjectP);
            var ownerQ = await db.AddUserAsync(_issuer, "participant-q-owner");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, ownerP.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var participantP = await db.AddParticipantAsync(org.Id, workspace.Id, ownerP.Id, displayName: "P");
            participantPId = participantP.Id;
            var participantQ = await db.AddParticipantAsync(org.Id, workspace.Id, ownerQ.Id, displayName: "Q");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            sessionId = session.Id;
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id);
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, entityType.Id, "Q Only Clue");
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, ownerQ.Id, available: true);
            privateAssetId = asset.Id;
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entity.Id, ownerQ.Id);
            await db.AddParticipantVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, entity.Id, participantQ.Id,
                VisibilityState.Visible);
        });

        using var client = factory.CreateClientFor(subjectP, _issuer, _orgA);
        var feedResponse = await GetFeedAsync(client, participantPId, sessionId);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);

        // P sees nothing — and the other participant's private asset never appears in P's feed.
        var payload = await feedResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateAssetId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> GetFeedAsync(HttpClient client, Guid participantId, Guid sessionId)
        => await client.GetAsync(
            $"/api/v1/participants/{participantId}/visible-feed?organizationSlug={_orgA}&sessionId={sessionId}");

    private static async Task<HttpResponseMessage> GetDownloadUrlAsync(HttpClient client, Guid assetId, Guid sessionId)
        => await client.GetAsync(
            $"/api/v1/assets/{assetId}/download-url?organizationSlug={_orgA}&sessionId={sessionId}");

    private static void AssertAttachmentPropertySet(JsonElement attachment)
    {
        var properties = attachment.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "assetId",
                "name",
                "contentType",
            },
            properties);
    }

    private static async Task<IReadOnlyDictionary<Guid, JsonElement>> ReadFeedItemsByIdAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var items = new Dictionary<Guid, JsonElement>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            items[item.GetProperty("resourceId").GetGuid()] = item.Clone();
        }

        return items;
    }

    /// <summary>
    /// Seeds (in org A) a caller with the Participant role in the org and a workspace, an active participant
    /// record they own, a content block whose audience visibility is <paramref name="visibility"/> in the
    /// caller's session, and an asset (Available unless <paramref name="available"/> is false) LINKED to that
    /// content block. Returns the ids needed to assert the attachments and exercise the download.
    /// </summary>
    private static async Task<AttachmentSeed> SeedVisibleLinkedAssetAsync(
        WorkspaceApiFactory factory,
        string subject,
        VisibilityState visibility,
        bool available = true)
    {
        AttachmentSeed seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is also a workspace member (the download route's 404-hide gate checks workspace
            // membership); the feed route is ownership-based and needs no workspace role.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Participant);
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, user.Id);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Live Session", SessionStatus.Live);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: available);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, block.Id, visibility);

            seed = new AttachmentSeed(
                participant.Id, session.Id, block.Id, asset.Id, asset.ObjectKey, asset.Bucket, asset.Checksum ?? string.Empty);
        });
        return seed;
    }

    private readonly record struct AttachmentSeed(
        Guid ParticipantId,
        Guid SessionId,
        Guid ContentBlockId,
        Guid AssetId,
        string ObjectKey,
        string Bucket,
        string Checksum);

    private sealed record DownloadDto(
        Guid AssetId,
        string Status,
        string ContentType,
        string DownloadUrl,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a conforming fake <see cref="IAssetStorage"/> for
    /// the production fail-closed default, so the download happy path can mint a signed URL without a real,
    /// deployment-supplied S3-compatible adapter. Only the storage signer seam is swapped; every other
    /// production behavior (auth, tenant resolution, the repositories, persistence) runs unchanged.
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
    /// adapter: it mints a short-lived signed URL for the asset's own coordinates and can never produce a
    /// public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that unrepresentable.
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
