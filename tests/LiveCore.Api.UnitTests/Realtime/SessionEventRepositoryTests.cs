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
    public async Task Append_then_list_returns_all_events_in_a_deterministic_order()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var first = Event(org, ws, session);
        var second = Event(org, ws, session);

        await AppendAsync(first);
        await AppendAsync(second);

        var events = await ListAsync(org, session);
        Assert.Equal(2, events.Count);
        Assert.Contains(first.Id, events.Select(e => e.Id));
        Assert.Contains(second.Id, events.Select(e => e.Id));

        // The order is deterministic — a second identical query returns the same sequence (ordered by
        // the time-ordered surrogate id; two events created within one millisecond have a stable but
        // unspecified relative order, so the test asserts stability, not a specific pair order).
        var again = await ListAsync(org, session);
        Assert.Equal(events.Select(e => e.Id), again.Select(e => e.Id));
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
