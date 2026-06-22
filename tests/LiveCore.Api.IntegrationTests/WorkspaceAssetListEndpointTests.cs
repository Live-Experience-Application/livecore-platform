// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the host workspace-asset enumeration read (CORE-ALC-003,
/// <c>GET /api/v1/workspaces/{workspaceId}/assets</c>, the "Asset Lifecycle and Attachment Completeness"
/// epic). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test
/// authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow (authentication
/// -&gt; tenant context resolver -&gt; endpoint -&gt; inline object-level authorization -&gt; bounded read)
/// is exercised end-to-end exactly as in production.
///
/// Coverage, per the story's required tests:
/// <list type="bullet">
///   <item>HAPPY PATH: a host-content role lists a workspace's assets (ALL statuses, Pending + Available)
///   scoped to the resolved tenant and workspace; the host AssetResponse projection carries the full
///   metadata (a Pending asset's size/checksum are null) and never a storage coordinate.</item>
///   <item>AUTHORIZATION + ISOLATION (fail-closed): 401 unauthenticated; a non-member and a known member who
///   lacks a host-content role (Participant/Observer/Auditor) are an indistinguishable hidden 404 (never
///   403); a foreign tenant's and a sibling workspace's assets never appear; a foreign-tenant workspace and
///   an unknown/malformed workspace are hidden 404.</item>
///   <item>PAGING (CORE-DX-003): an oversized limit is clamped to the platform max; the order is
///   deterministic and stable across reads; offset/hasMore page through the full set without overlap.</item>
///   <item>VALIDATION: a missing organizationSlug is 400; a malformed limit/offset is 400 (only after the
///   authorization gate, so a hidden caller never receives request-shape feedback).</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations, never an
/// ordering comparison; every denial asserts the SPECIFIC status code and that the Problem Details body leaks
/// no existence/rationale (threats T1/T5/T7). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class WorkspaceAssetListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The host-content roles that may enumerate a workspace's assets (csv/api_routes.csv "Host,CoHost,Owner,Admin").</summary>
    public static TheoryData<MembershipRole> HostRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The workspace-member roles that may NOT enumerate a workspace's assets: the audience roles
    /// (Participant, Observer) and the audit role (Auditor). Assets are host-only content, so a known member
    /// holding one of these is HIDDEN as 404 (never 403) — the host asset list's existence is host-only.
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
    public async Task List_assets_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // =====================================================================
    // Happy path — a host-content role lists all of the workspace's assets, every status.
    // =====================================================================

    [Theory]
    [MemberData(nameof(HostRoles))]
    public async Task Host_content_role_lists_the_workspaces_assets(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceGuid = Guid.Empty;
        Guid availableAssetId = Guid.Empty;
        Guid pendingAssetId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            // ALL statuses must be enumerated: one confirmed (Available) and one still-Pending upload intent.
            var available = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, "image/png", available: true);
            var pending = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, "application/pdf", available: false);
            workspaceGuid = workspace.Id;
            availableAssetId = available.Id;
            pendingAssetId = pending.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}");

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
    public async Task Host_asset_projection_carries_no_storage_coordinate()
    {
        // The host projection is full metadata but NEVER a storage coordinate (provider/bucket/object key) or
        // the creator: listing the metadata is not access to the bytes (threat T4 "Asset leak"/T7).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            workspaceGuid = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var json = await client.GetStringAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}");

        foreach (var marker in new[] { "bucket", "objectKey", "object_key", "storageProvider", "livecore-private-assets", "createdBy", "createdByUserProfileId" })
        {
            Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =====================================================================
    // Authorization + isolation — fail-closed, hidden-404.
    // =====================================================================

    [Theory]
    [MemberData(nameof(NonHostRoles))]
    public async Task List_assets_is_404_for_a_non_host_workspace_role(MembershipRole role)
    {
        // A known member of the workspace whose role is not a host-content role: assets are host-only, so the
        // list's very existence is hidden — an indistinguishable 404, never 403.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            workspaceGuid = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_assets_is_404_for_an_org_member_who_is_not_a_member_of_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            await db.AddAssetAsync(org.Id, workspace.Id, insider.Id, available: true);
            workspaceGuid = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_assets_never_includes_a_sibling_workspaces_assets()
    {
        // Two workspaces in the SAME tenant; the caller is a Host of workspace A only. Listing A's assets must
        // never include workspace B's asset (the workspace boundary; threat T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceA = Guid.Empty;
        Guid assetInA = Guid.Empty;
        Guid assetInB = Guid.Empty;
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
            var a = await db.AddAssetAsync(org.Id, wsA.Id, user.Id, available: true);
            var b = await db.AddAssetAsync(org.Id, wsB.Id, other.Id, available: true);
            workspaceA = wsA.Id;
            assetInA = a.Id;
            assetInB = b.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var page = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceA}/assets?organizationSlug={_orgA}", _json);

        Assert.NotNull(page);
        Assert.Equal(assetInA, Assert.Single(page.Items).AssetId);
        Assert.DoesNotContain(page.Items, a => a.AssetId == assetInB);
    }

    [Fact]
    public async Task List_assets_for_a_workspace_in_another_tenant_is_404()
    {
        // The caller is an Owner in tenant A; the workspace (and its assets) live in tenant B. Addressing the
        // org-B workspace with organizationSlug = A is hidden as 404 — a foreign-tenant workspace and its
        // assets never appear (threat T5).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        Guid workspaceInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var creatorInB = await db.AddUserAsync(_issuer, "creator-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Owner);
            var wsB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, wsB.Id, creatorInB.Id, MembershipRole.Host);
            await db.AddAssetAsync(orgB.Id, wsB.Id, creatorInB.Id, available: true);
            workspaceInB = wsB.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceInB}/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_assets_is_404_for_an_unknown_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedHostWithAssetsAsync(factory, subject, assetCount: 1);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{Guid.CreateVersion7()}/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_assets_is_404_for_a_malformed_workspace_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedHostWithAssetsAsync(factory, subject, assetCount: 1);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/not-a-guid/assets?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Validation.
    // =====================================================================

    [Fact]
    public async Task List_assets_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWithAssetsAsync(factory, subject, assetCount: 1);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_assets_with_a_malformed_limit_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWithAssetsAsync(factory, subject, assetCount: 1);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_non_host_never_learns_paging_shape_a_malformed_limit_is_still_404()
    {
        // The authorization gate runs BEFORE paging validation, so a non-host caller who supplies a malformed
        // limit still gets the hidden 404 (never a 400) — a hidden caller receives no request-shape feedback.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-observer";
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Observer);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Observer);
            await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            workspaceGuid = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Paging — clamp, stable order, offset/hasMore (CORE-DX-003).
    // =====================================================================

    [Fact]
    public async Task List_assets_clamps_an_oversized_limit_to_the_platform_max()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWithAssetsAsync(factory, subject, assetCount: 3);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var page = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=5000", _json);

        Assert.NotNull(page);
        // The server clamps the limit to its documented maximum (200), so a single read can never request an
        // unbounded page (threat T9; CORE-DX-003).
        Assert.Equal(200, page.Limit);
        Assert.Equal(3, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task List_assets_has_a_stable_deterministic_order()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWithAssetsAsync(factory, subject, assetCount: 5);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}", _json);
        var second = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}", _json);

        Assert.NotNull(first);
        Assert.NotNull(second);
        var firstOrder = first.Items.Select(a => a.AssetId).ToArray();
        var secondOrder = second.Items.Select(a => a.AssetId).ToArray();
        // Re-reading returns the SAME order (stable), and that order is the deterministic ascending id order
        // the repository enforces.
        Assert.Equal(firstOrder, secondOrder);
        Assert.Equal(firstOrder.OrderBy(id => id).ToArray(), firstOrder);
    }

    [Fact]
    public async Task List_assets_pages_through_the_full_set_with_offset_and_hasMore()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var workspaceGuid = await SeedHostWithAssetsAsync(factory, subject, assetCount: 5);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=2&offset=0", _json);
        Assert.NotNull(first);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);

        var second = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=2&offset=2", _json);
        Assert.NotNull(second);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.HasMore);

        var third = await client.GetFromJsonAsync<PageDto<AssetDto>>(
            $"/api/v1/workspaces/{workspaceGuid}/assets?organizationSlug={_orgA}&limit=2&offset=4", _json);
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
    /// Seeds (in org A) a Host caller in the org and a workspace, plus <paramref name="assetCount"/> Available
    /// assets in that workspace. Returns the workspace id.
    /// </summary>
    private static async Task<Guid> SeedHostWithAssetsAsync(
        WorkspaceApiFactory factory, string subject, int assetCount)
    {
        Guid workspaceGuid = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            for (var i = 0; i < assetCount; i++)
            {
                await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            }

            workspaceGuid = workspace.Id;
        });
        return workspaceGuid;
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
