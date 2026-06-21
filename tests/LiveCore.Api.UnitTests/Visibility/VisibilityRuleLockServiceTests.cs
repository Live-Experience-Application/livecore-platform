// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="VisibilityRuleLockService"/> (CORE-VSEAL-001) — the visibility-rule SEAL (lock) /
/// unseal (unlock) command. The service is driven against an in-memory SQLite database with foreign keys
/// enforced (<c>PRAGMA foreign_keys = ON</c>), over the real visibility-rule repository, the real audit log
/// repository and the real <see cref="TransactionalUnitOfWork"/>, so the atomic rule update + audit append run
/// against genuinely persisted state.
///
/// Coverage (the story's required tests):
/// <list type="bullet">
///   <item>Locking seals the rule and audits the orthogonal Unlocked-&gt;Locked transition; unlocking clears
///   the seal and audits the inverse — never touching the binary visibility state.</item>
///   <item>IDEMPOTENT: re-locking (or re-unlocking) is a no-op that changes nothing and audits nothing.</item>
///   <item>FAIL-CLOSED / ISOLATION (threats T1/T5/T3): an unknown rule, a rule in another tenant, another
///   workspace or another session all return null (the endpoint hides them as 404) and change NOTHING.</item>
///   <item>ARGUMENT GUARDS: empty required ids are rejected.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class VisibilityRuleLockServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 21, 9, 0, 0, TimeSpan.Zero);

    // A fixed, non-empty actor (the authoring role who changes the lock); recorded as the audit actor (a
    // recorded fact, not a foreign key, mirroring the reveal command tests).
    private static readonly Guid _actor = Guid.CreateVersion7();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public VisibilityRuleLockServiceTests()
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

    // The service and every repository it composes MUST share one context so the explicit transaction enrols
    // each repository's SaveChanges.
    private static VisibilityRuleLockService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new VisibilityRuleRepository(context),
            new AuditLogRepository(context));

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedSessionAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = "summer-show")
    {
        var organization = Organization.Create(organizationSlug, organizationSlug, _now);
        var workspace = Workspace.Create(organization.Id, workspaceSlug, workspaceSlug, _now);
        var session = Session.Create(organization.Id, workspace.Id, "Live Session", _now);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return (organization.Id, workspace.Id, session.Id);
    }

    private async Task<VisibilityRule> SeedRuleAsync(
        Guid org,
        Guid ws,
        Guid session,
        VisibilityState visibility = VisibilityState.Hidden,
        bool locked = false)
    {
        var rule = VisibilityRule.Create(org, ws, session, VisibilityResourceType.Entity, Guid.CreateVersion7(), visibility, _now);
        if (locked)
        {
            rule.Lock(_now);
        }

        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
        return rule;
    }

    private async Task<VisibilityRule?> LoadRuleAsync(Guid org, Guid ws, Guid session, Guid ruleId)
    {
        await using var context = CreateContext();
        return await new VisibilityRuleRepository(context)
            .FindByIdInSessionAsync(org, ws, session, ruleId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(Guid org)
    {
        await using var context = CreateContext();
        return await new AuditLogRepository(context).ListByOrganizationAsync(org, CancellationToken.None);
    }

    private async Task<VisibilityRule?> SetLockAsync(Guid org, Guid ws, Guid session, Guid ruleId, bool locked)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.SetLockAsync(org, ws, session, ruleId, locked, _actor, _now, CancellationToken.None);
    }

    // --- Lock / unlock + audit -------------------------------------------------

    [Fact]
    public async Task Lock_seals_the_rule_and_audits_the_transition()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityState.Visible);

        var result = await SetLockAsync(org, ws, session, rule.Id, locked: true);

        Assert.NotNull(result);
        Assert.True(result.Locked);
        // The seal is persisted; the binary visibility state is untouched (orthogonal).
        var reloaded = await LoadRuleAsync(org, ws, session, rule.Id);
        Assert.True(reloaded!.Locked);
        Assert.True(reloaded.IsVisibleToAudience());

        var entry = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(AuditAction.VisibilityRuleLockChanged, entry.Action);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(_actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(VisibilityResourceType.Entity), entry.ResourceType);
        Assert.Equal(rule.ResourceId, entry.ResourceId);
        Assert.Equal("Unlocked", entry.PreviousState);
        Assert.Equal("Locked", entry.NewState);
    }

    [Fact]
    public async Task Unlock_clears_the_seal_and_audits_the_inverse_transition()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityState.Hidden, locked: true);

        var result = await SetLockAsync(org, ws, session, rule.Id, locked: false);

        Assert.NotNull(result);
        Assert.False(result.Locked);
        var reloaded = await LoadRuleAsync(org, ws, session, rule.Id);
        Assert.False(reloaded!.Locked);

        var entry = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(AuditAction.VisibilityRuleLockChanged, entry.Action);
        Assert.Equal("Locked", entry.PreviousState);
        Assert.Equal("Unlocked", entry.NewState);
    }

    [Fact]
    public async Task Re_locking_an_already_locked_rule_is_a_no_op_that_audits_nothing()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityState.Hidden, locked: true);

        var result = await SetLockAsync(org, ws, session, rule.Id, locked: true);

        Assert.NotNull(result);
        Assert.True(result.Locked);
        // Idempotent: no second audit record for a no-op lock.
        Assert.Empty(await ListAuditAsync(org));
    }

    [Fact]
    public async Task Unlocking_an_already_unlocked_rule_is_a_no_op_that_audits_nothing()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityState.Hidden);

        var result = await SetLockAsync(org, ws, session, rule.Id, locked: false);

        Assert.NotNull(result);
        Assert.False(result.Locked);
        Assert.Empty(await ListAuditAsync(org));
    }

    // --- Fail-closed / isolation (threats T1/T5/T3) ----------------------------

    [Fact]
    public async Task An_unknown_rule_is_not_found_and_changes_nothing()
    {
        var (org, ws, session) = await SeedSessionAsync();

        var result = await SetLockAsync(org, ws, session, Guid.CreateVersion7(), locked: true);

        Assert.Null(result);
        Assert.Empty(await ListAuditAsync(org));
    }

    [Fact]
    public async Task A_rule_in_another_tenant_is_not_found_through_this_tenant()
    {
        var (orgA, _, _) = await SeedSessionAsync(_organizationSlugA, "ws-a");
        var (orgB, wsB, sessionB) = await SeedSessionAsync(_organizationSlugB, "ws-b");
        var ruleInB = await SeedRuleAsync(orgB, wsB, sessionB, VisibilityState.Hidden);

        // Address tenant B's rule with tenant A's organization id: never found (threat T5).
        var result = await SetLockAsync(orgA, wsB, sessionB, ruleInB.Id, locked: true);

        Assert.Null(result);
        // The rule in B is untouched.
        var reloaded = await LoadRuleAsync(orgB, wsB, sessionB, ruleInB.Id);
        Assert.False(reloaded!.Locked);
        Assert.Empty(await ListAuditAsync(orgB));
    }

    [Fact]
    public async Task A_rule_of_another_session_in_the_same_workspace_is_not_found_through_this_session()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var otherSession = Session.Create(org, ws, "Other", _now);
        await using (var context = CreateContext())
        {
            context.Sessions.Add(otherSession);
            await context.SaveChangesAsync();
        }

        var rule = await SeedRuleAsync(org, ws, session, VisibilityState.Hidden);

        // The rule belongs to `session`; addressing it through `otherSession` finds nothing (cross-session
        // isolation, threat T5/T3).
        var result = await SetLockAsync(org, ws, otherSession.Id, rule.Id, locked: true);

        Assert.Null(result);
        var reloaded = await LoadRuleAsync(org, ws, session, rule.Id);
        Assert.False(reloaded!.Locked);
    }

    // --- Argument guards -------------------------------------------------------

    [Fact]
    public async Task SetLock_rejects_empty_ids()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var ruleId = Guid.CreateVersion7();
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLockAsync(
            Guid.Empty, ws, session, ruleId, true, _actor, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLockAsync(
            org, Guid.Empty, session, ruleId, true, _actor, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLockAsync(
            org, ws, Guid.Empty, ruleId, true, _actor, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLockAsync(
            org, ws, session, Guid.Empty, true, _actor, _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetLockAsync(
            org, ws, session, ruleId, true, Guid.Empty, _now, CancellationToken.None));
    }
}
