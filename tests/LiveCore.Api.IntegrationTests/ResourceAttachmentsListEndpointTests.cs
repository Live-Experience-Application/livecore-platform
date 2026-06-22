// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the host per-resource attachments read (CORE-ALC-004,
/// <c>GET /api/v1/assets/by-target/{targetType}/{targetId}</c>, the "Asset Lifecycle and Attachment
/// Completeness" epic). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; inline object-level authorization -&gt;
/// per-target link read) is exercised end-to-end exactly as in production.
///
/// This is the HOST counterpart of the audience-safe per-resource attachments on the participant visible feed
/// (CORE-ALC-002); it shares the host <see cref="AssetResponse"/> projection with the workspace enumeration
/// (CORE-ALC-003). Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: a host-content role lists the assets linked to a content block AND to an entity it owns,
///   in EVERY lifecycle status (Pending + Available); the projection carries no storage coordinate.</item>
///   <item>EMPTY: a target that exists but has no links returns an empty page (never an error).</item>
///   <item>AUTHORIZATION + ISOLATION (fail-closed): 401 unauthenticated; a known member who lacks a
///   host-content role is denied 403 (the target resource's existence is not host-only); a non-member of the
///   target's workspace is a hidden 404; a target in a sibling workspace and a target in another tenant are a
///   hidden 404; an unknown target is a hidden 404; only the named target's links are listed.</item>
///   <item>VALIDATION: a missing organizationSlug/workspaceId is 400; a malformed workspace id, an
///   unknown/malformed target type and a malformed target id are a hidden 404; a malformed limit is 400 (only
///   after the authorization gate, so a denied caller never receives request-shape feedback).</item>
///   <item>PAGING (CORE-DX-003): an oversized limit is clamped to the platform max; offset/hasMore page
///   through the full set without overlap.</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations, never an
/// ordering comparison; every denial asserts the SPECIFIC status code. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class ResourceAttachmentsListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The host-content roles that may list a resource's attachments (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> HostRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace-member roles that may NOT list a resource's attachments: the audience roles
    /// (Participant, Observer) and the audit role (Auditor). The target resource's existence is not host-only
    /// (a member may see the content block / entity), so a known member holding one of these is DENIED 403
    /// (the asset link/confirm/delete convention), never a hidden 404.
    /// </summary>
    public static TheoryData<MembershipRole> NonHostRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // 401 — unauthenticated.
    // =====================================================================

    [Fact]
    public async Task List_attachments_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{Guid.CreateVersion7()}?organizationSlug={_orgA}&workspaceId={Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // Happy path — a host-content role lists the assets linked to a target it owns.
    // =====================================================================

    [Theory]
    [MemberData(nameof(HostRoles))]
    public async Task Host_role_lists_a_content_blocks_attachments_in_every_status(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        Guid availableAssetId = Guid.Empty;
        Guid pendingAssetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            // ALL statuses must be listed: one confirmed (Available) and one still-Pending asset, BOTH linked.
            var available = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, "image/png", available: true);
            var pending = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, "application/pdf", available: false);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, available.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, pending.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
            availableAssetId = available.Id;
            pendingAssetId = pending.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<AssetDto>>(_json);
        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);

        var available1 = page.Items.Single(a => a.AssetId == availableAssetId);
        Assert.Equal(nameof(AssetStatus.Available), available1.Status);
        Assert.Equal("image/png", available1.ContentType);
        Assert.Equal(4096, available1.SizeBytes);
        Assert.False(string.IsNullOrEmpty(available1.Checksum));

        var pending1 = page.Items.Single(a => a.AssetId == pendingAssetId);
        Assert.Equal(nameof(AssetStatus.Pending), pending1.Status);
        Assert.Equal("application/pdf", pending1.ContentType);
        // A still-Pending asset carries no confirmed upload yet.
        Assert.Null(pending1.SizeBytes);
        Assert.Null(pending1.Checksum);
    }

    [Fact]
    public async Task Host_lists_an_entitys_attachments()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceGuid = Guid.Empty;
        Guid entityGuid = Guid.Empty;
        Guid linkedAssetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id);
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, entityType.Id);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entity.Id, user.Id);
            workspaceGuid = workspace.Id;
            entityGuid = entity.Id;
            linkedAssetId = asset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var page = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/Entity/{entityGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}", _json);

        Assert.NotNull(page);
        Assert.Equal(linkedAssetId, Assert.Single(page.Items).AssetId);
    }

    [Fact]
    public async Task Attachment_projection_carries_no_storage_coordinate()
    {
        // The host projection is full metadata but NEVER a storage coordinate (provider/bucket/object key) or
        // the creator: listing the metadata is not access to the bytes (threat T4 "Asset leak"/T7).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var json = await client.GetStringAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        foreach (var marker in new[] { "bucket", "objectKey", "object_key", "storageProvider", "livecore-private-assets", "createdBy", "createdByUserProfileId" })
        {
            Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_target_with_no_links_returns_an_empty_page()
    {
        // The target genuinely exists in the caller's workspace but has no attachments: an EMPTY page, never an
        // error and never a 404 (a 404 is reserved for a target outside the caller's tenant/workspace).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<AssetDto>>(_json);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task List_attachments_only_lists_links_for_the_named_target()
    {
        // Two targets in the SAME workspace, each with its own linked asset. Listing one target must never
        // include the OTHER target's attachment (the per-target scope; threat T1).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        Guid assetForBlock = Guid.Empty;
        Guid assetForEntity = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id);
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, entityType.Id);
            var blockAsset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var entityAsset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, blockAsset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, entityAsset.Id, AssetLinkTargetType.Entity, entity.Id, user.Id);
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
            assetForBlock = blockAsset.Id;
            assetForEntity = entityAsset.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var page = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}", _json);

        Assert.NotNull(page);
        Assert.Equal(assetForBlock, Assert.Single(page.Items).AssetId);
        Assert.DoesNotContain(page.Items, a => a.AssetId == assetForEntity);
    }

    // =====================================================================
    // Authorization + isolation — fail-closed.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonHostRoles))]
    public async Task List_attachments_is_403_for_a_non_host_workspace_role(MembershipRole role)
    {
        // A known member of the workspace whose role is not a host-content role: the target resource's
        // existence is not host-only, so the host attachments capability is denied 403 (never a hidden 404).
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_is_404_for_a_target_in_a_sibling_workspace()
    {
        // The caller is a Host of workspace A and names workspace A, but the target lives in sibling workspace
        // B. The target is not in the caller's authorized workspace, so it is a hidden 404 (the same-workspace
        // coupling; threat T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceA = Guid.Empty;
        Guid blockInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var other = await db.AddUserAsync(_issuer, "host-b");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var wsA = await db.AddWorkspaceAsync(org.Id, "show-a", "Show A");
            var wsB = await db.AddWorkspaceAsync(org.Id, "show-b", "Show B");
            await db.AddWorkspaceMemberAsync(org.Id, wsA.Id, user.Id, MembershipRole.Host);
            await db.AddWorkspaceMemberAsync(org.Id, wsB.Id, other.Id, MembershipRole.Host);
            var sceneB = await db.AddSceneAsync(org.Id, wsB.Id, "Scene", 1);
            var blockB = await db.AddContentBlockAsync(org.Id, wsB.Id, sceneB.Id, ContentBlockType.Text, "Generic");
            workspaceA = wsA.Id;
            blockInB = blockB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockInB}?organizationSlug={_orgA}&workspaceId={workspaceA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_for_a_target_in_another_tenant_is_404()
    {
        // The caller is an Owner in tenant A; the workspace and its target live in tenant B. Addressing the
        // org-B workspace/target with organizationSlug = A is hidden as 404 (threat T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        Guid workspaceInB = Guid.Empty;
        Guid blockInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var creatorInB = await db.AddUserAsync(_issuer, "creator-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Owner);
            var wsB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, wsB.Id, creatorInB.Id, MembershipRole.Host);
            var sceneB = await db.AddSceneAsync(orgB.Id, wsB.Id, "Scene", 1);
            var blockB = await db.AddContentBlockAsync(orgB.Id, wsB.Id, sceneB.Id, ContentBlockType.Text, "Generic");
            workspaceInB = wsB.Id;
            blockInB = blockB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockInB}?organizationSlug={_orgA}&workspaceId={workspaceInB}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_is_404_for_an_unknown_target()
    {
        // The workspace exists and the caller is a Host, but the target id addresses no resource in it: a
        // hidden 404 (distinct from the empty page an EXISTING target with no links returns).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{Guid.CreateVersion7()}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Validation.
    // =====================================================================

    [Fact]
    public async Task List_attachments_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{Guid.CreateVersion7()}?workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_without_the_workspace_id_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_with_a_malformed_workspace_id_is_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{Guid.CreateVersion7()}?organizationSlug={_orgA}&workspaceId=not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_with_an_unknown_target_type_is_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // "Scene" is a real Core resource but NOT an asset-link target kind, so it can never address a stored
        // target's links: a hidden 404, never echoing why.
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/Scene/{Guid.CreateVersion7()}?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_attachments_with_a_malformed_target_id_is_404()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWorkspaceAsync(factory, subject);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/not-a-guid?organizationSlug={_orgA}&workspaceId={workspaceGuid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_non_host_never_learns_paging_shape_a_malformed_limit_is_still_403()
    {
        // The authorization gate runs BEFORE paging validation, so a non-host member who supplies a malformed
        // limit still gets 403 (never a 400) — a denied caller receives no request-shape feedback.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-observer";
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Observer);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Observer);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}&limit=0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // =====================================================================
    // Paging — clamp, offset/hasMore (CORE-DX-003).
    // =====================================================================

    [Fact]
    public async Task List_attachments_clamps_an_oversized_limit_to_the_platform_max()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var (workspaceGuid, blockGuid) = await SeedTargetWithAttachmentsAsync(factory, subject, attachmentCount: 3);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var page = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}&limit=5000", _json);

        Assert.NotNull(page);
        // The server clamps the limit to its documented maximum (200), so a single read can never request an
        // unbounded page (threat T9; CORE-DX-003).
        Assert.Equal(200, page.Limit);
        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task List_attachments_pages_through_the_full_set_with_offset_and_hasMore()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var (workspaceGuid, blockGuid) = await SeedTargetWithAttachmentsAsync(factory, subject, attachmentCount: 5);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}&limit=2&offset=0", _json);
        Assert.NotNull(first);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);

        var second = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}&limit=2&offset=2", _json);
        Assert.NotNull(second);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.HasMore);

        var third = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/assets/by-target/ContentBlock/{blockGuid}?organizationSlug={_orgA}&workspaceId={workspaceGuid}&limit=2&offset=4", _json);
        Assert.NotNull(third);
        Assert.Single(third.Items);
        Assert.False(third.HasMore);

        // The three pages together cover the full set with no overlap and no duplicate.
        var allIds = first.Items.Concat(second.Items).Concat(third.Items).Select(a => a.AssetId).ToArray();
        Assert.Equal(5, allIds.Distinct().Count());
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    /// <summary>
    /// Seeds (in org A) a Host caller in the org and a workspace (no target). Returns the workspace id.
    /// </summary>
    private static async Task<Guid> SeedHostWorkspaceAsync(WorkspaceApiFactory factory, string subject)
    {
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceGuid = workspace.Id;
        });
        return workspaceGuid;
    }

    /// <summary>
    /// Seeds (in org A) a Host caller, a workspace, a content block target, and <paramref name="attachmentCount"/>
    /// Available assets each LINKED to that content block. Returns the workspace id and the content block id.
    /// </summary>
    private static async Task<(Guid WorkspaceId, Guid BlockId)> SeedTargetWithAttachmentsAsync(
        WorkspaceApiFactory factory, string subject, int attachmentCount)
    {
        Guid workspaceGuid = Guid.Empty;
        Guid blockGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            for (var i = 0; i < attachmentCount; i++)
            {
                var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
                await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            }

            workspaceGuid = workspace.Id;
            blockGuid = block.Id;
        });
        return (workspaceGuid, blockGuid);
    }

    private sealed record AssetDto(
        Guid AssetId,
        string Status,
        string ContentType,
        long? SizeBytes,
        string? Checksum,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
