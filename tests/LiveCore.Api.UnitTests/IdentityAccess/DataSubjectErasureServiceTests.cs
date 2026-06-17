// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Integration-style unit tests for the <see cref="DataSubjectErasureService"/> (CORE-PRIV-001, GDPR Art.17
/// "right to erasure"). They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the whole erasure is exercised against the real model: the explicit
/// anonymization of the PII the foreign keys cannot reach (participant display names, invited emails) AND the
/// schema's <c>ON DELETE SET NULL</c>/<c>CASCADE</c> foreign keys (exports/assets survive anonymized, memberships
/// are revoked), all inside the CORE-CONC-002 transactional unit of work, plus the PII-free audit append.
///
/// The behaviors proven here:
/// <list type="bullet">
///   <item>a successful erasure deletes the subject's profile, anonymizes their participant display data and
///   invited-email rows EVERYWHERE (across workspaces/tenants), leaves their exports/assets anonymized, revokes
///   their memberships and appends one <c>UserProfileErased</c> audit fact by id;</item>
///   <item>the orphan guard refuses (changing nothing) when the subject is the sole Owner of any organization;</item>
///   <item>a missing subject is a no-op NotFound.</item>
/// </list>
/// </summary>
public sealed class DataSubjectErasureServiceTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private static readonly DateTimeOffset _seed = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public DataSubjectErasureServiceTests()
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

    private static DataSubjectErasureService CreateService(LiveCoreDbContext context) =>
        new(
            new TransactionalUnitOfWork(context),
            new UserProfileRepository(context),
            new ParticipantRepository(context),
            new WorkspaceInvitationRepository(context),
            new OrganizationMemberRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task EraseAsync_erases_pii_everywhere_keeps_dependents_anonymized_and_audits()
    {
        Guid orgId = Guid.Empty;
        Guid adminId = Guid.Empty;
        Guid subjectId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectParticipantId = Guid.Empty;
        Guid otherWorkspaceParticipantId = Guid.Empty;
        Guid assetId = Guid.Empty;
        Guid exportId = Guid.Empty;
        Guid invitationId = Guid.Empty;
        const string subjectEmail = "subject@example.test";

        await using (var seed = CreateContext())
        {
            var org = Organization.Create("northwind-labs", "Northwind", _seed);
            var admin = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "admin"), _seed);
            var subject = UserProfile.CreateFromPrincipal(
                new OidcPrincipal(PrincipalType.User, _issuer, "subject", "Subject Person", subjectEmail), _seed);
            seed.Organizations.Add(org);
            seed.UserProfiles.AddRange(admin, subject);

            var adminMembership = OrganizationMember.Create(org.Id, admin.Id, MembershipRole.Owner, _seed);
            var subjectMembership = OrganizationMember.Create(org.Id, subject.Id, MembershipRole.Participant, _seed);
            seed.OrganizationMembers.AddRange(adminMembership, subjectMembership);

            // Two workspaces: the subject has participant display data in BOTH, to prove the anonymization spans
            // workspaces (GDPR Art.17 "everywhere it was stored").
            var workspaceA = Workspace.Create(org.Id, "alpha", "Alpha", _seed);
            var workspaceB = Workspace.Create(org.Id, "beta", "Beta", _seed);
            seed.Workspaces.AddRange(workspaceA, workspaceB);
            seed.WorkspaceMembers.Add(WorkspaceMember.Create(org.Id, workspaceA.Id, subject.Id, MembershipRole.Participant, _seed));

            var participantA = Participant.Create(org.Id, workspaceA.Id, subject.Id, "Subject In A", _seed);
            var participantB = Participant.Create(org.Id, workspaceB.Id, subject.Id, "Subject In B", _seed);
            seed.Participants.AddRange(participantA, participantB);

            var asset = Asset.Create(
                org.Id, workspaceA.Id, subject.Id, "s3", "bucket", $"assets/{Guid.CreateVersion7()}", "image/png", _seed);
            seed.Assets.Add(asset);
            var export = ExportJob.Create(org.Id, workspaceA.Id, subject.Id, ExportScope.Workspace, _seed);
            seed.ExportJobs.Add(export);

            var invitation = WorkspaceInvitation.Create(
                org.Id, workspaceA.Id, subjectEmail, MembershipRole.Participant, _seed, out _);
            seed.WorkspaceInvitations.Add(invitation);

            await seed.SaveChangesAsync();

            orgId = org.Id;
            adminId = admin.Id;
            subjectId = subject.Id;
            subjectOrgMembershipId = subjectMembership.Id;
            subjectParticipantId = participantA.Id;
            otherWorkspaceParticipantId = participantB.Id;
            assetId = asset.Id;
            exportId = export.Id;
            invitationId = invitation.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.EraseAsync(orgId, adminId, subjectId, _now, CancellationToken.None);
            Assert.Equal(DataSubjectErasureResult.Erased, result);
        }

        await using (var verify = CreateContext())
        {
            Assert.False(await verify.UserProfiles.AsNoTracking().AnyAsync(u => u.Id == subjectId));

            var participantA = await verify.Participants.AsNoTracking().FirstAsync(p => p.Id == subjectParticipantId);
            Assert.Equal(Participant.AnonymizedDisplayName, participantA.DisplayName);
            Assert.Null(participantA.UserProfileId);
            var participantB = await verify.Participants.AsNoTracking().FirstAsync(p => p.Id == otherWorkspaceParticipantId);
            Assert.Equal(Participant.AnonymizedDisplayName, participantB.DisplayName);
            Assert.Null(participantB.UserProfileId);

            var invitation = await verify.WorkspaceInvitations.AsNoTracking().FirstAsync(i => i.Id == invitationId);
            Assert.Equal(WorkspaceInvitation.AnonymizedInvitedEmail, invitation.InvitedEmail);

            var asset = await verify.Assets.AsNoTracking().FirstAsync(a => a.Id == assetId);
            Assert.Null(asset.CreatedByUserProfileId);
            var export = await verify.ExportJobs.AsNoTracking().FirstAsync(e => e.Id == exportId);
            Assert.Null(export.RequestedByUserProfileId);

            Assert.False(await verify.OrganizationMembers.AsNoTracking().AnyAsync(m => m.Id == subjectOrgMembershipId));

            var audit = Assert.Single(await verify.AuditLogs.AsNoTracking()
                .Where(e => e.Action == AuditAction.UserProfileErased)
                .ToListAsync());
            Assert.Equal(adminId, audit.ActorUserProfileId);
            Assert.Equal(subjectId, audit.ResourceId);
            Assert.Equal(orgId, audit.OrganizationId);

            // The PII-free hash chain still verifies after the erasure.
            var verification = await new AuditLogChainVerifier(new AuditLogRepository(verify))
                .VerifyAsync(orgId, CancellationToken.None);
            Assert.True(verification.IsValid);
        }
    }

    [Fact]
    public async Task EraseAsync_refuses_and_changes_nothing_when_the_subject_is_the_sole_owner()
    {
        Guid orgId = Guid.Empty;
        Guid adminId = Guid.Empty;
        Guid ownerId = Guid.Empty;
        Guid ownerMembershipId = Guid.Empty;

        await using (var seed = CreateContext())
        {
            var org = Organization.Create("northwind-labs", "Northwind", _seed);
            var admin = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "admin"), _seed);
            var owner = UserProfile.CreateFromPrincipal(
                new OidcPrincipal(PrincipalType.User, _issuer, "owner", "Owner", "owner@example.test"), _seed);
            seed.Organizations.Add(org);
            seed.UserProfiles.AddRange(admin, owner);
            seed.OrganizationMembers.Add(OrganizationMember.Create(org.Id, admin.Id, MembershipRole.Admin, _seed));
            var ownerMembership = OrganizationMember.Create(org.Id, owner.Id, MembershipRole.Owner, _seed);
            seed.OrganizationMembers.Add(ownerMembership);
            await seed.SaveChangesAsync();

            orgId = org.Id;
            adminId = admin.Id;
            ownerId = owner.Id;
            ownerMembershipId = ownerMembership.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.EraseAsync(orgId, adminId, ownerId, _now, CancellationToken.None);
            Assert.Equal(DataSubjectErasureResult.SubjectIsSoleOrganizationOwner, result);
        }

        await using (var verify = CreateContext())
        {
            // Nothing changed: the sole Owner's profile and membership survive, and no erasure was audited.
            Assert.True(await verify.UserProfiles.AsNoTracking().AnyAsync(u => u.Id == ownerId));
            Assert.True(await verify.OrganizationMembers.AsNoTracking().AnyAsync(m => m.Id == ownerMembershipId));
            Assert.Empty(await verify.AuditLogs.AsNoTracking()
                .Where(e => e.Action == AuditAction.UserProfileErased)
                .ToListAsync());
        }
    }

    [Fact]
    public async Task EraseAsync_is_not_found_for_an_unknown_subject()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.EraseAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), _now, CancellationToken.None);

        Assert.Equal(DataSubjectErasureResult.SubjectNotFound, result);
    }

    [Fact]
    public async Task EraseAsync_anonymizes_an_invitation_with_a_matching_email_only()
    {
        Guid orgId = Guid.Empty;
        Guid adminId = Guid.Empty;
        Guid subjectId = Guid.Empty;
        Guid matchingInvitationId = Guid.Empty;
        Guid otherInvitationId = Guid.Empty;
        const string subjectEmail = "subject@example.test";

        await using (var seed = CreateContext())
        {
            var org = Organization.Create("northwind-labs", "Northwind", _seed);
            var admin = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, "admin"), _seed);
            var subject = UserProfile.CreateFromPrincipal(
                new OidcPrincipal(PrincipalType.User, _issuer, "subject", "Subject", subjectEmail), _seed);
            seed.Organizations.Add(org);
            seed.UserProfiles.AddRange(admin, subject);
            seed.OrganizationMembers.Add(OrganizationMember.Create(org.Id, admin.Id, MembershipRole.Owner, _seed));
            seed.OrganizationMembers.Add(OrganizationMember.Create(org.Id, subject.Id, MembershipRole.Participant, _seed));
            var workspace = Workspace.Create(org.Id, "alpha", "Alpha", _seed);
            seed.Workspaces.Add(workspace);
            var matching = WorkspaceInvitation.Create(
                org.Id, workspace.Id, subjectEmail, MembershipRole.Participant, _seed, out _);
            var other = WorkspaceInvitation.Create(
                org.Id, workspace.Id, "different@example.test", MembershipRole.Participant, _seed, out _);
            seed.WorkspaceInvitations.AddRange(matching, other);
            await seed.SaveChangesAsync();

            orgId = org.Id;
            adminId = admin.Id;
            subjectId = subject.Id;
            matchingInvitationId = matching.Id;
            otherInvitationId = other.Id;
        }

        await using (var context = CreateContext())
        {
            await CreateService(context).EraseAsync(orgId, adminId, subjectId, _now, CancellationToken.None);
        }

        await using (var verify = CreateContext())
        {
            var matching = await verify.WorkspaceInvitations.AsNoTracking().FirstAsync(i => i.Id == matchingInvitationId);
            Assert.Equal(WorkspaceInvitation.AnonymizedInvitedEmail, matching.InvitedEmail);
            var other = await verify.WorkspaceInvitations.AsNoTracking().FirstAsync(i => i.Id == otherInvitationId);
            Assert.Equal("different@example.test", other.InvitedEmail);
        }
    }
}
