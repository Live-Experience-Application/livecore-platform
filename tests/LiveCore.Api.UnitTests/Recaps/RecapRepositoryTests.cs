using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="RecapRepository"/> (CORE-AUD-004).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, SQL translation, the foreign keys into
/// <c>sessions</c>/<c>workspaces</c>/<c>organizations</c>/<c>users</c>, the set-null on producer deletion and
/// the cascade-on-delete from the session/workspace are exercised on every run without any database server or
/// Docker. The behaviors under test (scoped equality lookups, list-by-session, full isolation between
/// workspaces and between tenants, and the cascade/set-null deletes) are relational semantics shared with
/// PostgreSQL; provider-specific verification happens against PostgreSQL in the deployment pipeline
/// (livecore-deploy).
///
/// The epic acceptance is that "Audit/export/recap are generic and authorized". At the model + repository
/// level (no HTTP endpoints yet — the recap endpoint is a later story, exactly as CORE-AUD-002/003 deferred
/// the export endpoint):
/// <list type="bullet">
///   <item>ROUND-TRIP: a recap round-trips through the database, including its host-only body and the
///   nullable producer.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 tenant isolation; threat T1 broken object-level
///   authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before workspace
///   boundary): a recap in workspace W1 is never returned via workspace W2; a recap in organization A is never
///   resolved via organization B; and the foreign keys reject a recap referencing a non-existent session — so
///   a recap can never escape its tenant/workspace boundary.</item>
///   <item>LIST-BY-SESSION: a session's recaps are returned in produced order and a foreign-tenant/workspace
///   list returns nothing.</item>
///   <item>CASCADE / SET NULL: deleting the session or workspace removes the recap; deleting the producing
///   user anonymizes (does not delete) the recap.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md). All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class RecapRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private const string _summary = "Session opened; three scenes prepared and one entity revealed to the audience.";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public RecapRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step still uses
        // its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK constraints in the
        // model are genuinely exercised.
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

    private async Task<UserProfile> SeedUserAsync(string issuer, string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId),
            _createdAt);
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        Assert.Equal(UserProfileAddResult.Added, await repository.AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<Recap> SeedHostRecapAsync(Guid organizationId, Guid workspaceId, Guid sessionId, Guid generatedBy)
    {
        var recap = Recap.GenerateByHost(organizationId, workspaceId, sessionId, generatedBy, _summary, _generatedAt);
        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        await repository.AppendAsync(recap, CancellationToken.None);
        return recap;
    }

    [Fact]
    public async Task Recap_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(session.Id, loaded.SessionId);
        Assert.Equal(user.Id, loaded.GeneratedByUserProfileId);
        Assert.Equal(Recap.CurrentRecapVersion, loaded.RecapVersion);
        Assert.Equal(_summary, loaded.Summary);
        Assert.Equal(_generatedAt, loaded.GeneratedAt);
    }

    [Fact]
    public async Task System_recap_round_trips_with_a_null_producer()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var recap = Recap.GenerateBySystem(organization.Id, workspace.Id, session.Id, _summary, _generatedAt);

        await using (var writeContext = CreateContext())
        {
            await new RecapRepository(writeContext).AppendAsync(recap, CancellationToken.None);
        }

        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, recap.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.GeneratedByUserProfileId);
    }

    [Fact]
    public async Task ListBySession_returns_the_sessions_recaps_in_produced_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);

        var first = await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);
        var second = await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        var recaps = await repository.ListBySessionAsync(organization.Id, workspace.Id, session.Id, CancellationToken.None);

        // Both recaps are returned, ordered by the time-ordered surrogate id (produced order).
        Assert.Equal(new[] { first.Id, second.Id }, recaps.Select(recap => recap.Id).ToArray());
    }

    [Fact]
    public async Task FindById_for_a_missing_recap_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new RecapRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListBySession_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new RecapRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySessionAsync(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySessionAsync(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySessionAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_recap_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a recap exists in workspace W1 only. Looking it up by
        // id under workspace W2, and listing W1's session under W2, must both return nothing even though both
        // the recap and W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var session = await SeedSessionAsync(organization.Id, workspace1.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedHostRecapAsync(organization.Id, workspace1.Id, session.Id, user.Id);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);

        var foundInW1 = await repository.FindByIdAsync(organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);
        var bySessionViaW2 = await repository.ListBySessionAsync(organization.Id, workspace2.Id, session.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
        Assert.Empty(bySessionViaW2);
    }

    [Fact]
    public async Task A_recap_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md: organization
        // boundary checked before workspace boundary): a recap exists in a workspace owned by organization A.
        // Looking the SAME workspace id and recap id up under organization B's id must return null even though
        // the workspace id and recap id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organizationA.Id, workspaceInA.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedHostRecapAsync(organizationA.Id, workspaceInA.Id, session.Id, user.Id);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);

        var underA = await repository.FindByIdAsync(organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var bySessionUnderB = await repository.ListBySessionAsync(organizationB.Id, workspaceInA.Id, session.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
        Assert.Empty(bySessionUnderB);
    }

    [Fact]
    public async Task A_recap_cannot_reference_a_session_that_does_not_exist()
    {
        // The session_id foreign key is enforced (PRAGMA foreign_keys = ON): a recap for a non-existent session
        // is rejected, so a dangling recap can never exist outside a real session (threats T1/T5). The
        // organization and workspace are real to isolate the session FK; the session is NOT persisted.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Recap.GenerateBySystem(organization.Id, workspace.Id, Guid.CreateVersion7(), _summary, _generatedAt);

        await using var context = CreateContext();
        var repository = new RecapRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AppendAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_session_cascades_to_its_recaps()
    {
        // The session_id foreign key cascades on delete: a recap has no meaning without its session, so
        // removing the session removes its recaps.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedSession = await deleteContext.Sessions.SingleAsync(s => s.Id == session.Id);
            deleteContext.Sessions.Remove(storedSession);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        Assert.Equal(0, await context.Recaps.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_workspace_cascades_to_its_recaps()
    {
        // The workspace_id foreign key cascades on delete: removing the workspace removes its recaps (and its
        // sessions).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedWorkspace = await deleteContext.Workspaces.SingleAsync(ws => ws.Id == workspace.Id);
            deleteContext.Workspaces.Remove(storedWorkspace);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        Assert.Equal(0, await context.Recaps.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_producing_user_anonymizes_the_recap()
    {
        // The generated_by foreign key SETS NULL on delete: the record of a recap is retained when its producer
        // is removed (the recap survives, anonymized), mirroring export_jobs.requested_by.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening night");
        var user = await SeedUserAsync(_issuer, _subject);
        var recap = await SeedHostRecapAsync(organization.Id, workspace.Id, session.Id, user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedUser = await deleteContext.UserProfiles.SingleAsync(u => u.Id == user.Id);
            deleteContext.UserProfiles.Remove(storedUser);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var repository = new RecapRepository(context);
        var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, recap.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.GeneratedByUserProfileId);
        Assert.Equal(_summary, loaded.Summary);
    }
}
