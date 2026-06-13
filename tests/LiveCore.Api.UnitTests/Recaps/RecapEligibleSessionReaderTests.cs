using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="RecapEligibleSessionReader"/> (CORE-JOB-001) —
/// the system read that finds the sessions a recap should be generated for (ended sessions with no recap yet).
///
/// They run against an in-memory SQLite database (the same harness as <see cref="RecapRepositoryTests"/>), so
/// the real model mapping and the anti-join SQL translation are exercised on every run without a database
/// server. The behaviors under test are the job's acceptance criteria at the data layer:
/// <list type="bullet">
///   <item>ELIGIBILITY: only ENDED sessions are returned — a Prepared, Live or Cancelled session never needs
///   a recap.</item>
///   <item>IDEMPOTENCY: a session that already has a recap is excluded, so a recap is produced at most once
///   per session across any number of sweeps.</item>
///   <item>TENANT-SCOPED (threat T5): the sweep spans tenants (a system job) but each returned entry carries
///   its own session's organization, workspace and id.</item>
///   <item>BOUNDED + ORDERED: the result honors the batch size and is ordered by the time-ordered surrogate
///   session id (oldest first), so repeated sweeps cover everything.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class RecapEligibleSessionReaderTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endedAt = new(2026, 6, 13, 10, 0, 0, TimeSpan.Zero);
    private const string _summary = "Automated recap for a concluded session.";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public RecapEligibleSessionReaderTests()
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

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            WorkspaceAddResult.Added,
            await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    /// <summary>Seeds a session and drives it to the requested lifecycle status before persisting it.</summary>
    private async Task<Session> SeedSessionAsync(
        Guid organizationId, Guid workspaceId, string title, SessionStatus status)
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        switch (status)
        {
            case SessionStatus.Prepared:
                break;
            case SessionStatus.Live:
                session.Start(_startedAt);
                break;
            case SessionStatus.Ended:
                session.Start(_startedAt);
                session.End(_endedAt);
                break;
            case SessionStatus.Cancelled:
                session.Cancel(_startedAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status in the fixture.");
        }

        await using var context = CreateContext();
        Assert.Equal(
            SessionAddResult.Added,
            await new SessionRepository(context).AddAsync(session, CancellationToken.None));
        return session;
    }

    private async Task SeedSystemRecapAsync(Guid organizationId, Guid workspaceId, Guid sessionId)
    {
        var recap = Recap.GenerateBySystem(organizationId, workspaceId, sessionId, _summary, _endedAt);
        await using var context = CreateContext();
        await new RecapRepository(context).AppendAsync(recap, CancellationToken.None);
    }

    [Fact]
    public async Task Returns_only_ended_sessions_that_have_no_recap()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");

        var endedNeedingRecap = await SeedSessionAsync(organization.Id, workspace.Id, "Ended", SessionStatus.Ended);
        var endedAlreadyRecapped = await SeedSessionAsync(organization.Id, workspace.Id, "Recapped", SessionStatus.Ended);
        await SeedSystemRecapAsync(organization.Id, workspace.Id, endedAlreadyRecapped.Id);
        // Non-eligible lifecycle states: never returned.
        await SeedSessionAsync(organization.Id, workspace.Id, "Prepared", SessionStatus.Prepared);
        await SeedSessionAsync(organization.Id, workspace.Id, "Live", SessionStatus.Live);
        await SeedSessionAsync(organization.Id, workspace.Id, "Cancelled", SessionStatus.Cancelled);

        await using var context = CreateContext();
        var reader = new RecapEligibleSessionReader(context);
        var eligible = await reader.ListSessionsNeedingRecapAsync(50, CancellationToken.None);

        // Exactly the one ended-and-unrecapped session, carrying its own coordinates and live-timeline times.
        var only = Assert.Single(eligible);
        Assert.Equal(endedNeedingRecap.Id, only.SessionId);
        Assert.Equal(organization.Id, only.OrganizationId);
        Assert.Equal(workspace.Id, only.WorkspaceId);
        Assert.Equal(_startedAt, only.StartedAt);
        Assert.Equal(_endedAt, only.EndedAt);
    }

    [Fact]
    public async Task A_session_with_a_recap_is_excluded_so_generation_is_idempotent()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Ended", SessionStatus.Ended);

        await using (var beforeContext = CreateContext())
        {
            var before = await new RecapEligibleSessionReader(beforeContext)
                .ListSessionsNeedingRecapAsync(50, CancellationToken.None);
            Assert.Equal(session.Id, Assert.Single(before).SessionId);
        }

        // Once a recap exists for the session, it is no longer eligible — the anti-join is the idempotency
        // mechanism the generation service relies on.
        await SeedSystemRecapAsync(organization.Id, workspace.Id, session.Id);

        await using var context = CreateContext();
        var after = await new RecapEligibleSessionReader(context)
            .ListSessionsNeedingRecapAsync(50, CancellationToken.None);

        Assert.Empty(after);
    }

    [Fact]
    public async Task Spans_tenants_returning_each_sessions_own_coordinates()
    {
        // The sweep is a system job, so it returns ended-unrecapped sessions across tenants — but each entry
        // carries its own tenant/workspace/session, so the recap produced from it is scoped to that tenant
        // alone (threat T5).
        var organizationA = await SeedOrganizationAsync("northwind-labs");
        var organizationB = await SeedOrganizationAsync("acme-co");
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, "summer-show");
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, "winter-show");
        var sessionA = await SeedSessionAsync(organizationA.Id, workspaceA.Id, "A", SessionStatus.Ended);
        var sessionB = await SeedSessionAsync(organizationB.Id, workspaceB.Id, "B", SessionStatus.Ended);

        await using var context = CreateContext();
        var eligible = await new RecapEligibleSessionReader(context)
            .ListSessionsNeedingRecapAsync(50, CancellationToken.None);

        var fromA = Assert.Single(eligible, candidate => candidate.SessionId == sessionA.Id);
        Assert.Equal(organizationA.Id, fromA.OrganizationId);
        Assert.Equal(workspaceA.Id, fromA.WorkspaceId);

        var fromB = Assert.Single(eligible, candidate => candidate.SessionId == sessionB.Id);
        Assert.Equal(organizationB.Id, fromB.OrganizationId);
        Assert.Equal(workspaceB.Id, fromB.WorkspaceId);
    }

    [Fact]
    public async Task Honors_the_batch_size_and_orders_oldest_first()
    {
        var organization = await SeedOrganizationAsync("northwind-labs");
        var workspace = await SeedWorkspaceAsync(organization.Id, "summer-show");
        var first = await SeedSessionAsync(organization.Id, workspace.Id, "First", SessionStatus.Ended);
        var second = await SeedSessionAsync(organization.Id, workspace.Id, "Second", SessionStatus.Ended);
        var third = await SeedSessionAsync(organization.Id, workspace.Id, "Third", SessionStatus.Ended);

        await using var context = CreateContext();
        var reader = new RecapEligibleSessionReader(context);

        // The session ids are UUIDv7 (created in order), so the batch surfaces the oldest two first.
        var firstBatch = await reader.ListSessionsNeedingRecapAsync(2, CancellationToken.None);
        Assert.Equal(
            new[] { first.Id, second.Id },
            firstBatch.Select(candidate => candidate.SessionId).ToArray());

        // The full batch returns all three in created (surrogate-id) order; the third trails the first two.
        var fullBatch = await reader.ListSessionsNeedingRecapAsync(50, CancellationToken.None);
        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            fullBatch.Select(candidate => candidate.SessionId).ToArray());
    }

    [Fact]
    public async Task Rejects_a_non_positive_batch_size()
    {
        await using var context = CreateContext();
        var reader = new RecapEligibleSessionReader(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListSessionsNeedingRecapAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListSessionsNeedingRecapAsync(-1, CancellationToken.None));
    }
}
