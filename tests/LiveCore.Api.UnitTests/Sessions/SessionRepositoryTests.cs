using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Sessions;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="SessionRepository"/>
/// (CORE-SES-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation,
/// the foreign keys into <c>organizations</c>/<c>workspaces</c> and the
/// status-name conversion are exercised on every test run without any database
/// server or Docker. The behaviors under test (scoped equality lookups, full
/// isolation between workspaces and between tenants, the lifecycle round-trip and
/// the status stored as its name) are relational semantics shared with PostgreSQL;
/// provider-specific verification happens against PostgreSQL in the deployment
/// pipeline (livecore-deploy) and the isolation test story.
///
/// The epic acceptance for Sessions and Participants is that sessions are generic,
/// with no vertical-specific terms, and with required "Lifecycle tests and
/// authorization tests". At the aggregate + repository level (no HTTP endpoints
/// yet — create/start/end endpoints and the SessionCreated/Started/Ended events
/// are CORE-SES-004):
/// <list type="bullet">
///   <item>LIFECYCLE: a session round-trips through the database and its
///   Prepared -&gt; Live -&gt; Ended transitions (with the live-timeline
///   timestamps) persist; the status is stored as its stable NAME.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
///   authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked
///   before workspace boundary): a session in workspace W1 is never returned via
///   workspace W2; a session in organization A is never resolved via organization
///   B; and the foreign keys reject a session referencing a non-existent workspace
///   or organization.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class SessionRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endedAt = new(2026, 6, 11, 10, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SessionRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive
        // while every step still uses its own context, so reads genuinely
        // round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so
        // the FK constraints in the model are genuinely exercised.
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

    private async Task<Session> SeedSessionAsync(Guid organizationId, Guid workspaceId, string title)
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        Assert.Equal(SessionAddResult.Added, await repository.AddAsync(session, CancellationToken.None));
        return session;
    }

    [Fact]
    public async Task Prepared_session_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedSessionAsync(organization.Id, workspace.Id, "Opening Run");

        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal("Opening Run", loaded.Title);
        Assert.Equal(SessionStatus.Prepared, loaded.Status);
        Assert.Null(loaded.StartedAt);
        Assert.Null(loaded.EndedAt);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lifecycle_start_then_end_persists_through_updates()
    {
        // Lifecycle test (required-tests column): a prepared session is started and
        // then ended, and both transitions (with their live-timeline timestamps)
        // round-trip through the database.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedSessionAsync(organization.Id, workspace.Id, "Run");

        await using (var context = CreateContext())
        {
            var repository = new SessionRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(SessionStatus.Prepared, loaded.Status);

            loaded.Start(_startedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SessionRepository(context);
            var live = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(live);
            Assert.Equal(SessionStatus.Live, live.Status);
            Assert.Equal(_startedAt, live.StartedAt);
            Assert.Null(live.EndedAt);

            live.End(_endedAt);
            await repository.UpdateAsync(live, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SessionRepository(context);
            var ended = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(ended);
            Assert.Equal(SessionStatus.Ended, ended.Status);
            Assert.Equal(_startedAt, ended.StartedAt);
            Assert.Equal(_endedAt, ended.EndedAt);
            Assert.True(ended.EndedAt >= ended.StartedAt);
        }
    }

    [Fact]
    public async Task Status_is_stored_as_its_stable_name_in_the_database()
    {
        // The status column persists the stable NAME, not the numeric value
        // (HasConversion<string>), so the stored lifecycle value stays readable and
        // is not coupled to the declaration-order integers. Read the raw column back
        // to assert the stored text.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedSessionAsync(organization.Id, workspace.Id, "Run");

        await using (var context = CreateContext())
        {
            var repository = new SessionRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.Start(_startedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using var assertionContext = CreateContext();
        // Read the raw status column back as text. Only one session exists in this
        // test, so no id filter is needed (and binding a Guid parameter across the
        // provider's storage format is avoided). The stored value is the enum NAME,
        // never the integer discriminator.
        var storedStatuses = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT status AS Value FROM sessions")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(SessionStatus.Live), Assert.Single(storedStatuses));
    }

    [Fact]
    public async Task FindById_for_a_missing_session_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SessionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a session exists in
        // workspace W1 only. Looking it up by id under workspace W2 must return null
        // even though both the session and W2 exist. A cross-workspace lookup
        // returns null/deny.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedSessionAsync(organization.Id, workspace1.Id, "Run");

        await using var context = CreateContext();
        var repository = new SessionRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_session_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary): a session exists in a workspace owned by organization
        // A. Looking the SAME workspace id and session id up under organization B's
        // id must return null/deny even though the workspace id and session id are
        // correct: the wrong tenant denies access.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var inA = await SeedSessionAsync(organizationA.Id, workspaceInA.Id, "Run");

        await using var context = CreateContext();
        var repository = new SessionRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task Many_sessions_with_the_same_title_can_coexist_in_one_workspace()
    {
        // A session has no natural key (no slug/title uniqueness): it is identified
        // only by its surrogate id, so a workspace may hold many sessions with the
        // same title and each resolves independently by id.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var first = await SeedSessionAsync(organization.Id, workspace.Id, "Same Title");
        var second = await SeedSessionAsync(organization.Id, workspace.Id, "Same Title");

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a
        // session for a non-existent workspace is rejected, so a dangling session can
        // never exist outside a real workspace boundary (threat T5). The
        // organization is real to isolate the workspace FK as the failing
        // constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = Session.Create(organization.Id, Guid.CreateVersion7(), "Run", _createdAt);

        await using var context = CreateContext();
        var repository = new SessionRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a session whose tenant does
        // not exist is rejected even when the workspace exists, so the row can never
        // carry a tenant boundary that is not a real organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Session.Create(Guid.CreateVersion7(), workspace.Id, "Run", _createdAt);

        await using var context = CreateContext();
        var repository = new SessionRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }
}
