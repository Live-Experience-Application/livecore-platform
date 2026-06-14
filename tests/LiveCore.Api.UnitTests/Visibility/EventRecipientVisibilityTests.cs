using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="EventRecipientVisibility"/> (CORE-RT-004) — the Visibility module's per-recipient
/// event-visibility decision used by the Realtime delivery. It is a thin, fail-closed adapter over the
/// real <see cref="VisibilityPolicy"/>, driven against an in-memory SQLite database with foreign keys
/// enforced, exactly like <see cref="VisibilityPolicyTests"/>, so the parse-then-delegate behavior runs
/// against genuinely persisted rules.
///
/// The two security-critical properties:
/// <list type="bullet">
///   <item>FAIL-CLOSED on the subject kind: an unrecognized, numeric or empty subject can never make a
///   resource visible to any recipient (threats T1/T3).</item>
///   <item>It REUSES the central visibility decision: an audience-wide visible rule reaches the audience
///   and any participant; a participant-scoped reveal reaches ONLY that participant, never another (the
///   crown jewel, threat T3/T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class EventRecipientVisibilityTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    public EventRecipientVisibilityTests()
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

    private EventRecipientVisibility CreateService(LiveCoreDbContext context)
        => new(new VisibilityPolicy(new VisibilityRuleRepository(context)));

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync()
    {
        var organization = Organization.Create("northwind-labs", "northwind-labs", _createdAt);
        var workspace = Workspace.Create(organization.Id, "summer-show", "Summer Show", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return (organization.Id, workspace.Id);
    }

    private async Task<Guid> SeedParticipantAsync(Guid organizationId, Guid workspaceId)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId: null, "Guest", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(ParticipantAddResult.Added, await new ParticipantRepository(context).AddAsync(participant, CancellationToken.None));
        return participant.Id;
    }

    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var session = Session.Create(organizationId, workspaceId, "Live Session", _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        _sessionByWorkspace[workspaceId] = session.Id;
        return session.Id;
    }

    private async Task SeedAudienceRuleAsync(Guid organizationId, Guid workspaceId, Guid resourceId, VisibilityState state)
    {
        var sessionId = await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.Create(organizationId, workspaceId, sessionId, VisibilityResourceType.Entity, resourceId, state, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    private async Task SeedParticipantRuleAsync(Guid organizationId, Guid workspaceId, Guid resourceId, Guid participantId, VisibilityState state)
    {
        var sessionId = await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.CreateForParticipant(
            organizationId, workspaceId, sessionId, VisibilityResourceType.Entity, resourceId, participantId, state, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("999")]
    [InlineData("")]
    [InlineData("entity")] // case-sensitive: the wrong case is not a recognized kind
    public async Task An_unrecognized_subject_kind_is_never_visible(string subjectType)
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // Even with a visible audience-wide rule, a malformed subject kind fails closed.
        await SeedAudienceRuleAsync(org, ws, resourceId, VisibilityState.Visible);
        var participant = await SeedParticipantAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.False(await service.CanAudienceReceiveAsync(org, ws, await SessionIdAsync(org, ws), subjectType, resourceId, CancellationToken.None));
        Assert.False(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), participant, subjectType, resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_subject_id_is_never_visible()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.False(await service.CanAudienceReceiveAsync(org, ws, await SessionIdAsync(org, ws), "Entity", Guid.Empty, CancellationToken.None));
        Assert.False(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), participant, "Entity", Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task An_audience_wide_visible_rule_reaches_the_audience_and_any_participant()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedAudienceRuleAsync(org, ws, resourceId, VisibilityState.Visible);
        var participant = await SeedParticipantAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.True(await service.CanAudienceReceiveAsync(org, ws, await SessionIdAsync(org, ws), "Entity", resourceId, CancellationToken.None));
        Assert.True(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), participant, "Entity", resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_hidden_rule_reaches_no_one()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedAudienceRuleAsync(org, ws, resourceId, VisibilityState.Hidden);
        var participant = await SeedParticipantAsync(org, ws);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.False(await service.CanAudienceReceiveAsync(org, ws, await SessionIdAsync(org, ws), "Entity", resourceId, CancellationToken.None));
        Assert.False(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), participant, "Entity", resourceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_participant_scoped_reveal_reaches_only_the_selected_participant()
    {
        // THE crown jewel at the realtime recipient layer: a reveal scoped to `selected` reaches only
        // them — not the audience-at-large and not another participant.
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var selected = await SeedParticipantAsync(org, ws);
        var other = await SeedParticipantAsync(org, ws);
        await SeedParticipantRuleAsync(org, ws, resourceId, selected, VisibilityState.Visible);

        await using var context = CreateContext();
        var service = CreateService(context);

        Assert.False(await service.CanAudienceReceiveAsync(org, ws, await SessionIdAsync(org, ws), "Entity", resourceId, CancellationToken.None));
        Assert.True(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), selected, "Entity", resourceId, CancellationToken.None));
        Assert.False(await service.CanParticipantReceiveAsync(org, ws, await SessionIdAsync(org, ws), other, "Entity", resourceId, CancellationToken.None));
    }
}
