// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the optimistic-concurrency token surfaced as a weak
/// <c>ETag</c> on workspace reads and honored as an <c>If-Match</c> precondition on
/// workspace mutations (CORE-DX-002). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> — the canonical GET-then-PUT a consumer
/// runs against a workspace (the only <c>PUT</c> in the API).
///
/// <para>
/// The token is the PostgreSQL <c>xmin</c> row version (CORE-CONC-006), which exists
/// ONLY on the Npgsql provider; the default in-memory SQLite test provider maps none
/// (see <see cref="LiveCore.Api.Persistence.LiveCoreDbContext"/>). So the tests that
/// need a REAL, changing token — a current <c>If-Match</c> succeeding, and two clients
/// racing — assert only when the suite runs against PostgreSQL (the CI
/// <c>integration-postgres</c> job, <see cref="PostgresTestDatabase.IsConfigured"/>),
/// exactly like <see cref="OptimisticConcurrencyTokenTests"/>. The provider-independent
/// guarantees — an absent <c>If-Match</c> preserves the current behavior, a clearly
/// stale <c>If-Match</c> is rejected fail-closed with <c>412</c>, and a precondition is
/// only ever evaluated AFTER authorization (so it never leaks existence) — are asserted
/// on BOTH providers.
/// </para>
/// </summary>
public sealed class WorkspaceConcurrencyTokenEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _owner = "owner-a";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- Reads carry the weak ETag + version --------------------------------

    [Fact]
    public async Task Get_by_id_carries_the_concurrency_token_as_a_weak_etag_and_version()
    {
        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var client = factory.CreateClientFor(_owner, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadWorkspaceAsync(response);

        if (PostgresTestDatabase.IsConfigured)
        {
            // The read carries a weak ETag whose opaque value is the body's version
            // (the xmin token), so a consumer can echo it back as If-Match.
            var etag = response.Headers.ETag;
            Assert.NotNull(etag);
            Assert.True(etag!.IsWeak);
            Assert.False(string.IsNullOrEmpty(body.Version));
            Assert.Equal($"\"{body.Version}\"", etag.Tag);
        }
        else
        {
            // SQLite maps no row-version token, so there is none to surface.
            Assert.Null(body.Version);
            Assert.Null(response.Headers.ETag);
        }
    }

    // ---- An absent If-Match preserves the current (unconditional) behavior ----

    [Fact]
    public async Task Rename_without_if_match_renames_unconditionally()
    {
        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var client = factory.CreateClientFor(_owner, _issuer, _orgA);
        var response = await RenameAsync(client, workspaceId, "Renamed", ifMatch: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadWorkspaceAsync(response);
        Assert.Equal("Renamed", body.Name);
    }

    // ---- A stale If-Match is rejected with 412, fail-closed (both providers) --

    [Fact]
    public async Task Rename_with_a_stale_if_match_is_rejected_with_412_and_changes_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var client = factory.CreateClientFor(_owner, _issuer, _orgA);

        // W/"0" can never be the current token: xmin is never 0 on PostgreSQL, and on
        // SQLite (no token) an If-Match cannot be confirmed — both fail closed to 412.
        var response = await RenameAsync(client, workspaceId, "Clobbered", ifMatch: "W/\"0\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("precondition_failed", await ReadCodeAsync(response));

        // The rejected write changed nothing: the workspace keeps its original name.
        var current = await ReadWorkspaceAsync(
            await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}"));
        Assert.Equal("Original", current.Name);
    }

    [Fact]
    public async Task Archive_with_a_stale_if_match_is_rejected_with_412_and_changes_nothing()
    {
        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var client = factory.CreateClientFor(_owner, _issuer, _orgA);
        var response = await ArchiveAsync(client, workspaceId, ifMatch: "W/\"0\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("precondition_failed", await ReadCodeAsync(response));

        // The archive did not happen: the workspace is still Active.
        var current = await ReadWorkspaceAsync(
            await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}"));
        Assert.Equal("Active", current.Status);
    }

    // ---- A current If-Match succeeds; two writers cannot both win (Postgres) --

    [Fact]
    public async Task Rename_with_the_current_if_match_succeeds_and_advances_the_token()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            return; // Needs the real, changing xmin token (PostgreSQL only).
        }

        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var client = factory.CreateClientFor(_owner, _issuer, _orgA);

        var read = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
        var etag = ETagOf(read);
        Assert.False(string.IsNullOrEmpty(etag));

        var renamed = await RenameAsync(client, workspaceId, "Renamed", ifMatch: etag);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await ReadWorkspaceAsync(renamed);
        Assert.Equal("Renamed", body.Name);

        // The write advanced the version, so the same (now stale) tag no longer matches.
        var newEtag = ETagOf(renamed);
        Assert.False(string.IsNullOrEmpty(newEtag));
        Assert.NotEqual(etag, newEtag);

        var staleRetry = await RenameAsync(client, workspaceId, "Again", ifMatch: etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleRetry.StatusCode);
    }

    [Fact]
    public async Task Two_clients_reading_then_writing_cannot_both_win()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            return; // Needs the real, changing xmin token (PostgreSQL only).
        }

        await using var factory = new WorkspaceApiFactory();
        var workspaceId = await SeedOwnedWorkspaceAsync(factory);

        using var first = factory.CreateClientFor(_owner, _issuer, _orgA);
        using var second = factory.CreateClientFor(_owner, _issuer, _orgA);

        // Both read the SAME version (the classic interleaved read-modify-write).
        var sharedEtag = ETagOf(await first.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}"));
        Assert.False(string.IsNullOrEmpty(sharedEtag));

        // The first writer's conditional rename wins.
        var firstWrite = await RenameAsync(first, workspaceId, "First", ifMatch: sharedEtag);
        Assert.Equal(HttpStatusCode.OK, firstWrite.StatusCode);

        // The second writer holds the now-stale version, so its write is refused (412):
        // it cannot silently clobber the first writer's update.
        var secondWrite = await RenameAsync(second, workspaceId, "Second", ifMatch: sharedEtag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, secondWrite.StatusCode);

        var current = await ReadWorkspaceAsync(
            await first.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}"));
        Assert.Equal("First", current.Name);
    }

    // ---- NEGATIVE: the precondition is evaluated only AFTER authorization -----

    [Fact]
    public async Task A_stale_if_match_from_an_unauthorized_role_is_403_not_412()
    {
        // A tenant member who is not Owner/Admin cannot rename: the role check is a 403
        // BEFORE the If-Match precondition, so a precondition failure is never observable
        // to an unauthorized caller (it would otherwise confirm the resource changed).
        await using var factory = new WorkspaceApiFactory();
        const string outsider = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, outsider);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Original");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(outsider, _issuer, _orgA);
        var response = await RenameAsync(client, workspaceId, "Clobbered", ifMatch: "W/\"0\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_if_match_for_a_foreign_tenant_is_hidden_404_not_412()
    {
        // A caller resolving a tenant they have no standing in is hidden as 404 BEFORE
        // the workspace (or its version) is ever considered, so If-Match never leaks the
        // existence of a workspace in another tenant (threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string foreigner = "owner-b";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // The target workspace lives in org A.
            var ownerA = await db.AddUserAsync(_issuer, _owner);
            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, ownerA.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(orgA.Id, "summer-show", "Original");
            workspaceId = workspace.Id;

            // The caller is an Owner of a DIFFERENT org (org B) only.
            var ownerB = await db.AddUserAsync(_issuer, foreigner);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, ownerB.Id, MembershipRole.Owner);
        });

        // The caller presents org B as the tenant for org A's workspace.
        using var client = factory.CreateClientFor(foreigner, _issuer, _orgB);
        var response = await RenameAsync(client, workspaceId, "Clobbered", ifMatch: "W/\"0\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Seeds an organization with an Owner who is also a member of a single Active
    /// workspace named "Original", and returns that workspace's id. The Owner can both
    /// read it (workspace membership) and rename/archive it (organization Owner role).
    /// </summary>
    private static async Task<Guid> SeedOwnedWorkspaceAsync(WorkspaceApiFactory factory)
    {
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _owner);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Original");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Owner);
        });
        return workspaceId;
    }

    private static Task<HttpResponseMessage> RenameAsync(
        HttpClient client, Guid workspaceId, string name, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/workspaces/{workspaceId}")
        {
            Content = JsonContent.Create(new UpdateWorkspaceRequest(_orgA, name)),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ArchiveAsync(HttpClient client, Guid workspaceId, string? ifMatch)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static string? ETagOf(HttpResponseMessage response)
        => response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;

    private static async Task<WorkspaceResponse> ReadWorkspaceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<WorkspaceResponse>(_json);
        Assert.NotNull(body);
        return body!;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }
}
