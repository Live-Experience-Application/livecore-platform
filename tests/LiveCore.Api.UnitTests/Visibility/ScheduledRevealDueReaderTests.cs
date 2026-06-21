// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="ScheduledRevealDueReader"/> (CORE-VSEAL-002) — the cross-tenant due-rule read the
/// worker sweep issues. Driven against in-memory SQLite (the same harness as the other Visibility tests). The
/// read must return exactly the rules that are DUE for an automatic reveal — HIDDEN, carrying a
/// <c>scheduled_reveal_at</c> at or before the sweep time, and NOT sealed — and exclude everything else, while
/// projecting only the coordinates the sweep needs (never content; threat T7).
/// </summary>
public sealed class ScheduledRevealDueReaderTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _past = _now - TimeSpan.FromHours(1);
    private static readonly DateTimeOffset _future = _now + TimeSpan.FromHours(1);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ScheduledRevealDueReaderTests()
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

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedSessionAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        var workspace = Workspace.Create(organization.Id, slug, slug, _now);
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
        DateTimeOffset? scheduledRevealAt,
        VisibilityState visibility = VisibilityState.Hidden,
        bool locked = false)
    {
        var rule = VisibilityRule.Create(org, ws, session, VisibilityResourceType.Entity, Guid.CreateVersion7(), visibility, _now, scheduledRevealAt);
        if (locked)
        {
            rule.Lock(_now);
        }

        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
        return rule;
    }

    private async Task<IReadOnlyList<DueScheduledReveal>> ListDueAsync(int maxCount = 50)
    {
        await using var context = CreateContext();
        return await new ScheduledRevealDueReader(context).ListDueRulesAsync(_now, maxCount, CancellationToken.None);
    }

    [Fact]
    public async Task Returns_a_hidden_rule_whose_schedule_is_due_with_its_coordinates()
    {
        var (org, ws, session) = await SeedSessionAsync("northwind-labs");
        var rule = await SeedRuleAsync(org, ws, session, scheduledRevealAt: _past);

        var due = Assert.Single(await ListDueAsync());

        Assert.Equal(rule.Id, due.RuleId);
        Assert.Equal(org, due.OrganizationId);
        Assert.Equal(ws, due.WorkspaceId);
        Assert.Equal(session, due.SessionId);
        Assert.Equal(VisibilityResourceType.Entity, due.ResourceType);
        Assert.Equal(rule.ResourceId, due.ResourceId);
        Assert.Null(due.TargetParticipantId);
    }

    [Fact]
    public async Task Excludes_a_rule_scheduled_in_the_future()
    {
        var (org, ws, session) = await SeedSessionAsync("northwind-labs");
        await SeedRuleAsync(org, ws, session, scheduledRevealAt: _future);

        Assert.Empty(await ListDueAsync());
    }

    [Fact]
    public async Task Excludes_a_rule_with_no_schedule()
    {
        var (org, ws, session) = await SeedSessionAsync("northwind-labs");
        await SeedRuleAsync(org, ws, session, scheduledRevealAt: null);

        Assert.Empty(await ListDueAsync());
    }

    [Fact]
    public async Task Excludes_an_already_visible_rule()
    {
        var (org, ws, session) = await SeedSessionAsync("northwind-labs");
        await SeedRuleAsync(org, ws, session, scheduledRevealAt: _past, visibility: VisibilityState.Visible);

        Assert.Empty(await ListDueAsync());
    }

    [Fact]
    public async Task Excludes_a_sealed_due_rule()
    {
        var (org, ws, session) = await SeedSessionAsync("northwind-labs");
        await SeedRuleAsync(org, ws, session, scheduledRevealAt: _past, locked: true);

        Assert.Empty(await ListDueAsync());
    }

    [Fact]
    public async Task Spans_tenants_and_is_bounded_by_the_batch_size()
    {
        var (orgA, wsA, sessionA) = await SeedSessionAsync("northwind-labs");
        var (orgB, wsB, sessionB) = await SeedSessionAsync("acme-co");
        await SeedRuleAsync(orgA, wsA, sessionA, scheduledRevealAt: _past);
        await SeedRuleAsync(orgB, wsB, sessionB, scheduledRevealAt: _past);
        await SeedRuleAsync(orgB, wsB, sessionB, scheduledRevealAt: _past);

        // It is a system read that spans tenants.
        Assert.Equal(3, (await ListDueAsync()).Count);
        // Bounded by the batch size (threat T9).
        Assert.Equal(2, (await ListDueAsync(maxCount: 2)).Count);
    }

    [Fact]
    public async Task Rejects_a_non_positive_batch_size()
    {
        await using var context = CreateContext();
        var reader = new ScheduledRevealDueReader(context);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListDueRulesAsync(_now, 0, CancellationToken.None));
    }
}
