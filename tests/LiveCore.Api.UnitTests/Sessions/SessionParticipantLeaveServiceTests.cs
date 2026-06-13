using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Sessions;

/// <summary>
/// Tests for <see cref="SessionParticipantLeaveService"/> (CORE-EVT-002), the symmetric leave to the join
/// service: it soft-removes a participant from a session and, on (and only on) an actual departure, emits the
/// durable <c>ParticipantLeft</c> session event. Like <c>SessionParticipantJoinServiceTests</c> they drive the
/// real EF Core repositories against an in-memory SQLite database with foreign keys enforced, so the
/// transition runs against genuinely persisted state, and a recording publisher captures exactly what was (or
/// was not) emitted.
///
/// Coverage (the story's "Integration: join/leave persist one event each ... no PII leak" at the unit level,
/// plus the mandatory negative authorization tests — AGENTS.md; docs/17_DEFINITION_OF_DONE.md):
/// <list type="bullet">
///   <item>DEPARTURE: an active participant leaving a session is soft-removed (status Removed) and emits
///   exactly ONE ParticipantLeft event carrying the participant IDENTIFIER only (no display name / PII,
///   threat T7), audience-wide and subjectless, System-emitted (no actor).</item>
///   <item>IDEMPOTENCE: leaving when already removed is a no-op that changes nothing and emits nothing — so a
///   repeat never appends a second event.</item>
///   <item>OBJECT-LEVEL ISOLATION (threat T1/T5): a session or participant that lives only in organization B
///   (or workspace W2) is hidden as not-found under organization A (or W1) and emits NOTHING; a cross-tenant /
///   cross-workspace leave is denied in both directions and never removes or emits.</item>
/// </list>
/// </summary>
public sealed class SessionParticipantLeaveServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _leftAt = new(2026, 6, 11, 9, 45, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(_leftAt);

    public SessionParticipantLeaveServiceTests()
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

    private SessionParticipantLeaveService CreateService(
        LiveCoreDbContext context,
        ISessionEventPublisher? eventPublisher = null)
        => new(
            new SessionRepository(context),
            new ParticipantRepository(context),
            eventPublisher ?? new RecordingSessionEventPublisher(),
            _timeProvider);

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

    private async Task<Session> SeedLiveSessionAsync(Guid organizationId, Guid workspaceId, string title)
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        session.Start(_startedAt);
        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        Assert.Equal(SessionAddResult.Added, await repository.AddAsync(session, CancellationToken.None));
        return session;
    }

    private async Task<Participant> SeedParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        string displayName,
        Guid? userProfileId = null)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId, displayName, _createdAt);
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        Assert.Equal(ParticipantAddResult.Added, await repository.AddAsync(participant, CancellationToken.None));
        return participant;
    }

    /// <summary>Re-reads the persisted participant so a test can assert its stored status after a leave.</summary>
    private async Task<Participant?> ReadParticipantAsync(Guid organizationId, Guid workspaceId, Guid participantId)
    {
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        return await repository.FindByIdAsync(organizationId, workspaceId, participantId, CancellationToken.None);
    }

    // ---- DEPARTURE (happy path) -------------------------------------------------

    [Fact]
    public async Task Active_participant_leaving_is_removed_and_emits_one_ParticipantLeft()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedLiveSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Stage Left");

        var publisher = new RecordingSessionEventPublisher();
        await using (var context = CreateContext())
        {
            var service = CreateService(context, publisher);
            var result = await service.LeaveAsync(
                organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

            Assert.Equal(ParticipantLeaveOutcome.Left, result.Outcome);
            Assert.True(result.Left);
            Assert.Equal(organization.Id, result.OrganizationId);
            Assert.Equal(workspace.Id, result.WorkspaceId);
            Assert.Equal(session.Id, result.SessionId);
            Assert.Equal(participant.Id, result.ParticipantId);
        }

        // The participant is soft-removed (the leave performed Participant.Remove and persisted it).
        var stored = await ReadParticipantAsync(organization.Id, workspace.Id, participant.Id);
        Assert.NotNull(stored);
        Assert.Equal(ParticipantStatus.Removed, stored.Status);

        // Exactly one ParticipantLeft event, identifier-only, audience-wide, subjectless, System-emitted.
        AssertParticipantLeftEmitted(publisher, organization.Id, workspace.Id, session.Id, participant);
    }

    [Fact]
    public async Task Leaving_when_already_removed_is_an_idempotent_no_op_that_emits_nothing()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedLiveSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Stage Left");

        var publisher = new RecordingSessionEventPublisher();

        // First leave: a real departure (one event).
        await using (var context = CreateContext())
        {
            var service = CreateService(context, publisher);
            var first = await service.LeaveAsync(
                organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);
            Assert.Equal(ParticipantLeaveOutcome.Left, first.Outcome);
        }

        // Second leave of the SAME participant: already removed -> no-op, NO second event.
        await using (var context = CreateContext())
        {
            var service = CreateService(context, publisher);
            var second = await service.LeaveAsync(
                organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);
            Assert.Equal(ParticipantLeaveOutcome.AlreadyLeft, second.Outcome);
            Assert.False(second.Left);
            // The located ids are still carried (the participant was found, just already gone).
            Assert.Equal(participant.Id, second.ParticipantId);
        }

        // Across both calls, exactly ONE ParticipantLeft was ever emitted.
        Assert.Single(publisher.Published);
    }

    // ---- NOT-FOUND (existence hiding) -------------------------------------------

    [Fact]
    public async Task Missing_session_is_SessionNotFound_and_emits_nothing()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.LeaveAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), participant.Id, CancellationToken.None);

        AssertHiddenNotFound(result, ParticipantLeaveOutcome.SessionNotFound, publisher);
        // The participant was never touched.
        var stored = await ReadParticipantAsync(organization.Id, workspace.Id, participant.Id);
        Assert.NotNull(stored);
        Assert.Equal(ParticipantStatus.Active, stored.Status);
    }

    [Fact]
    public async Task Missing_participant_is_ParticipantNotFound_and_emits_nothing()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedLiveSessionAsync(organization.Id, workspace.Id, "Run");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.LeaveAsync(
            organization.Id, workspace.Id, session.Id, Guid.CreateVersion7(), CancellationToken.None);

        AssertHiddenNotFound(result, ParticipantLeaveOutcome.ParticipantNotFound, publisher);
    }

    // ---- AUTHORIZATION / TENANT + WORKSPACE ISOLATION (mandatory negatives) ------

    [Fact]
    public async Task A_session_in_organization_B_is_SessionNotFound_under_organization_A_and_emits_nothing()
    {
        // Foreign-tenant negative (threat T5; organization boundary checked before workspace boundary). The
        // session and participant both live in organization B; a leave presented under organization A must hide
        // the session as not-found, remove nothing and emit nothing.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugA);
        var session = await SeedLiveSessionAsync(organizationB.Id, workspaceInB.Id, "Run");
        var participant = await SeedParticipantAsync(organizationB.Id, workspaceInB.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);

        var underA = await service.LeaveAsync(
            organizationA.Id, workspaceInB.Id, session.Id, participant.Id, CancellationToken.None);
        AssertHiddenNotFound(underA, ParticipantLeaveOutcome.SessionNotFound, publisher);

        // The participant in B is untouched: cross-tenant leave never removes another tenant's participant.
        var stored = await ReadParticipantAsync(organizationB.Id, workspaceInB.Id, participant.Id);
        Assert.NotNull(stored);
        Assert.Equal(ParticipantStatus.Active, stored.Status);
    }

    [Fact]
    public async Task A_participant_in_organization_B_is_ParticipantNotFound_under_organization_A_and_emits_nothing()
    {
        // A session exists in organization A; a participant with a known id exists only in organization B. A
        // leave presented under organization A finds A's session but must hide B's participant as not-found.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var sessionInA = await SeedLiveSessionAsync(organizationA.Id, workspaceInA.Id, "Run");
        var participantInB = await SeedParticipantAsync(organizationB.Id, workspaceInB.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.LeaveAsync(
            organizationA.Id, workspaceInA.Id, sessionInA.Id, participantInB.Id, CancellationToken.None);

        AssertHiddenNotFound(result, ParticipantLeaveOutcome.ParticipantNotFound, publisher);
    }

    [Fact]
    public async Task A_session_in_workspace_W2_is_SessionNotFound_under_workspace_W1_and_emits_nothing()
    {
        // Foreign-workspace negative within one tenant (threat T5): the session and participant live in W2; a
        // leave presented under W1 must hide the session as not-found and emit nothing.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var session = await SeedLiveSessionAsync(organization.Id, workspace2.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace2.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.LeaveAsync(
            organization.Id, workspace1.Id, session.Id, participant.Id, CancellationToken.None);

        AssertHiddenNotFound(result, ParticipantLeaveOutcome.SessionNotFound, publisher);
    }

    [Fact]
    public async Task A_cross_tenant_leave_is_denied_in_both_directions_and_never_removes_or_emits()
    {
        // Mandatory cross-tenant test (threat T5): a participant of tenant A and a session of tenant B can never
        // combine into a leave, regardless of which tenant id is presented.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var sessionInB = await SeedLiveSessionAsync(organizationB.Id, workspaceInB.Id, "Run");
        var participantInA = await SeedParticipantAsync(organizationA.Id, workspaceInA.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);

        // Presented under A: A's session lookup (B's session id under A) fails first -> SessionNotFound.
        var presentedUnderA = await service.LeaveAsync(
            organizationA.Id, workspaceInA.Id, sessionInB.Id, participantInA.Id, CancellationToken.None);
        AssertHiddenNotFound(presentedUnderA, ParticipantLeaveOutcome.SessionNotFound, publisher);

        // Presented under B: B's session is found, but A's participant under B is not -> ParticipantNotFound.
        var presentedUnderB = await service.LeaveAsync(
            organizationB.Id, workspaceInB.Id, sessionInB.Id, participantInA.Id, CancellationToken.None);
        Assert.Equal(ParticipantLeaveOutcome.ParticipantNotFound, presentedUnderB.Outcome);

        // No combination removed the participant or emitted any event.
        Assert.Empty(publisher.Published);
        var stored = await ReadParticipantAsync(organizationA.Id, workspaceInA.Id, participantInA.Id);
        Assert.NotNull(stored);
        Assert.Equal(ParticipantStatus.Active, stored.Status);
    }

    // ---- GUARDS -----------------------------------------------------------------

    [Fact]
    public async Task LeaveAsync_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LeaveAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LeaveAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LeaveAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LeaveAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var sessions = new SessionRepository(CreateContext());
        var participants = new ParticipantRepository(CreateContext());
        var publisher = new RecordingSessionEventPublisher();

        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantLeaveService(null!, participants, publisher, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantLeaveService(sessions, null!, publisher, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantLeaveService(sessions, participants, null!, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantLeaveService(sessions, participants, publisher, null!));
    }

    // ---- EMISSION HELPERS + TEST DOUBLES ----------------------------------------

    private static void AssertParticipantLeftEmitted(
        RecordingSessionEventPublisher publisher,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Participant participant)
    {
        var sessionEvent = Assert.Single(publisher.Published);
        Assert.Equal(SessionEventTypes.ParticipantLeft, sessionEvent.EventType);
        Assert.Equal(organizationId, sessionEvent.OrganizationId);
        Assert.Equal(workspaceId, sessionEvent.WorkspaceId);
        Assert.Equal(sessionId, sessionEvent.SessionId);
        // Audience-wide and subjectless, like ParticipantJoined / SessionStarted.
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.HasVisibilitySubject);
        // ParticipantLeft is System-emitted: no actor (docs/09_EVENT_CATALOG.md).
        Assert.Null(sessionEvent.CreatedBy);
        // Identifier-only payload: the participant id is present; the display name is NOT (threat T7).
        using var document = JsonDocument.Parse(sessionEvent.Payload);
        Assert.Equal(participant.Id, document.RootElement.GetProperty("ParticipantId").GetGuid());
        Assert.DoesNotContain(participant.DisplayName, sessionEvent.Payload, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertHiddenNotFound(
        SessionParticipantLeaveResult result,
        ParticipantLeaveOutcome expected,
        RecordingSessionEventPublisher publisher)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.False(result.Left);
        // A not-found leaks no identifiers (existence hidden).
        Assert.Equal(Guid.Empty, result.OrganizationId);
        Assert.Equal(Guid.Empty, result.WorkspaceId);
        Assert.Equal(Guid.Empty, result.SessionId);
        Assert.Equal(Guid.Empty, result.ParticipantId);
        // And emits nothing.
        Assert.Empty(publisher.Published);
    }

    /// <summary>A recording <see cref="ISessionEventPublisher"/> that captures published events without touching the database.</summary>
    private sealed class RecordingSessionEventPublisher : ISessionEventPublisher
    {
        private readonly List<SessionEvent> _published = [];

        public IReadOnlyList<SessionEvent> Published => _published;

        public Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sessionEvent);
            _published.Add(sessionEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>A <see cref="TimeProvider"/> that always returns a fixed instant, so emitted timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
