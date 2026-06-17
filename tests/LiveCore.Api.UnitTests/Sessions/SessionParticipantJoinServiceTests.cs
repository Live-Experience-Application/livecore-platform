// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json;
using LiveCore.Api.Entitlements;
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
/// Tests for <see cref="SessionParticipantJoinService"/> (CORE-SES-003), the
/// fail-closed decision of whether a participant may join a session. They mirror
/// the precedent of <c>TenantContextResolver</c>: the service is a plain service
/// over repositories that returns an admission or a typed denial.
///
/// Unlike the resolver tests (which fake the repositories), these drive the real
/// EF Core repositories against an in-memory SQLite database with foreign keys
/// enforced (<c>PRAGMA foreign_keys = ON</c>), exactly like
/// <c>SessionRepositoryTests</c> and <c>ParticipantRepositoryTests</c>. This means
/// the lifecycle gates run against genuinely persisted state: a session is driven
/// to Live/Ended via <see cref="Session.Start"/>/<see cref="Session.End"/> and a
/// participant to Removed via <see cref="Participant.Remove"/>, then persisted, so
/// the gates see real stored statuses; and the tenant/workspace isolation denials
/// run through the same scoped repository lookups the production code uses.
///
/// The story acceptance is that sessions and participants are generic (no
/// vertical-specific actor terms) with required "Lifecycle tests and authorization
/// tests":
/// <list type="bullet">
///   <item>LIFECYCLE: an active participant joins a Prepared or Live session
///   (admission); an ended session denies with SessionNotJoinable; a removed
///   participant denies with ParticipantNotActive.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization;
///   docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before workspace
///   boundary): a session that exists only in organization B (or workspace W2) is
///   SessionNotFound under organization A (or W1); the same for participants; a
///   cross-tenant join (participant of tenant A, session of tenant B) is denied in
///   BOTH directions and never admitted.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class SessionParticipantJoinServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _endedAt = new(2026, 6, 11, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _removedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _joinedAt = new(2026, 6, 11, 9, 15, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(_joinedAt);

    public SessionParticipantJoinServiceTests()
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

    /// <summary>
    /// Builds a service over fresh repositories on their own context, so each call
    /// reads genuinely persisted state (the same pattern the repository tests use). A
    /// recording publisher captures the ParticipantJoined event emitted on admission
    /// (CORE-EVT-002); pass one in to inspect it, or let the default absorb it for the
    /// tests that only assert the decision.
    /// </summary>
    private SessionParticipantJoinService CreateService(
        LiveCoreDbContext context,
        ISessionEventPublisher? eventPublisher = null)
        => new(
            new SessionRepository(context),
            new ParticipantRepository(context),
            eventPublisher ?? new RecordingSessionEventPublisher(),
            CreateQuotaEnforcement(context),
            _timeProvider);

    /// <summary>
    /// Builds the real <see cref="QuotaEnforcementService"/> over fresh repositories on the given context, so the
    /// participant-cap gate (CORE-MON-005) runs end-to-end through SQL exactly as in production. With no
    /// <c>session.participant.max</c> quota seeded the join is ungoverned and proceeds, so the lifecycle/isolation
    /// tests above need no quota fixture.
    /// </summary>
    private QuotaEnforcementService CreateQuotaEnforcement(LiveCoreDbContext context)
        => new(
            new QuotaDefinitionRepository(context),
            new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
            new QuotaUsageRepository(context),
            _timeProvider);

    /// <summary>
    /// Seeds the <c>session.participant.max</c> quota for the given session subject and grants it a limit (or an
    /// unlimited/fair-use grant for a "paid plan"). Optionally records a starting usage so a test can drive the
    /// session straight to its cap. Mirrors the QuotaEnforcementService test fixtures, but keyed on the
    /// <see cref="EntitlementSubjectType.Session"/> subject this story introduces.
    /// </summary>
    private async Task SeedParticipantQuotaAsync(Guid sessionId, long? limit, long startingUsage = 0)
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(
            QuotaEntitlementKeys.SessionParticipantMax, EntitlementValueKind.Quota, "Quota", null, _createdAt);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.Session, QuotaUnit.Count, _createdAt);
        context.QuotaDefinitions.Add(quota);
        context.SubjectEntitlements.Add(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.Session, sessionId, entitlement, limit, null, _createdAt));

        if (startingUsage != 0)
        {
            var usage = QuotaUsage.Start(EntitlementSubjectType.Session, sessionId, quota, _createdAt);
            usage.Record(startingUsage, _createdAt);
            context.QuotaUsage.Add(usage);
        }

        await context.SaveChangesAsync();
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

    private async Task<Participant> SeedParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        string displayName)
    {
        // Anonymous participants need no user profile, which keeps the isolation
        // tests focused on the session/participant boundary rather than user FKs.
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, displayName, _createdAt);
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        Assert.Equal(ParticipantAddResult.Added, await repository.AddAsync(participant, CancellationToken.None));
        return participant;
    }

    /// <summary>Drives a persisted prepared session to Live and saves it.</summary>
    private async Task StartSessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId)
    {
        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        var session = await repository.FindByIdAsync(organizationId, workspaceId, sessionId, CancellationToken.None);
        Assert.NotNull(session);
        session.Start(_startedAt);
        await repository.UpdateAsync(session, CancellationToken.None);
    }

    /// <summary>Drives a persisted session to Ended (via Live) and saves it.</summary>
    private async Task EndSessionAsync(Guid organizationId, Guid workspaceId, Guid sessionId)
    {
        await using var context = CreateContext();
        var repository = new SessionRepository(context);
        var session = await repository.FindByIdAsync(organizationId, workspaceId, sessionId, CancellationToken.None);
        Assert.NotNull(session);
        session.Start(_startedAt);
        session.End(_endedAt);
        await repository.UpdateAsync(session, CancellationToken.None);
    }

    /// <summary>Drives a persisted active participant to Removed and saves it.</summary>
    private async Task RemoveParticipantAsync(Guid organizationId, Guid workspaceId, Guid participantId)
    {
        await using var context = CreateContext();
        var repository = new ParticipantRepository(context);
        var participant = await repository.FindByIdAsync(
            organizationId, workspaceId, participantId, CancellationToken.None);
        Assert.NotNull(participant);
        participant.Remove(_removedAt);
        await repository.UpdateAsync(participant, CancellationToken.None);
    }

    private static void AssertAdmitted(
        SessionJoinResult result,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
    {
        Assert.True(result.Admitted);
        Assert.Equal(SessionJoinDenialReason.None, result.Reason);
        Assert.Equal(organizationId, result.OrganizationId);
        Assert.Equal(workspaceId, result.WorkspaceId);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(participantId, result.ParticipantId);
    }

    private static void AssertDenied(SessionJoinResult result, SessionJoinDenialReason reason)
    {
        Assert.False(result.Admitted);
        Assert.Equal(reason, result.Reason);
        // A denial never leaks admitted identifiers.
        Assert.Equal(Guid.Empty, result.OrganizationId);
        Assert.Equal(Guid.Empty, result.WorkspaceId);
        Assert.Equal(Guid.Empty, result.SessionId);
        Assert.Equal(Guid.Empty, result.ParticipantId);
    }

    // ---- HAPPY PATH (lifecycle) -------------------------------------------------

    [Fact]
    public async Task Active_participant_joins_a_prepared_session()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Opening Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Stage Left");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertAdmitted(result, organization.Id, workspace.Id, session.Id, participant.Id);
        // An admission emits exactly one ParticipantJoined event carrying the participant IDENTIFIER only
        // (no display name / PII, threat T7).
        AssertParticipantJoinedEmitted(publisher, organization.Id, workspace.Id, session.Id, participant);
    }

    [Fact]
    public async Task Active_participant_joins_a_live_session()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Stage Right");
        // Drive the persisted session to Live so the joinable gate runs against real
        // stored state.
        await StartSessionAsync(organization.Id, workspace.Id, session.Id);

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertAdmitted(result, organization.Id, workspace.Id, session.Id, participant.Id);
    }

    // ---- LIFECYCLE NEGATIVES ----------------------------------------------------

    [Fact]
    public async Task Ended_session_denies_with_SessionNotJoinable()
    {
        // Lifecycle negative: the session is driven through Live to Ended and
        // persisted, so the joinable gate sees the real terminal status. An ended
        // session admits no one.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");
        await EndSessionAsync(organization.Id, workspace.Id, session.Id);

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.SessionNotJoinable);
    }

    [Fact]
    public async Task Removed_participant_denies_with_ParticipantNotActive()
    {
        // Lifecycle negative: the participant is soft-removed and persisted, so the
        // standing gate sees the real Removed status. A removed participant holds no
        // standing to join even a live session.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");
        await StartSessionAsync(organization.Id, workspace.Id, session.Id);
        await RemoveParticipantAsync(organization.Id, workspace.Id, participant.Id);

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.ParticipantNotActive);
        // A denial emits NO event: a join that is not admitted is never recorded or delivered (fail-closed).
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Session_existence_is_checked_before_the_participant_status_gate()
    {
        // Ordering: a removed participant against a MISSING session denies as
        // SessionNotFound, not ParticipantNotActive — the session check runs first
        // and hides session existence before any participant state is consulted
        // (threat T1/T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");
        await RemoveParticipantAsync(organization.Id, workspace.Id, participant.Id);

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.SessionNotFound);
    }

    // ---- NOT-FOUND (existence hiding) -------------------------------------------

    [Fact]
    public async Task Missing_session_denies_with_SessionNotFound()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.SessionNotFound);
    }

    [Fact]
    public async Task Missing_participant_denies_with_ParticipantNotFound()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, Guid.CreateVersion7(), CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.ParticipantNotFound);
    }

    // ---- AUTHORIZATION / TENANT + WORKSPACE ISOLATION (the core of this story) ----

    [Fact]
    public async Task A_session_in_organization_B_is_SessionNotFound_under_organization_A()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary). The session and participant both live in organization
        // B; requesting the join under organization A's id (with the correct
        // workspace/session/participant ids) must hide the session as not-found, the
        // first scoped lookup that fails.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organizationB.Id, workspaceInB.Id, "Run");
        var participant = await SeedParticipantAsync(organizationB.Id, workspaceInB.Id, "Name");

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);

        // Sanity: under B's own tenant the join is admitted and emits exactly one ParticipantJoined.
        var underB = await service.JoinAsync(
            organizationB.Id, workspaceInB.Id, session.Id, participant.Id, CancellationToken.None);
        AssertAdmitted(underB, organizationB.Id, workspaceInB.Id, session.Id, participant.Id);
        Assert.Single(publisher.Published);

        // Under A's tenant it is hidden as not-found — and emits NOTHING: a cross-tenant join can never be
        // recorded or delivered (fail-closed; threat T5). The published count stays at the one B emitted.
        var underA = await service.JoinAsync(
            organizationA.Id, workspaceInB.Id, session.Id, participant.Id, CancellationToken.None);
        AssertDenied(underA, SessionJoinDenialReason.SessionNotFound);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task A_session_in_workspace_W2_is_SessionNotFound_under_workspace_W1()
    {
        // Mandatory negative workspace test (threat T5): the session and participant
        // both live in workspace W2; requesting the join under workspace W1's id must
        // hide the session as not-found even though both belong to the same tenant.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var session = await SeedSessionAsync(organization.Id, workspace2.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace2.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);

        var underW2 = await service.JoinAsync(
            organization.Id, workspace2.Id, session.Id, participant.Id, CancellationToken.None);
        AssertAdmitted(underW2, organization.Id, workspace2.Id, session.Id, participant.Id);

        var underW1 = await service.JoinAsync(
            organization.Id, workspace1.Id, session.Id, participant.Id, CancellationToken.None);
        AssertDenied(underW1, SessionJoinDenialReason.SessionNotFound);
    }

    [Fact]
    public async Task A_participant_in_workspace_W2_is_ParticipantNotFound_under_workspace_W1()
    {
        // The participant lives in workspace W2 while the SESSION lives in W1.
        // Requesting the join under W1 finds the session but must hide the
        // participant (which is in W2) as not-found: cross-workspace participant
        // access is never leaked (threat T1/T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var sessionInW1 = await SeedSessionAsync(organization.Id, workspace1.Id, "Run");
        var participantInW2 = await SeedParticipantAsync(organization.Id, workspace2.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace1.Id, sessionInW1.Id, participantInW2.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.ParticipantNotFound);
    }

    [Fact]
    public async Task A_participant_in_organization_B_is_ParticipantNotFound_under_organization_A()
    {
        // A session exists in organization A; a participant with a known id exists
        // only in organization B. Requesting the join under organization A finds A's
        // session but must hide B's participant as not-found (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var sessionInA = await SeedSessionAsync(organizationA.Id, workspaceInA.Id, "Run");
        var participantInB = await SeedParticipantAsync(organizationB.Id, workspaceInB.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organizationA.Id, workspaceInA.Id, sessionInA.Id, participantInB.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.ParticipantNotFound);
    }

    [Fact]
    public async Task A_cross_tenant_join_is_denied_in_both_directions_and_never_admitted()
    {
        // Mandatory cross-tenant test (threat T5): a participant of tenant A and a
        // session of tenant B must never combine into an admission, regardless of
        // which tenant id is presented. Whichever organization id is used, the lookup
        // for the resource that belongs to the OTHER tenant fails and the join is
        // denied; no combination admits.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _workspaceSlugB);
        var sessionInB = await SeedSessionAsync(organizationB.Id, workspaceInB.Id, "Run");
        var participantInA = await SeedParticipantAsync(organizationA.Id, workspaceInA.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);

        // Presented under A: A's session lookup (with B's session id, A's workspace)
        // fails first -> SessionNotFound. Participant of A never gets to admit.
        var presentedUnderA = await service.JoinAsync(
            organizationA.Id, workspaceInA.Id, sessionInB.Id, participantInA.Id, CancellationToken.None);
        AssertDenied(presentedUnderA, SessionJoinDenialReason.SessionNotFound);

        // Presented under B: B's session is found, but A's participant under B is
        // not -> ParticipantNotFound.
        var presentedUnderB = await service.JoinAsync(
            organizationB.Id, workspaceInB.Id, sessionInB.Id, participantInA.Id, CancellationToken.None);
        AssertDenied(presentedUnderB, SessionJoinDenialReason.ParticipantNotFound);
    }

    [Fact]
    public async Task A_session_and_participant_from_different_workspaces_are_denied_not_admitted()
    {
        // Belt-and-suspenders mismatch: the session is in W1 and the participant is
        // in W2 of the same tenant. There is no (organization, workspace) pair that
        // contains BOTH, so every presentation is a denial — an admission can never
        // be minted from a session and participant that do not share a workspace.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var sessionInW1 = await SeedSessionAsync(organization.Id, workspace1.Id, "Run");
        var participantInW2 = await SeedParticipantAsync(organization.Id, workspace2.Id, "Name");

        await using var context = CreateContext();
        var service = CreateService(context);

        // Under W1: session found, participant (in W2) hidden -> ParticipantNotFound.
        var underW1 = await service.JoinAsync(
            organization.Id, workspace1.Id, sessionInW1.Id, participantInW2.Id, CancellationToken.None);
        AssertDenied(underW1, SessionJoinDenialReason.ParticipantNotFound);

        // Under W2: session (in W1) hidden -> SessionNotFound.
        var underW2 = await service.JoinAsync(
            organization.Id, workspace2.Id, sessionInW1.Id, participantInW2.Id, CancellationToken.None);
        AssertDenied(underW2, SessionJoinDenialReason.SessionNotFound);
    }

    // ---- PARTICIPANT CAP (session.participant.max, CORE-MON-005) ----------------

    [Fact]
    public async Task Join_at_the_participant_limit_is_rejected_with_QuotaExceeded()
    {
        // The free-tier participant gate: with a limit of one the first active participant is admitted (consuming
        // the only slot) and a second is rejected fail-closed with QuotaExceeded — the server-side cap, not the UI,
        // blocks the join (docs/21: "Free limits cannot be bypassed by clients"). The denial emits NO event.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var first = await SeedParticipantAsync(organization.Id, workspace.Id, "First");
        var second = await SeedParticipantAsync(organization.Id, workspace.Id, "Second");
        await SeedParticipantQuotaAsync(session.Id, limit: 1);

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);

        // The first participant fills the only slot.
        var admitted = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, first.Id, CancellationToken.None);
        AssertAdmitted(admitted, organization.Id, workspace.Id, session.Id, first.Id);
        Assert.Single(publisher.Published);

        // The session is now at its cap: the second participant is rejected, and the rejection emits nothing
        // (the published count stays at the one the admission emitted).
        var rejected = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, second.Id, CancellationToken.None);
        AssertDenied(rejected, SessionJoinDenialReason.QuotaExceeded);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task Join_already_at_the_seeded_limit_is_rejected_without_consuming()
    {
        // The session is seeded straight to its cap (limit 4, usage 4 — the free-tier value). The next join is
        // rejected with QuotaExceeded and the atomic guard records nothing beyond the cap.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Latecomer");
        await SeedParticipantQuotaAsync(session.Id, limit: 4, startingUsage: 4);

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.QuotaExceeded);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task A_paid_plan_allows_more_participants_than_the_free_limit()
    {
        // The same three participants that a free limit of one would cap at one are ALL admitted under an
        // unlimited (fair-use) "paid plan" grant — premium state comes from the server entitlement, and a higher
        // plan raises the cap. Each admission emits its own ParticipantJoined.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var p1 = await SeedParticipantAsync(organization.Id, workspace.Id, "One");
        var p2 = await SeedParticipantAsync(organization.Id, workspace.Id, "Two");
        var p3 = await SeedParticipantAsync(organization.Id, workspace.Id, "Three");
        // limit: null is the unlimited (fair-use) grant of the paid plans in csv/mobile_entitlement_catalog.csv.
        await SeedParticipantQuotaAsync(session.Id, limit: null);

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);

        foreach (var participant in new[] { p1, p2, p3 })
        {
            var result = await service.JoinAsync(
                organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);
            AssertAdmitted(result, organization.Id, workspace.Id, session.Id, participant.Id);
        }

        Assert.Equal(3, publisher.Published.Count);
    }

    [Fact]
    public async Task A_paid_limit_admits_exactly_up_to_its_higher_cap()
    {
        // A "paid" cap of two admits the first two participants and rejects the third with QuotaExceeded: the gate
        // enforces whatever limit the session is granted, so a larger plan simply admits more before the cap.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var p1 = await SeedParticipantAsync(organization.Id, workspace.Id, "One");
        var p2 = await SeedParticipantAsync(organization.Id, workspace.Id, "Two");
        var p3 = await SeedParticipantAsync(organization.Id, workspace.Id, "Three");
        await SeedParticipantQuotaAsync(session.Id, limit: 2);

        await using var context = CreateContext();
        var service = CreateService(context);

        AssertAdmitted(
            await service.JoinAsync(organization.Id, workspace.Id, session.Id, p1.Id, CancellationToken.None),
            organization.Id, workspace.Id, session.Id, p1.Id);
        AssertAdmitted(
            await service.JoinAsync(organization.Id, workspace.Id, session.Id, p2.Id, CancellationToken.None),
            organization.Id, workspace.Id, session.Id, p2.Id);
        AssertDenied(
            await service.JoinAsync(organization.Id, workspace.Id, session.Id, p3.Id, CancellationToken.None),
            SessionJoinDenialReason.QuotaExceeded);
    }

    [Fact]
    public async Task An_ungoverned_session_admits_without_a_participant_quota()
    {
        // No session.participant.max quota is defined for the deployment, so there is nothing to enforce: Core
        // enforces only quotas that EXIST, and the join proceeds unchanged. (This is the default the lifecycle and
        // isolation tests above rely on.)
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Solo");

        await using var context = CreateContext();
        var service = CreateService(context);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertAdmitted(result, organization.Id, workspace.Id, session.Id, participant.Id);
    }

    [Fact]
    public async Task A_defined_participant_quota_the_session_is_not_entitled_to_denies_fail_closed()
    {
        // The quota is DEFINED for the deployment but the session holds NO entitlement granting a limit: fail-closed,
        // the session has no allowance, so the join is denied with QuotaExceeded and emits nothing. This is the
        // server-side default that stops a client unlocking a cap it was never granted.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var session = await SeedSessionAsync(organization.Id, workspace.Id, "Run");
        var participant = await SeedParticipantAsync(organization.Id, workspace.Id, "Name");
        await SeedParticipantQuotaDefinitionWithoutGrantAsync();

        var publisher = new RecordingSessionEventPublisher();
        await using var context = CreateContext();
        var service = CreateService(context, publisher);
        var result = await service.JoinAsync(
            organization.Id, workspace.Id, session.Id, participant.Id, CancellationToken.None);

        AssertDenied(result, SessionJoinDenialReason.QuotaExceeded);
        Assert.Empty(publisher.Published);
    }

    /// <summary>
    /// Seeds only the <c>session.participant.max</c> quota DEFINITION (no subject grant), so the quota governs the
    /// deployment but no session is entitled to a limit — the fail-closed "defined but not granted" case.
    /// </summary>
    private async Task SeedParticipantQuotaDefinitionWithoutGrantAsync()
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(
            QuotaEntitlementKeys.SessionParticipantMax, EntitlementValueKind.Quota, "Quota", null, _createdAt);
        context.EntitlementDefinitions.Add(entitlement);
        context.QuotaDefinitions.Add(
            QuotaDefinition.Define(entitlement, EntitlementSubjectType.Session, QuotaUnit.Count, _createdAt));
        await context.SaveChangesAsync();
    }

    // ---- GUARDS -----------------------------------------------------------------

    [Fact]
    public async Task JoinAsync_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.JoinAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.JoinAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.JoinAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.JoinAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var sessions = new SessionRepository(CreateContext());
        var participants = new ParticipantRepository(CreateContext());
        var publisher = new RecordingSessionEventPublisher();
        var quota = CreateQuotaEnforcement(CreateContext());

        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantJoinService(null!, participants, publisher, quota, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantJoinService(sessions, null!, publisher, quota, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantJoinService(sessions, participants, null!, quota, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantJoinService(sessions, participants, publisher, null!, _timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SessionParticipantJoinService(sessions, participants, publisher, quota, null!));
    }

    // ---- EMISSION HELPERS + TEST DOUBLES ----------------------------------------

    /// <summary>
    /// Asserts exactly one ParticipantJoined session event was emitted for the admitted join, carrying the
    /// participant IDENTIFIER only (audience-wide, subjectless, no display name / PII — threat T7).
    /// </summary>
    private static void AssertParticipantJoinedEmitted(
        RecordingSessionEventPublisher publisher,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Participant participant)
    {
        var sessionEvent = Assert.Single(publisher.Published);
        Assert.Equal(SessionEventTypes.ParticipantJoined, sessionEvent.EventType);
        Assert.Equal(organizationId, sessionEvent.OrganizationId);
        Assert.Equal(workspaceId, sessionEvent.WorkspaceId);
        Assert.Equal(sessionId, sessionEvent.SessionId);
        // Audience-wide (no selected participant) and subjectless (no visibility subject): delivered to the
        // whole session audience unconditionally, like SessionStarted/Ended.
        Assert.Null(sessionEvent.TargetParticipantId);
        Assert.False(sessionEvent.HasVisibilitySubject);
        // The actor is the joining participant's linked user (null/system for an anonymous participant).
        Assert.Equal(participant.UserProfileId, sessionEvent.CreatedBy);
        // Identifier-only payload: the participant id is present; the display name is NOT (threat T7).
        using var document = JsonDocument.Parse(sessionEvent.Payload);
        Assert.Equal(participant.Id, document.RootElement.GetProperty("ParticipantId").GetGuid());
        Assert.DoesNotContain(participant.DisplayName, sessionEvent.Payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A recording <see cref="ISessionEventPublisher"/> that captures the events a flow publishes without
    /// touching the database, so a unit test can assert exactly what was (or was not) emitted.
    /// </summary>
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

        // The join flow emits through the combined PublishAsync; the split append/deliver halves
        // (CORE-CONC-002) are not exercised on this path, so they fail fast if ever reached.
        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeliverAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A <see cref="TimeProvider"/> that always returns a fixed instant, so emitted timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
