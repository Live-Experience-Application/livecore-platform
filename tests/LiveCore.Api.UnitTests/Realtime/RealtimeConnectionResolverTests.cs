using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="RealtimeConnectionResolver"/> (CORE-RT-002) — which SERVER-MANAGED groups a hub
/// connection joins, or a fail-closed denial. They drive the REAL EF Core repositories and the real
/// <see cref="TenantContextResolver"/> against an in-memory SQLite database with foreign keys enforced
/// (mirroring <c>SessionParticipantJoinServiceTests</c>), so the tenant/session/membership/participant
/// chain runs against genuinely persisted state.
///
/// The story's required tests are "Realtime integration tests with selected/unselected participants";
/// this suite is the authorization + group-computation core:
/// <list type="bullet">
///   <item>POSITIVE: a host-capable member joins the org/workspace-hosts/session-hosts groups; an
///   Observer joins only the session-observers group; a participant joins ONLY its own
///   session-participant group.</item>
///   <item>SELECTED vs UNSELECTED (the crown jewel, threat T3): a caller can join only the participant
///   group they OWN — a different participant id, a foreign-workspace participant, or a removed
///   participant is denied, so a connection can never subscribe to another participant's feed.</item>
///   <item>FAIL-CLOSED / ISOLATION (threats T1/T5): an unauthenticated principal, a tenant the caller is
///   not entitled to, a session in another tenant, a non-member, and a Participant/Auditor workspace role
///   with no participant record are all denied, each carrying no groups.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionResolverTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public RealtimeConnectionResolverTests()
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

    private RealtimeConnectionResolver CreateResolver(LiveCoreDbContext context)
        => new(
            new TenantContextResolver(
                new OrganizationRepository(context),
                new UserProfileRepository(context),
                new OrganizationMemberRepository(context)),
            new SessionRepository(context),
            new WorkspaceMemberRepository(context),
            new ParticipantRepository(context));

    private static ClaimsPrincipal Principal(string subject, params string[] organizationSlugs)
    {
        var claims = new List<Claim>
        {
            new(OidcClaimTypes.Issuer, _issuer),
            new(OidcClaimTypes.Subject, subject),
        };
        foreach (var slug in organizationSlugs)
        {
            claims.Add(new Claim(OidcClaimTypes.Organization, slug));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _now);
        await using var context = CreateContext();
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        return workspace;
    }

    private async Task<Session> SeedSessionAsync(Guid organizationId, Guid workspaceId)
    {
        var session = Session.Create(organizationId, workspaceId, "Session", _now);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Seeds a user profile and an organization membership (any role — the resolver classifies by the
    /// WORKSPACE role / participant record, never the org role; the org membership only satisfies the
    /// tenant boundary the same way every authenticated caller does).
    /// </summary>
    private async Task<Guid> SeedUserAsync(Guid organizationId, string subject)
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subject), _now);
        var member = OrganizationMember.Create(organizationId, profile.Id, MembershipRole.Participant, _now);
        await using var context = CreateContext();
        context.UserProfiles.Add(profile);
        context.OrganizationMembers.Add(member);
        await context.SaveChangesAsync();
        return profile.Id;
    }

    private async Task SeedWorkspaceMemberAsync(Guid organizationId, Guid workspaceId, Guid userProfileId, MembershipRole role)
    {
        var member = WorkspaceMember.Create(organizationId, workspaceId, userProfileId, role, _now);
        await using var context = CreateContext();
        context.WorkspaceMembers.Add(member);
        await context.SaveChangesAsync();
    }

    private async Task<Participant> SeedParticipantAsync(Guid organizationId, Guid workspaceId, Guid? userProfileId, bool removed = false)
    {
        var participant = Participant.Create(organizationId, workspaceId, userProfileId, "Participant", _now);
        if (removed)
        {
            participant.Remove(_now);
        }

        await using var context = CreateContext();
        context.Participants.Add(participant);
        await context.SaveChangesAsync();
        return participant;
    }

    private async Task<RealtimeConnectionAdmission> ResolveAsync(
        ClaimsPrincipal? user,
        string? organizationSlug,
        Guid sessionId,
        Guid? participantId = null)
    {
        await using var context = CreateContext();
        return await CreateResolver(context)
            .ResolveAsync(user, organizationSlug, sessionId, participantId, CancellationToken.None);
    }

    // --- Host / Observer member paths ------------------------------------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public async Task A_host_capable_member_joins_the_org_workspace_and_session_host_groups(MembershipRole role)
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, $"host-{role}");
        await SeedWorkspaceMemberAsync(org.Id, workspace.Id, user, role);

        var admission = await ResolveAsync(Principal($"host-{role}", _orgSlugA), _orgSlugA, session.Id);

        Assert.True(admission.Admitted);
        Assert.Equal(
            new[]
            {
                RealtimeGroups.Organization(org.Id),
                RealtimeGroups.WorkspaceHosts(workspace.Id),
                RealtimeGroups.SessionHosts(session.Id),
            },
            admission.Groups);
    }

    [Fact]
    public async Task An_observer_member_joins_only_the_session_observers_group()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, "observer");
        await SeedWorkspaceMemberAsync(org.Id, workspace.Id, user, MembershipRole.Observer);

        var admission = await ResolveAsync(Principal("observer", _orgSlugA), _orgSlugA, session.Id);

        Assert.True(admission.Admitted);
        Assert.Equal(new[] { RealtimeGroups.SessionObservers(session.Id) }, admission.Groups);
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Auditor)]
    public async Task A_member_role_with_no_realtime_group_is_denied(MembershipRole role)
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, $"member-{role}");
        await SeedWorkspaceMemberAsync(org.Id, workspace.Id, user, role);

        var admission = await ResolveAsync(Principal($"member-{role}", _orgSlugA), _orgSlugA, session.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    [Fact]
    public async Task A_non_member_is_denied()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        // The user is an org member (tenant resolves) but has no workspace membership and no participant.
        await SeedUserAsync(org.Id, "outsider");

        var admission = await ResolveAsync(Principal("outsider", _orgSlugA), _orgSlugA, session.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    // --- Participant path (selected vs unselected) -----------------------------

    [Fact]
    public async Task A_participant_joins_only_its_own_participant_group()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, "participant");
        var participant = await SeedParticipantAsync(org.Id, workspace.Id, user);

        var admission = await ResolveAsync(Principal("participant", _orgSlugA), _orgSlugA, session.Id, participant.Id);

        Assert.True(admission.Admitted);
        Assert.Equal(new[] { RealtimeGroups.SessionParticipant(session.Id, participant.Id) }, admission.Groups);
    }

    [Fact]
    public async Task A_caller_cannot_join_a_participant_group_they_do_not_own()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        await SeedUserAsync(org.Id, "caller");
        var otherUser = await SeedUserAsync(org.Id, "other");
        // A participant owned by SOMEONE ELSE.
        var othersParticipant = await SeedParticipantAsync(org.Id, workspace.Id, otherUser);

        // The caller supplies another participant's id: denied — they cannot subscribe to it (the crown
        // jewel of the selected/unselected guarantee, threat T3).
        var admission = await ResolveAsync(Principal("caller", _orgSlugA), _orgSlugA, session.Id, othersParticipant.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    [Fact]
    public async Task An_anonymous_participant_cannot_be_claimed()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        await SeedUserAsync(org.Id, "caller");
        // An anonymous participant (no user link) is owned by no one.
        var anonymous = await SeedParticipantAsync(org.Id, workspace.Id, userProfileId: null);

        var admission = await ResolveAsync(Principal("caller", _orgSlugA), _orgSlugA, session.Id, anonymous.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    [Fact]
    public async Task A_removed_participant_is_denied()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, "gone");
        var participant = await SeedParticipantAsync(org.Id, workspace.Id, user, removed: true);

        var admission = await ResolveAsync(Principal("gone", _orgSlugA), _orgSlugA, session.Id, participant.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    [Fact]
    public async Task A_participant_of_another_workspace_is_denied()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var sessionWorkspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var otherWorkspace = await SeedWorkspaceAsync(org.Id, "winter-show");
        var session = await SeedSessionAsync(org.Id, sessionWorkspace.Id);
        var user = await SeedUserAsync(org.Id, "participant");
        // The caller owns a participant, but in a DIFFERENT workspace than the session's.
        var participant = await SeedParticipantAsync(org.Id, otherWorkspace.Id, user);

        var admission = await ResolveAsync(Principal("participant", _orgSlugA), _orgSlugA, session.Id, participant.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    // --- Fail-closed authentication / tenant / session -------------------------

    [Fact]
    public async Task A_null_principal_is_denied_invalid_principal()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);

        AssertDenied(await ResolveAsync(null, _orgSlugA, session.Id), RealtimeConnectionDenialReason.InvalidPrincipal);
    }

    [Fact]
    public async Task A_caller_without_the_org_claim_is_tenant_denied()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        var user = await SeedUserAsync(org.Id, "host");
        await SeedWorkspaceMemberAsync(org.Id, workspace.Id, user, MembershipRole.Host);

        // The principal asserts no organization claim, so the tenant boundary denies before any session
        // lookup (claim AND membership required).
        var admission = await ResolveAsync(Principal("host"), _orgSlugA, session.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.TenantDenied);
    }

    [Fact]
    public async Task A_session_in_another_tenant_is_session_not_found()
    {
        var orgA = await SeedOrganizationAsync(_orgSlugA);
        var orgB = await SeedOrganizationAsync(_orgSlugB);
        var workspaceB = await SeedWorkspaceAsync(orgB.Id, "b-show");
        var sessionB = await SeedSessionAsync(orgB.Id, workspaceB.Id);
        // The caller is a host in org A and addresses org B's session while claiming org A.
        var user = await SeedUserAsync(orgA.Id, "host");
        var workspaceA = await SeedWorkspaceAsync(orgA.Id, "summer-show");
        await SeedWorkspaceMemberAsync(orgA.Id, workspaceA.Id, user, MembershipRole.Host);

        var admission = await ResolveAsync(Principal("host", _orgSlugA), _orgSlugA, sessionB.Id);

        AssertDenied(admission, RealtimeConnectionDenialReason.SessionNotFound);
    }

    [Fact]
    public async Task An_empty_session_id_is_session_not_found()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var user = await SeedUserAsync(org.Id, "host");

        AssertDenied(
            await ResolveAsync(Principal("host", _orgSlugA), _orgSlugA, Guid.Empty),
            RealtimeConnectionDenialReason.SessionNotFound);
    }

    [Fact]
    public async Task An_explicitly_empty_participant_id_is_denied()
    {
        var org = await SeedOrganizationAsync(_orgSlugA);
        var workspace = await SeedWorkspaceAsync(org.Id, "summer-show");
        var session = await SeedSessionAsync(org.Id, workspace.Id);
        await SeedUserAsync(org.Id, "host");

        AssertDenied(
            await ResolveAsync(Principal("host", _orgSlugA), _orgSlugA, session.Id, Guid.Empty),
            RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }

    private static void AssertDenied(RealtimeConnectionAdmission admission, RealtimeConnectionDenialReason reason)
    {
        Assert.False(admission.Admitted);
        Assert.Equal(reason, admission.Reason);
        Assert.Empty(admission.Groups);
    }
}
