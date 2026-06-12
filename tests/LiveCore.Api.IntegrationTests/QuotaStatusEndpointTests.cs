using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the quota-status endpoints (CORE-ENTL-003,
/// <c>GET /api/v1/me/quota-status</c> and <c>GET /api/v1/workspaces/{workspaceId}/quota-status</c>). They drive the
/// real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core
/// SQLite, foreign keys ON), so the documented request flow (authentication → tenant context resolver → endpoint →
/// authorization → server-side calculation) is exercised end-to-end.
///
/// Coverage, per the story's required tests ("Quota calculation and boundary tests") and the required NEGATIVE
/// authorization cases (threat T1 broken object-level authorization; threat T5 tenant isolation;
/// docs/06_AUTHORIZATION_MATRIX.md):
/// <list type="bullet">
///   <item>POSITIVE: a user reads its own quota status (computed from seeded entitlement + usage, at the boundary);
///   a host-capable workspace member reads the workspace's quota status.</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a service account has no personal quota (403 for
///   /me); the audience/audit roles {Participant, Observer, Auditor} are workspace members but NOT authorized for
///   the workspace quota status (403); a non-member of the workspace, a workspace in another tenant, an unknown
///   workspace and a malformed workspace id are ALL hidden as 404; a missing organizationSlug is 400.</item>
/// </list>
/// All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaStatusEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> HostCapableRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonHostRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // =====================================================================
    // GET /api/v1/me/quota-status
    // =====================================================================

    [Fact]
    public async Task My_quota_status_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/me/quota-status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task My_quota_status_for_a_service_account_is_403()
    {
        // /me is a user concept: a service account (issuer-asserted client_id marker) has no personal quota.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync("/api/v1/me/quota-status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task My_quota_status_returns_the_users_calculated_quota_at_the_boundary()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            // A user-subject quota: limit 1, used 1 -> entitled, at the boundary (not exceeded), zero remaining.
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync("workspace.active.max");
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User, QuotaUnit.Count);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 1);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.User, user.Id, quota, usedAmount: 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer);
        var response = await client.GetAsync("/api/v1/me/quota-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QuotaStatusDto>(_json);
        Assert.NotNull(body);
        var quotaItem = Assert.Single(body.Quotas);
        Assert.Equal("workspace.active.max", quotaItem.Key);
        Assert.Equal(nameof(QuotaUnit.Count), quotaItem.Unit);
        Assert.True(quotaItem.Entitled);
        Assert.Equal(1, quotaItem.Used);
        Assert.Equal(1, quotaItem.Limit);
        Assert.Equal(0, quotaItem.Remaining);
        Assert.False(quotaItem.Exceeded);
    }

    // =====================================================================
    // GET /api/v1/workspaces/{workspaceId}/quota-status
    // =====================================================================

    [Fact]
    public async Task Workspace_quota_status_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await GetWorkspaceAsync(client, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(HostCapableRoles))]
    public async Task Workspace_quota_status_is_200_for_a_host_capable_member(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceWithQuotaAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, seed.WorkspaceId, _orgA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<QuotaStatusDto>(_json);
        Assert.NotNull(body);
        var quotaItem = Assert.Single(body.Quotas);
        Assert.Equal("session.active.max", quotaItem.Key);
        Assert.True(quotaItem.Entitled);
        Assert.Equal(1, quotaItem.Used);
        Assert.Equal(1, quotaItem.Limit);
        Assert.False(quotaItem.Exceeded); // at the boundary
    }

    [Theory]
    [MemberData(nameof(NonHostRoles))]
    public async Task Workspace_quota_status_is_403_for_an_audience_or_audit_member(MembershipRole role)
    {
        // The caller is a member of the workspace but not a host-capable role: a workspace's quota status is
        // management metadata, so the audience/audit roles are denied fail-closed.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedWorkspaceWithQuotaAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, seed.WorkspaceId, _orgA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_quota_status_is_404_for_an_org_member_who_is_not_a_workspace_member()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The workspace exists but the caller is NOT a member of it (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, workspaceId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_quota_status_is_404_for_a_workspace_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        Guid workspaceInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insiderB = await db.AddUserAsync(_issuer, "insider-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspace.Id, insiderB.Id, MembershipRole.Host);
            workspaceInB = workspace.Id;
        });

        // Address the org-B workspace with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, workspaceInB, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_quota_status_is_404_for_an_unknown_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedWorkspaceWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_quota_status_is_404_for_a_malformed_workspace_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await SeedWorkspaceWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/workspaces/not-a-guid/quota-status?organizationSlug=" + _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_quota_status_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedWorkspaceWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await GetWorkspaceAsync(client, seed.WorkspaceId, organizationSlug: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> GetWorkspaceAsync(
        HttpClient client,
        Guid workspaceId,
        string? organizationSlug)
    {
        var url = $"/api/v1/workspaces/{workspaceId}/quota-status";
        if (organizationSlug is not null)
        {
            url += $"?organizationSlug={Uri.EscapeDataString(organizationSlug)}";
        }

        return await client.GetAsync(url);
    }

    /// <summary>
    /// Seeds an org + a caller with the given role in both the org and a workspace (all in org A), plus a
    /// WORKSPACE-subject quota (session.active.max) with limit 1 and usage 1, so an authorized read returns the
    /// computed status at its boundary.
    /// </summary>
    private static async Task<SeedResult> SeedWorkspaceWithQuotaAsync(
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

            var entitlement = await db.AddQuotaEntitlementDefinitionAsync("session.active.max");
            var quota = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace, QuotaUnit.Count);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 1);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.Workspace, workspace.Id, quota, usedAmount: 1);

            seed = new SeedResult(org.Id, workspace.Id);
        });
        return seed;
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId);

    private sealed record QuotaStatusDto(IReadOnlyList<QuotaItemDto> Quotas);

    private sealed record QuotaItemDto(
        string Key,
        string Unit,
        long Used,
        long? Limit,
        bool Unlimited,
        bool Entitled,
        long? Remaining,
        bool Exceeded);
}
