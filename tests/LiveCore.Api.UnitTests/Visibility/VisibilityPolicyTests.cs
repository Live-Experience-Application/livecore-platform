using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="VisibilityPolicy"/> (CORE-VIS-002) — the <c>CanViewResource</c> decision. The
/// policy is a fail-closed decision service over the real EF Core visibility-rule repository, driven
/// against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the tenant/workspace-scoped rule lookups run against genuinely persisted rules — exactly like
/// the CORE-SES-003 join service and CORE-ENT-005 search service tests.
///
/// The story's required tests are "Negative authorization tests, idempotency tests, projection
/// tests"; this is the NEGATIVE-AUTHORIZATION-heavy suite that the policy demands:
/// <list type="bullet">
///   <item>HOST-content roles (Owner/Admin/Host/CoHost) may view a resource whether it is hidden or
///   visible — allowed even with NO rule (docs/06 "View host-only content" = yes).</item>
///   <item>AUDIENCE roles (Participant/Observer) may view a resource ONLY when a rule makes it visible
///   ("if visible"); a hidden-only or rule-less resource is denied (deny-by-default).</item>
///   <item>The audit role and any undefined role are DENIED even when the resource is visible
///   (Auditor is audit-only, not a live content grant; threats T1/T5).</item>
///   <item>ISOLATION: a visible rule in another WORKSPACE or another TENANT never makes the resource
///   visible to an audience viewer of this workspace/tenant (organization boundary before workspace
///   boundary before resource-level visibility; threat T5).</item>
/// </list>
/// "idempotency tests" and "projection tests" target later stories (the reveal command CORE-VIS-004 /
/// the participant projection CORE-VIS-003), not this read-only access decision.
///
/// All fixtures are GENERIC — no vertical vocabulary appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class VisibilityPolicyTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public VisibilityPolicyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private VisibilityPolicy CreatePolicy(LiveCoreDbContext context)
        => new(new VisibilityRuleRepository(context));

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task SeedRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(organizationId, workspaceId, resourceType, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        Assert.Equal(VisibilityRuleAddResult.Added, await repository.AddAsync(rule, CancellationToken.None));
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization.Id, workspace.Id);
    }

    // --- Host-content roles see everything (no rule needed) --------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task Host_role_can_view_a_resource_with_no_rule(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // No rule seeded: a hidden-by-default resource is still visible to a host role.

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, role, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByHostRole, decision.Reason);
    }

    [Fact]
    public async Task Host_role_can_view_a_hidden_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, MembershipRole.Host, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByHostRole, decision.Reason);
    }

    // --- Audience roles: only if visible ---------------------------------------

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_can_view_a_visible_resource(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, role, VisibilityResourceType.Scene, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByVisibleRule, decision.Reason);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public async Task Audience_role_cannot_view_a_hidden_resource(MembershipRole role)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Hidden);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, role, VisibilityResourceType.Scene, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    [Fact]
    public async Task Audience_role_cannot_view_a_resource_with_no_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // No rule at all -> host-only by default -> denied to the audience.

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    [Fact]
    public async Task Audience_role_can_view_when_any_of_several_rules_is_visible()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // Two rules for the same resource (the non-unique index permits this): one hidden, one
        // visible. Any visible rule grants visibility.
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, MembershipRole.Observer, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.True(decision.CanView);
        Assert.Equal(VisibilityAccessReason.GrantedByVisibleRule, decision.Reason);
    }

    // --- Audit role / undefined role: denied even when visible -----------------

    [Fact]
    public async Task Auditor_is_denied_even_for_a_visible_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, MembershipRole.Auditor, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        // Auditor is audit-only on both content rows: a visible resource does not grant it live access.
        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedRoleNotPermitted, decision.Reason);
    }

    [Fact]
    public async Task Undefined_role_is_denied_even_for_a_visible_resource()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        var decision = await policy.CanViewResourceAsync(
            org, ws, (MembershipRole)999, VisibilityResourceType.ContentBlock, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedRoleNotPermitted, decision.Reason);
    }

    // --- Isolation: a visible rule elsewhere does not grant visibility ---------

    [Fact]
    public async Task A_visible_rule_in_another_workspace_does_not_grant_visibility()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        // The resource is visible in workspace B only.
        await SeedRuleAsync(organization.Id, workspaceB.Id, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        // An audience viewer of workspace A must NOT see it (the rule is in another workspace).
        var decision = await policy.CanViewResourceAsync(
            organization.Id, workspaceA.Id, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    [Fact]
    public async Task A_visible_rule_in_another_tenant_does_not_grant_visibility()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        // The resource is visible in tenant B only.
        await SeedRuleAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        await using var context = CreateContext();
        var policy = CreatePolicy(context);
        // An audience viewer of tenant A must NOT see it (the rule is in another tenant). The
        // organization boundary is checked before the workspace boundary (threat T5).
        var decision = await policy.CanViewResourceAsync(
            orgA, wsA, MembershipRole.Participant, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.False(decision.CanView);
        Assert.Equal(VisibilityAccessReason.DeniedNotVisible, decision.Reason);
    }

    // --- Guards ----------------------------------------------------------------

    [Fact]
    public async Task Empty_ids_are_rejected()
    {
        await using var context = CreateContext();
        var policy = CreatePolicy(context);

        var org = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var resource = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            Guid.Empty, ws, MembershipRole.Owner, VisibilityResourceType.Entity, resource, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            org, Guid.Empty, MembershipRole.Owner, VisibilityResourceType.Entity, resource, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.CanViewResourceAsync(
            org, ws, MembershipRole.Owner, VisibilityResourceType.Entity, Guid.Empty, CancellationToken.None));
    }
}
