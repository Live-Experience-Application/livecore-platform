using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="ExportJobRepository"/> (CORE-AUD-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys =
/// ON</c>), so the real model mapping, SQL translation, the foreign keys into
/// <c>organizations</c>/<c>workspaces</c>/<c>users</c>, the optional set-null requester link and the
/// scope/status name conversions are exercised on every run without any database server or Docker. The
/// behaviors under test (scoped equality lookups, full isolation between workspaces and between tenants,
/// the lifecycle round-trip, the enums stored as their names and the requester anonymization on user
/// deletion) are relational semantics shared with PostgreSQL; provider-specific verification happens
/// against PostgreSQL in the deployment pipeline (livecore-deploy).
///
/// The epic acceptance is that "Audit/export/recap are generic and authorized". At the model + repository
/// level (no HTTP endpoints yet — the export endpoint/manifest is CORE-AUD-003):
/// <list type="bullet">
///   <item>LIFECYCLE: a job round-trips through the database and its Pending -&gt; Running -&gt; Completed
///   transition (and a Failed reason) persists; the scope and status are stored as their stable
///   NAMES.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 tenant isolation; threat T1 broken
///   object-level authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
///   workspace boundary): a job in workspace W1 is never returned via workspace W2; a job in organization A
///   is never resolved via organization B; and the foreign keys reject a job referencing a non-existent
///   workspace, organization or requesting user — so an export job can never escape its tenant/workspace
///   boundary.</item>
///   <item>REQUESTER ANONYMIZATION: deleting the requesting user sets the job's <c>requested_by</c> to null
///   (the job survives, anonymized) rather than cascade-deleting the job.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md). All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class ExportJobRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _finishedAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ExportJobRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step still
        // uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK constraints in
        // the model are genuinely exercised.
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

    private async Task<ExportJob> SeedJobAsync(
        Guid organizationId, Guid workspaceId, Guid requestedBy, ExportScope scope = ExportScope.Workspace)
    {
        var job = ExportJob.Create(organizationId, workspaceId, requestedBy, scope, _createdAt);
        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        Assert.Equal(ExportJobAddResult.Added, await repository.AddAsync(job, CancellationToken.None));
        return job;
    }

    [Fact]
    public async Task Pending_job_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.UserData);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(user.Id, loaded.RequestedByUserProfileId);
        Assert.Equal(ExportScope.UserData, loaded.Scope);
        Assert.Equal(ExportJobStatus.Pending, loaded.Status);
        Assert.Null(loaded.FailureReason);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lifecycle_start_then_complete_persists_through_updates()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.Start(_startedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var running = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(running);
            Assert.Equal(ExportJobStatus.Running, running.Status);
            running.Complete(_finishedAt);
            await repository.UpdateAsync(running, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var completed = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(completed);
            Assert.Equal(ExportJobStatus.Completed, completed.Status);
            Assert.Null(completed.FailureReason);
            Assert.Equal(_finishedAt, completed.UpdatedAt);
        }
    }

    [Fact]
    public async Task A_failed_jobs_reason_persists_through_an_update()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.Fail("storage adapter unavailable", _startedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var failed = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(failed);
            Assert.Equal(ExportJobStatus.Failed, failed.Status);
            Assert.Equal("storage adapter unavailable", failed.FailureReason);
        }
    }

    [Fact]
    public async Task Scope_and_status_are_stored_as_their_stable_names_in_the_database()
    {
        // The scope and status columns persist the stable NAME, not the numeric value
        // (HasConversion<string>). Read the raw columns back to assert the stored text.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedJobAsync(organization.Id, workspace.Id, user.Id, ExportScope.UserData);

        await using (var context = CreateContext())
        {
            var repository = new ExportJobRepository(context);
            var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded.Start(_startedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using var assertionContext = CreateContext();
        var storedScopes = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT scope AS Value FROM export_jobs")
            .ToListAsync(CancellationToken.None);
        var storedStatuses = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT status AS Value FROM export_jobs")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(ExportScope.UserData), Assert.Single(storedScopes));
        Assert.Equal(nameof(ExportJobStatus.Running), Assert.Single(storedStatuses));
    }

    [Fact]
    public async Task FindById_for_a_missing_job_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_returns_only_the_workspace_jobs_in_id_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);

        var first = await SeedJobAsync(organization.Id, workspace1.Id, user.Id);
        var second = await SeedJobAsync(organization.Id, workspace1.Id, user.Id, ExportScope.UserData);
        // A job in another workspace must never leak into W1's list.
        await SeedJobAsync(organization.Id, workspace2.Id, user.Id);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        var listed = await repository.ListByWorkspaceAsync(organization.Id, workspace1.Id, CancellationToken.None);

        // Exactly W1's two jobs, returned in the repository's deterministic ascending-id order. The two ids
        // are compared as a sorted sequence rather than against insertion order, because UUIDv7 is only
        // coarsely time-ordered (not strictly monotonic within a millisecond).
        var expectedIdsInOrder = new[] { first.Id, second.Id }.OrderBy(id => id).ToList();
        Assert.Equal(expectedIdsInOrder, listed.Select(job => job.Id).ToList());
    }

    [Fact]
    public async Task ListByWorkspace_returns_empty_for_a_workspace_with_no_jobs()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        var listed = await repository.ListByWorkspaceAsync(organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(listed);
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a job exists in workspace W1 only. Looking it up by
        // id under workspace W2 must return null even though both the job and W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedJobAsync(organization.Id, workspace1.Id, user.Id);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        var foundInW1 = await repository.FindByIdAsync(organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_job_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md: organization
        // boundary checked before workspace boundary): a job exists in a workspace owned by organization A.
        // Looking the SAME workspace id and job id up under organization B's id must return null even though
        // the workspace id and job id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedJobAsync(organizationA.Id, workspaceInA.Id, user.Id);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        var underA = await repository.FindByIdAsync(organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_tenants_jobs()
    {
        // A list scoped to organization A's workspace never borrows organization B's jobs, even when the
        // requested workspace id belongs to B (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedJobAsync(organizationA.Id, workspaceInA.Id, user.Id);
        await SeedJobAsync(organizationB.Id, workspaceInB.Id, user.Id);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        // Organization A's read returns only A's job; addressing B's workspace under A's tenant returns none.
        var aJobs = await repository.ListByWorkspaceAsync(organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var crossTenant = await repository.ListByWorkspaceAsync(organizationA.Id, workspaceInB.Id, CancellationToken.None);

        Assert.Equal(inA.Id, Assert.Single(aJobs).Id);
        Assert.Empty(crossTenant);
    }

    [Fact]
    public async Task A_job_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a job for a non-existent
        // workspace is rejected, so a dangling job can never exist outside a real workspace boundary
        // (threat T5). The organization and user are real to isolate the workspace FK.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = ExportJob.Create(organization.Id, Guid.CreateVersion7(), user.Id, ExportScope.Workspace, _createdAt);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a job whose tenant does not exist is rejected even
        // when the workspace exists, so the row can never carry a tenant boundary that is not a real
        // organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = ExportJob.Create(Guid.CreateVersion7(), workspace.Id, user.Id, ExportScope.Workspace, _createdAt);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_cannot_reference_a_requesting_user_that_does_not_exist()
    {
        // The requested_by foreign key is enforced: a job whose requester is not a real user is rejected,
        // so the recorded requester can never be a dangling reference.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = ExportJob.Create(organization.Id, workspace.Id, Guid.CreateVersion7(), ExportScope.Workspace, _createdAt);

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_the_requesting_user_anonymizes_the_job_rather_than_deleting_it()
    {
        // The requested_by foreign key sets null on delete: removing the requesting user must NOT delete the
        // job (the audit trail of who requested an export survives). The job survives with a null requester
        // (anonymized), and its log-safe rendering reflects that.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedUser = await deleteContext.UserProfiles.SingleAsync(profile => profile.Id == user.Id);
            deleteContext.UserProfiles.Remove(storedUser);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var repository = new ExportJobRepository(context);
        var loaded = await repository.FindByIdAsync(organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.RequestedByUserProfileId);
        Assert.Contains("requestedBy=anonymized", loaded.ToString());
    }

    [Fact]
    public async Task Deleting_the_workspace_cascades_to_its_jobs()
    {
        // The workspace_id foreign key cascades on delete: an export job has no meaning without its
        // workspace, so removing the workspace removes its jobs.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedJobAsync(organization.Id, workspace.Id, user.Id);

        await using (var deleteContext = CreateContext())
        {
            var storedWorkspace = await deleteContext.Workspaces.SingleAsync(ws => ws.Id == workspace.Id);
            deleteContext.Workspaces.Remove(storedWorkspace);
            await deleteContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = CreateContext();
        var remaining = await context.ExportJobs.CountAsync(CancellationToken.None);
        Assert.Equal(0, remaining);
    }
}
