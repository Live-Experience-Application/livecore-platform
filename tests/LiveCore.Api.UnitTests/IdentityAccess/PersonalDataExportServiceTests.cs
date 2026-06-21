// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Integration-style unit tests for the <see cref="PersonalDataExportService"/> (CORE-PRIV-004, GDPR Art.15
/// access / Art.20 portability). They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the export is exercised against the real model: the tenant-scoped reads
/// of the subject's profile, organization membership, workspace memberships, participant records and invited-email
/// rows, plus the PII-free audit append.
///
/// The behaviors proven here:
/// <list type="bullet">
///   <item>a successful export gathers exactly the subject's personal data WITHIN the resolved tenant — and
///   NOTHING from another tenant (threat T5) nor anything belonging to another subject — and appends one
///   <c>PersonalDataExported</c> audit fact by id, leaving the PII-free hash chain verifying;</item>
///   <item>a subject with no email discloses no invited-email rows;</item>
///   <item>a missing subject is a null (NotFound) result that audits nothing.</item>
/// </list>
/// </summary>
public sealed class PersonalDataExportServiceTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subjectEmail = "subject@example.test";
    private static readonly DateTimeOffset _seed = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PersonalDataExportServiceTests()
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

    private static PersonalDataExportService CreateService(LiveCoreDbContext context) =>
        new(
            new UserProfileRepository(context),
            new OrganizationMemberRepository(context),
            new WorkspaceMemberRepository(context),
            new ParticipantRepository(context),
            new WorkspaceInvitationRepository(context),
            new PushSubscriptionRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task ExportAsync_assembles_the_subjects_tenant_scoped_data_only_and_audits()
    {
        Guid orgAId = Guid.Empty;
        Guid orgBId = Guid.Empty;
        Guid subjectId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid workspaceMembershipAId = Guid.Empty;
        Guid participantAId = Guid.Empty;
        Guid invitationAId = Guid.Empty;
        Guid subjectPushSubscriptionId = Guid.Empty;

        await using (var seed = CreateContext())
        {
            var orgA = Organization.Create("northwind-labs", "Northwind", _seed);
            var orgB = Organization.Create("acme-co", "Acme", _seed);
            seed.Organizations.AddRange(orgA, orgB);

            var subject = UserProfile.CreateFromPrincipal(
                new OidcPrincipal(PrincipalType.User, _issuer, "subject", "Subject Person", _subjectEmail), _seed);
            var other = UserProfile.CreateFromPrincipal(
                new OidcPrincipal(PrincipalType.User, _issuer, "other", "Other Person", "other@example.test"), _seed);
            seed.UserProfiles.AddRange(subject, other);

            // The subject in tenant A: membership, a workspace membership, a participant record and an invitation.
            var subjectMembershipA = OrganizationMember.Create(orgA.Id, subject.Id, MembershipRole.Participant, _seed);
            seed.OrganizationMembers.Add(subjectMembershipA);
            var workspaceA = Workspace.Create(orgA.Id, "alpha", "Alpha", _seed);
            seed.Workspaces.Add(workspaceA);
            var workspaceMembershipA = WorkspaceMember.Create(orgA.Id, workspaceA.Id, subject.Id, MembershipRole.Participant, _seed);
            seed.WorkspaceMembers.Add(workspaceMembershipA);
            var participantA = Participant.Create(orgA.Id, workspaceA.Id, subject.Id, "Subject In A", _seed);
            seed.Participants.Add(participantA);
            var invitationA = WorkspaceInvitation.Create(orgA.Id, workspaceA.Id, _subjectEmail, MembershipRole.Participant, _seed, out _);
            seed.WorkspaceInvitations.Add(invitationA);

            // A global, per-principal Web Push subscription for the subject (CORE-PUSH-001): no tenant, so it is
            // disclosed regardless of the resolved tenant. A control subscription belongs to the OTHER subject.
            var subjectPushSubscription = PushSubscription.Register(
                subject.Id, "https://push.example.test/sub/subject", "p256dh-subject", "auth-subject", _seed);
            seed.PushSubscriptions.Add(subjectPushSubscription);
            seed.PushSubscriptions.Add(PushSubscription.Register(
                other.Id, "https://push.example.test/sub/other", "p256dh-other", "auth-other", _seed));

            // CROSS-TENANT control (threat T5): the SAME subject also has data in tenant B — it must NOT appear in
            // the tenant-A export.
            seed.OrganizationMembers.Add(OrganizationMember.Create(orgB.Id, subject.Id, MembershipRole.Participant, _seed));
            var workspaceB = Workspace.Create(orgB.Id, "beta", "Beta", _seed);
            seed.Workspaces.Add(workspaceB);
            seed.WorkspaceMembers.Add(WorkspaceMember.Create(orgB.Id, workspaceB.Id, subject.Id, MembershipRole.Participant, _seed));
            seed.Participants.Add(Participant.Create(orgB.Id, workspaceB.Id, subject.Id, "Subject In B", _seed));
            seed.WorkspaceInvitations.Add(WorkspaceInvitation.Create(orgB.Id, workspaceB.Id, _subjectEmail, MembershipRole.Participant, _seed, out _));

            // CROSS-SUBJECT control: ANOTHER subject's data in tenant A — it must NOT appear in the subject's export.
            seed.WorkspaceMembers.Add(WorkspaceMember.Create(orgA.Id, workspaceA.Id, other.Id, MembershipRole.Participant, _seed));
            seed.Participants.Add(Participant.Create(orgA.Id, workspaceA.Id, other.Id, "Other In A", _seed));
            seed.WorkspaceInvitations.Add(WorkspaceInvitation.Create(orgA.Id, workspaceA.Id, "other@example.test", MembershipRole.Participant, _seed, out _));

            await seed.SaveChangesAsync();

            orgAId = orgA.Id;
            orgBId = orgB.Id;
            subjectId = subject.Id;
            subjectOrgMembershipId = subjectMembershipA.Id;
            workspaceMembershipAId = workspaceMembershipA.Id;
            participantAId = participantA.Id;
            invitationAId = invitationA.Id;
            subjectPushSubscriptionId = subjectPushSubscription.Id;
        }

        PersonalDataExport? export;
        await using (var context = CreateContext())
        {
            // The subject themselves obtains the export (self-service: actor == subject).
            export = await CreateService(context).ExportAsync(orgAId, subjectId, subjectId, _now, CancellationToken.None);
        }

        Assert.NotNull(export);
        Assert.Equal(orgAId, export!.OrganizationId);

        // The identity profile is the subject's own.
        Assert.Equal(subjectId, export.Subject.Id);
        Assert.Equal(_subjectEmail, export.Subject.Email);

        // The organization membership is exactly the tenant-A one.
        Assert.NotNull(export.OrganizationMembership);
        Assert.Equal(subjectOrgMembershipId, export.OrganizationMembership!.Id);
        Assert.Equal(orgAId, export.OrganizationMembership.OrganizationId);

        // Exactly the tenant-A workspace membership, participant and invitation — the tenant-B copies are absent
        // (threat T5), and the other subject's records are absent.
        var workspaceMembership = Assert.Single(export.WorkspaceMemberships);
        Assert.Equal(workspaceMembershipAId, workspaceMembership.Id);
        var participant = Assert.Single(export.Participants);
        Assert.Equal(participantAId, participant.Id);
        Assert.Equal("Subject In A", participant.DisplayName);
        var invitation = Assert.Single(export.Invitations);
        Assert.Equal(invitationAId, invitation.Id);
        Assert.Equal(_subjectEmail, invitation.InvitedEmail);

        // The global push subscription is exactly the subject's own (the other subject's is absent), and the
        // auth encryption secret is never carried on the assembled aggregate's projection.
        var pushSubscription = Assert.Single(export.PushSubscriptions);
        Assert.Equal(subjectPushSubscriptionId, pushSubscription.Id);
        Assert.Equal("https://push.example.test/sub/subject", pushSubscription.Endpoint);

        await using (var verify = CreateContext())
        {
            // The access is audited exactly once, by id only: organization-level (no workspace), capturing the
            // actor and the exported subject, never the disclosed PII.
            var audit = Assert.Single(await verify.AuditLogs.AsNoTracking()
                .Where(e => e.Action == AuditAction.PersonalDataExported)
                .ToListAsync());
            Assert.Equal(subjectId, audit.ActorUserProfileId);
            Assert.Equal(subjectId, audit.ResourceId);
            Assert.Equal(nameof(UserProfile), audit.ResourceType);
            Assert.Equal(orgAId, audit.OrganizationId);
            Assert.Null(audit.WorkspaceId);
            Assert.Null(audit.PreviousState);
            Assert.Null(audit.NewState);

            // No tenant-B audit fact was written (the export was tenant-A only).
            Assert.Empty(await verify.AuditLogs.AsNoTracking()
                .Where(e => e.OrganizationId == orgBId)
                .ToListAsync());

            // The PII-free hash chain still verifies after the export.
            var verification = await new AuditLogChainVerifier(new AuditLogRepository(verify))
                .VerifyAsync(orgAId, CancellationToken.None);
            Assert.True(verification.IsValid);
        }
    }

    [Fact]
    public async Task ExportAsync_returns_no_invitations_when_the_subject_has_no_email()
    {
        Guid orgId = Guid.Empty;
        Guid subjectId = Guid.Empty;

        await using (var seed = CreateContext())
        {
            var org = Organization.Create("northwind-labs", "Northwind", _seed);
            seed.Organizations.Add(org);
            // A subject the provider never asserted an email for (no email), but an invitation exists for SOME
            // email — it must not be matched, because the subject has no email to match by.
            var subject = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "subject"), _seed);
            seed.UserProfiles.Add(subject);
            seed.OrganizationMembers.Add(OrganizationMember.Create(org.Id, subject.Id, MembershipRole.Participant, _seed));
            var workspace = Workspace.Create(org.Id, "alpha", "Alpha", _seed);
            seed.Workspaces.Add(workspace);
            seed.WorkspaceInvitations.Add(
                WorkspaceInvitation.Create(org.Id, workspace.Id, "someone@example.test", MembershipRole.Participant, _seed, out _));
            await seed.SaveChangesAsync();

            orgId = org.Id;
            subjectId = subject.Id;
        }

        await using var context = CreateContext();
        var export = await CreateService(context).ExportAsync(orgId, subjectId, subjectId, _now, CancellationToken.None);

        Assert.NotNull(export);
        Assert.Null(export!.Subject.Email);
        Assert.Empty(export.Invitations);
    }

    [Fact]
    public async Task ExportAsync_returns_null_and_audits_nothing_for_an_unknown_subject()
    {
        await using var context = CreateContext();
        var organizationId = Guid.CreateVersion7();

        var export = await CreateService(context).ExportAsync(
            organizationId, Guid.CreateVersion7(), Guid.CreateVersion7(), _now, CancellationToken.None);

        Assert.Null(export);
        Assert.Empty(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
    }
}
