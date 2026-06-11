using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Participants;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="ParticipantRepository"/> (CORE-SES-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL
/// translation, the foreign keys into
/// <c>organizations</c>/<c>workspaces</c>/<c>users</c> and the FILTERED unique
/// (<c>workspace_id</c>, <c>user_id</c>) index are exercised on every test run
/// without any database server or Docker. The behaviors under test (scoped
/// equality lookups, filtered-unique index enforcement, full isolation between
/// workspaces and between tenants, optional user link, the soft-delete lifecycle
/// round-trip) are relational semantics shared with PostgreSQL; provider-specific
/// verification happens against PostgreSQL in the deployment pipeline
/// (livecore-deploy) and the isolation test story.
///
/// The epic acceptance for Sessions and Participants is that sessions and
/// participants are generic, with no vertical-specific actor terms, and with
/// required "Lifecycle tests and authorization tests". At the aggregate +
/// repository level
/// (no HTTP endpoints yet — the participant-visible feed is CORE-SES-005, the
/// session-join flow is CORE-SES-003):
/// <list type="bullet">
///   <item>LIFECYCLE: a participant round-trips through the database and its
///   Active -> Removed soft-delete transition persists.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
///   authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary
///   checked before workspace boundary): a participant in workspace W1 is never
///   returned via workspace W2; a participant in organization A is never resolved
///   via organization B; a duplicate user-linked participant is rejected leaving
///   the existing row unchanged; the same OIDC subject under two issuers is two
///   users and therefore two participants; and the foreign keys reject a
///   participant referencing a non-existent workspace or organization.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class ParticipantRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _otherSubject = "11111111-2222-3333-4444-555555555555";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ParticipantRepositoryTests()
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
        // SQLite does not enforce foreign keys unless asked; turn enforcement on
        // so the FK constraints in the model are genuinely exercised.
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

    private async Task<Participant> SeedParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid? userProfileId,
        string displayName)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId, displayName, _createdAt);
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        Assert.Equal(
            ParticipantAddResult.Added,
            await repository.AddAsync(participant, CancellationToken.None));
        return participant;
    }

    [Fact]
    public async Task User_linked_participant_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "Stage Left");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(user.Id, loaded.UserProfileId);
        Assert.Equal("Stage Left", loaded.DisplayName);
        Assert.Equal(ParticipantStatus.Active, loaded.Status);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Anonymous_participant_round_trips_with_a_null_user_link()
    {
        // docs/03_DOMAIN_LANGUAGE.md: a participant may be anonymous (no user
        // account). The null user link must persist and re-load as null.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedParticipantAsync(organization.Id, workspace.Id, userProfileId: null, "Guest 1");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Null(loaded.UserProfileId);
        Assert.False(loaded.IsLinkedToUser);
    }

    [Fact]
    public async Task Many_anonymous_participants_can_coexist_in_one_workspace()
    {
        // The filtered unique (workspace_id, user_id) index excludes null user
        // links, so a workspace may hold any number of anonymous participants
        // without colliding (threat T5: isolation is per row, not a false
        // uniqueness collision on null).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var first = await SeedParticipantAsync(organization.Id, workspace.Id, userProfileId: null, "Guest 1");
        var second = await SeedParticipantAsync(organization.Id, workspace.Id, userProfileId: null, "Guest 2");

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_removal_persists_through_update()
    {
        // Lifecycle test (required-tests column): an active participant is soft
        // removed and the Removed status round-trips through the database.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "Name");

        await using (var context = CreateContext())
        {
            var repository = new ParticipantRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(ParticipantStatus.Active, loaded.Status);

            loaded.Remove(_updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new ParticipantRepository(context);
            var reloaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal(ParticipantStatus.Removed, reloaded.Status);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
        }
    }

    [Fact]
    public async Task FindByUser_returns_the_user_linked_participant()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "Name");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        var loaded = await repository.FindByUserAsync(
            organization.Id, workspace.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(user.Id, loaded.UserProfileId);
    }

    [Fact]
    public async Task FindById_for_a_missing_participant_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

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
    public async Task FindByUser_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByUserAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByUserAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByUserAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_participant_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a participant exists in
        // workspace W1 only. Looking it up by id under workspace W2 must return
        // null even though both the participant and W2 exist; and the user-linked
        // lookup under W2 must also return null. A cross-workspace lookup
        // returns null/deny.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedParticipantAsync(organization.Id, workspace1.Id, user.Id, "Name");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2ById = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);
        var viaW2ByUser = await repository.FindByUserAsync(
            organization.Id, workspace2.Id, user.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2ById);
        Assert.Null(viaW2ByUser);
    }

    [Fact]
    public async Task A_participant_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary): a participant exists in a workspace owned by
        // organization A. Looking the SAME workspace id and participant id (and
        // the same user) up under organization B's id must return null/deny even
        // though the workspace id, participant id and user id are correct: the
        // wrong tenant denies access.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedParticipantAsync(organizationA.Id, workspaceInA.Id, user.Id, "Name");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underBById = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underBByUser = await repository.FindByUserAsync(
            organizationB.Id, workspaceInA.Id, user.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underBById);
        Assert.Null(underBByUser);
    }

    [Fact]
    public async Task Duplicate_user_participant_is_rejected_and_the_existing_row_stays_unchanged()
    {
        // Mandatory uniqueness test: the filtered unique (workspace_id, user_id)
        // index is the database-level guarantee that a second writer can never
        // create a duplicate participant for one user in one workspace
        // (threat T5). The duplicate's different display name must not overwrite
        // the existing record.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var existing = await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "Original");
        var duplicate = Participant.Create(organization.Id, workspace.Id, user.Id, "Impostor", _updatedAt);

        await using (var context = CreateContext())
        {
            var repository = new ParticipantRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(ParticipantAddResult.DuplicateUserParticipant, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new ParticipantRepository(context);
            var loaded = await repository.FindByUserAsync(
                organization.Id, workspace.Id, user.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            // The original row (its id and its display name) is untouched.
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal("Original", loaded.DisplayName);
        }
    }

    [Fact]
    public async Task The_same_user_can_be_a_participant_in_two_workspaces_independently()
    {
        // Object-level isolation works in both directions: one user may have a
        // participant in each of two workspaces, and each participant is scoped to
        // and resolved only within its own workspace.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedParticipantAsync(organization.Id, workspace1.Id, user.Id, "In W1");
        var inW2 = await SeedParticipantAsync(organization.Id, workspace2.Id, user.Id, "In W2");

        Assert.NotEqual(inW1.Id, inW2.Id);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        var loadedW1 = await repository.FindByUserAsync(
            organization.Id, workspace1.Id, user.Id, CancellationToken.None);
        var loadedW2 = await repository.FindByUserAsync(
            organization.Id, workspace2.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loadedW1);
        Assert.NotNull(loadedW2);
        Assert.Equal("In W1", loadedW1.DisplayName);
        Assert.Equal("In W2", loadedW2.DisplayName);
    }

    [Fact]
    public async Task Same_oidc_subject_under_two_issuers_are_distinct_participants()
    {
        // The user is identified by the user profile surrogate id, which is keyed
        // by the OIDC identity pair (issuer, subject). The same OIDC subject value
        // under two issuers is two distinct users and therefore two distinct
        // participants, even within one workspace (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var foreignIssuerUser = await SeedUserAsync(_foreignIssuer, _subject);

        Assert.NotEqual(user.Id, foreignIssuerUser.Id);

        await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "Home Issuer");
        await SeedParticipantAsync(organization.Id, workspace.Id, foreignIssuerUser.Id, "Foreign Issuer");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        var first = await repository.FindByUserAsync(
            organization.Id, workspace.Id, user.Id, CancellationToken.None);
        var second = await repository.FindByUserAsync(
            organization.Id, workspace.Id, foreignIssuerUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Home Issuer", first.DisplayName);
        Assert.Equal("Foreign Issuer", second.DisplayName);
    }

    [Fact]
    public async Task Two_users_can_be_distinct_participants_in_one_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var otherUser = await SeedUserAsync(_issuer, _otherSubject);
        await SeedParticipantAsync(organization.Id, workspace.Id, user.Id, "First");
        await SeedParticipantAsync(organization.Id, workspace.Id, otherUser.Id, "Second");

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        var first = await repository.FindByUserAsync(
            organization.Id, workspace.Id, user.Id, CancellationToken.None);
        var second = await repository.FindByUserAsync(
            organization.Id, workspace.Id, otherUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_participant_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a
        // participant for a non-existent workspace is rejected, so a dangling
        // participant can never exist outside a real workspace boundary
        // (threat T5). The organization and user are real to isolate the
        // workspace FK as the failing constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = Participant.Create(
            organization.Id, Guid.CreateVersion7(), user.Id, "Name", _createdAt);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_participant_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a participant whose tenant
        // does not exist is rejected even when the workspace and user exist, so
        // the row can never carry a tenant boundary that is not a real
        // organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = Participant.Create(
            Guid.CreateVersion7(), workspace.Id, user.Id, "Name", _createdAt);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_user_linked_participant_cannot_reference_a_user_that_does_not_exist()
    {
        // The optional user_id foreign key is enforced when present: a participant
        // linked to a non-existent user is rejected, so a user-linked participant
        // can never name a user that is not a real profile (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Participant.Create(
            organization.Id, workspace.Id, Guid.CreateVersion7(), "Name", _createdAt);

        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }
}
