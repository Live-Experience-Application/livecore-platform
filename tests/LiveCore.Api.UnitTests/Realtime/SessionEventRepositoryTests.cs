// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventRepository"/> (CORE-RT-003), driven against an in-memory SQLite
/// database with foreign keys enforced (mirroring the other repository tests). They cover the append +
/// the tenant- and session-scoped read in append order, plus the mandatory isolation negatives: one
/// session's or one tenant's events are never returned through another's id (threat T5/T1). All fixtures
/// are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SessionEventRepositoryTests()
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

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedSessionAsync(
        string organizationSlug = "northwind-labs",
        string workspaceSlug = "summer-show")
    {
        var organization = Organization.Create(organizationSlug, organizationSlug, _now);
        var workspace = Workspace.Create(organization.Id, workspaceSlug, workspaceSlug, _now);
        var session = Session.Create(organization.Id, workspace.Id, "Session", _now);
        await using var context = CreateContext();
        context.Organizations.Add(organization);
        context.Workspaces.Add(workspace);
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return (organization.Id, workspace.Id, session.Id);
    }

    private async Task AppendAsync(SessionEvent sessionEvent)
    {
        await using var context = CreateContext();
        await new SessionEventRepository(context).AppendAsync(sessionEvent, CancellationToken.None);
    }

    private async Task<IReadOnlyList<SessionEvent>> ListAsync(Guid organizationId, Guid sessionId)
    {
        await using var context = CreateContext();
        return await new SessionEventRepository(context).ListBySessionAsync(organizationId, sessionId, CancellationToken.None);
    }

    private static SessionEvent Event(Guid org, Guid ws, Guid session, Guid? target = null)
        => SessionEvent.Create(org, ws, session, SessionEventTypes.ContentRevealed, Guid.NewGuid(), target, "{}", 1, _now);

    [Fact]
    public async Task Append_then_list_returns_all_events_in_append_order()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var first = Event(org, ws, session);
        var second = Event(org, ws, session);

        await AppendAsync(first);
        await AppendAsync(second);

        var events = await ListAsync(org, session);
        Assert.Equal(2, events.Count);
        // The order is the per-session sequence (CORE-RTC-001), which is append order — no longer the
        // "stable but unspecified" id order. The first appended event reads back first.
        Assert.Equal(new[] { first.Id, second.Id }, events.Select(e => e.Id));
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Events_appended_within_one_millisecond_read_back_in_append_order()
    {
        // The required test: several events appended at the SAME created_at (_now). The UUIDv7 id is only
        // monotonic at millisecond resolution, so ordering by id could reorder these; the per-session
        // sequence preserves append order exactly (CORE-RTC-001).
        var (org, ws, session) = await SeedSessionAsync();
        var appended = new List<SessionEvent>();
        for (var i = 0; i < 8; i++)
        {
            var sessionEvent = Event(org, ws, session);
            await AppendAsync(sessionEvent);
            appended.Add(sessionEvent);
        }

        var events = await ListAsync(org, session);
        // Read-back order equals append order, and the sequence is the gap-free 1..N run.
        Assert.Equal(appended.Select(e => e.Id), events.Select(e => e.Id));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task The_sequence_is_per_session_gap_free_and_starts_at_one()
    {
        // Each session's sequence is independent and starts at 1: appending to session A then B then A
        // again yields A=[1,2], B=[1] — the counter is per session, not global.
        var (org, ws, sessionA) = await SeedSessionAsync();
        var sessionB = Session.Create(org, ws, "Other", _now);
        await using (var context = CreateContext())
        {
            context.Sessions.Add(sessionB);
            await context.SaveChangesAsync();
        }

        await AppendAsync(Event(org, ws, sessionA));
        await AppendAsync(Event(org, ws, sessionB.Id));
        await AppendAsync(Event(org, ws, sessionA));

        Assert.Equal(new long[] { 1, 2 }, (await ListAsync(org, sessionA)).Select(e => e.Sequence));
        Assert.Equal(new long[] { 1 }, (await ListAsync(org, sessionB.Id)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task A_missing_event_is_detectable_as_a_sequence_gap()
    {
        // Because the server emits a contiguous per-session run, a client that received only some of the
        // events detects a missed one purely from the gap in the sequence (CORE-RTC-001).
        var (org, ws, session) = await SeedSessionAsync();
        await AppendAsync(Event(org, ws, session));
        await AppendAsync(Event(org, ws, session));
        await AppendAsync(Event(org, ws, session));

        var sequences = (await ListAsync(org, session)).Select(e => e.Sequence).ToList();

        // The full stream is contiguous (no gap).
        Assert.True(IsContiguous(sequences));
        // A client that dropped the middle event sees a non-contiguous run — the gap reveals the loss.
        Assert.False(IsContiguous(new[] { sequences[0], sequences[2] }));
    }

    private static bool IsContiguous(IReadOnlyList<long> sequences)
    {
        for (var index = 1; index < sequences.Count; index++)
        {
            if (sequences[index] != sequences[index - 1] + 1)
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public async Task List_is_scoped_to_its_session()
    {
        var (org, ws, sessionA) = await SeedSessionAsync();
        var sessionB = Session.Create(org, ws, "Other", _now);
        await using (var context = CreateContext())
        {
            context.Sessions.Add(sessionB);
            await context.SaveChangesAsync();
        }

        await AppendAsync(Event(org, ws, sessionA));
        await AppendAsync(Event(org, ws, sessionB.Id));

        var inA = await ListAsync(org, sessionA);
        var inB = await ListAsync(org, sessionB.Id);
        Assert.Single(inA);
        Assert.Single(inB);
        Assert.NotEqual(inA[0].Id, inB[0].Id);
    }

    [Fact]
    public async Task List_is_scoped_to_its_tenant()
    {
        var (orgA, wsA, sessionA) = await SeedSessionAsync("northwind-labs", "summer-show");
        var (orgB, _, _) = await SeedSessionAsync("acme-co", "b-show");

        await AppendAsync(Event(orgA, wsA, sessionA));

        // The same session id is never returned through another tenant's id.
        Assert.Empty(await ListAsync(orgB, sessionA));
        Assert.Single(await ListAsync(orgA, sessionA));
    }

    [Fact]
    public async Task List_rejects_empty_ids()
    {
        var (org, _, session) = await SeedSessionAsync();
        await using var context = CreateContext();
        var repository = new SessionEventRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySessionAsync(Guid.Empty, session, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListBySessionAsync(org, Guid.Empty, CancellationToken.None));
    }
}
