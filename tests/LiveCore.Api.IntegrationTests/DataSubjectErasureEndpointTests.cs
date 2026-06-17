using System.Net;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for data-subject erasure (CORE-PRIV-001, GDPR Art.17 "right to erasure",
/// <c>DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data</c>, csv/api_routes.csv
/// roles Owner,Admin). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (SQLite with foreign keys enforced), so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> erasure command -> audit) AND the schema's
/// <c>ON DELETE SET NULL</c>/<c>CASCADE</c> foreign keys are exercised end-to-end.
///
/// The heart of the story is that an authorized erasure removes the subject's personal data EVERYWHERE it was
/// stored while the PII-free immutable audit chain still verifies, under negative authorization and tenant
/// isolation (threats T1/T5/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>the subject's user profile (OIDC subject, email, display name) is deleted, their participant display
///   data and invited-email rows are anonymized, and their exports/assets survive with a null creator;</item>
///   <item>the audit hash chain still verifies (the erasure is audited by id only);</item>
///   <item>401 for missing auth; 403 for a tenant member that is not Owner/Admin; 404 (hidden) for a
///   cross-tenant/unknown organization or member; 409 for the sole organization Owner.</item>
/// </list>
/// </summary>
public sealed class DataSubjectErasureEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _subjectEmail = "subject.person@example.test";
    private const string _subjectDisplayName = "Subject Person";
    private const string _subjectDisplayNameOnParticipant = "Subject The Participant";

    private static string PersonalDataRoute(string organizationSlug, Guid memberId) =>
        $"/api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Erase_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success — erases PII everywhere, survives dependents, audits ---

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Erase_succeeds_anonymizes_pii_everywhere_dependents_survive_and_audit_chain_verifies(
        MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();

        const string adminSubject = "admin-a";
        const string subjectSubject = "data-subject-a";
        const string otherSubject = "other-user-a";

        Guid orgAId = Guid.Empty;
        Guid adminProfileId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectWorkspaceMembershipId = Guid.Empty;
        Guid subjectParticipantId = Guid.Empty;
        Guid anonymousParticipantId = Guid.Empty;
        Guid otherParticipantId = Guid.Empty;
        Guid subjectAssetId = Guid.Empty;
        Guid subjectExportId = Guid.Empty;
        Guid subjectInvitationId = Guid.Empty;
        Guid otherInvitationId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);

            // The data subject: a member with PII on their profile (email + display name), plus personal data
            // scattered across the schema.
            var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var subjectMembership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = subjectMembership.Id;

            var workspace = await db.AddWorkspaceAsync(org.Id, "alpha", "Alpha");
            var subjectWorkspaceMembership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, subject.Id, MembershipRole.Participant);
            subjectWorkspaceMembershipId = subjectWorkspaceMembership.Id;

            // Participant display data linked to the subject (PII display name + user link).
            var subjectParticipant = await db.AddParticipantAsync(
                org.Id, workspace.Id, subject.Id, _subjectDisplayNameOnParticipant);
            subjectParticipantId = subjectParticipant.Id;

            // Controls that must be left untouched: an anonymous participant and a different user's participant.
            var anonymousParticipant = await db.AddParticipantAsync(org.Id, workspace.Id, null, "Guest");
            anonymousParticipantId = anonymousParticipant.Id;
            var other = await db.AddUserAsync(_issuer, otherSubject, "Other Person", "other.person@example.test");
            var otherParticipant = await db.AddParticipantAsync(org.Id, workspace.Id, other.Id, "Other Person");
            otherParticipantId = otherParticipant.Id;

            // Dependents the subject created: they must SURVIVE anonymized (created_by/requested_by -> null) via
            // the schema's ON DELETE SET NULL foreign keys.
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, subject.Id);
            subjectAssetId = asset.Id;
            var export = await db.AddExportJobAsync(org.Id, workspace.Id, subject.Id);
            subjectExportId = export.Id;

            // An invited-email row for the subject's email (must be anonymized) and a control for another email.
            var (subjectInvitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _subjectEmail);
            subjectInvitationId = subjectInvitation.Id;
            var (otherInvitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: "someone.else@example.test");
            otherInvitationId = otherInvitation.Id;

            // Pre-existing audit history so the tenant's hash chain has content before the erasure appends to it.
            await db.AddAuditLogEntryAsync(org.Id, AuditAction.SessionStarted);
            await db.AddAuditLogEntryAsync(org.Id, AuditAction.SessionEnded);
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(PersonalDataRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        // The user profile (OIDC subject, email, display name) is GONE.
        Assert.False(await context.UserProfiles.AsNoTracking().AnyAsync(u => u.Id == subjectProfileId));
        // It is gone EVERYWHERE: no row retains the subject's email or display name.
        Assert.False(await context.UserProfiles.AsNoTracking().AnyAsync(u => u.Email == _subjectEmail));
        Assert.False(await context.UserProfiles.AsNoTracking().AnyAsync(u => u.DisplayName == _subjectDisplayName));

        // The subject's participant display data is anonymized (placeholder name, user link cleared).
        var subjectParticipant = await context.Participants.AsNoTracking()
            .FirstAsync(p => p.Id == subjectParticipantId);
        Assert.Equal(Participant.AnonymizedDisplayName, subjectParticipant.DisplayName);
        Assert.Null(subjectParticipant.UserProfileId);
        // No participant anywhere still carries the subject's plaintext display name.
        Assert.False(await context.Participants.AsNoTracking()
            .AnyAsync(p => p.DisplayName == _subjectDisplayNameOnParticipant));

        // Controls untouched.
        var anonymousParticipant = await context.Participants.AsNoTracking()
            .FirstAsync(p => p.Id == anonymousParticipantId);
        Assert.Equal("Guest", anonymousParticipant.DisplayName);
        var otherParticipant = await context.Participants.AsNoTracking()
            .FirstAsync(p => p.Id == otherParticipantId);
        Assert.Equal("Other Person", otherParticipant.DisplayName);
        Assert.NotNull(otherParticipant.UserProfileId);

        // The subject's invited-email row is anonymized; the control invitation is untouched.
        var subjectInvitation = await context.WorkspaceInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == subjectInvitationId);
        Assert.Equal(WorkspaceInvitation.AnonymizedInvitedEmail, subjectInvitation.InvitedEmail);
        var otherInvitation = await context.WorkspaceInvitations.AsNoTracking()
            .FirstAsync(i => i.Id == otherInvitationId);
        Assert.Equal("someone.else@example.test", otherInvitation.InvitedEmail);
        Assert.False(await context.WorkspaceInvitations.AsNoTracking().AnyAsync(i => i.InvitedEmail == _subjectEmail));

        // Exports/assets the subject created SURVIVE with a null (anonymized) creator (ON DELETE SET NULL).
        var asset = await context.Assets.AsNoTracking().FirstAsync(a => a.Id == subjectAssetId);
        Assert.Null(asset.CreatedByUserProfileId);
        var export = await context.ExportJobs.AsNoTracking().FirstAsync(e => e.Id == subjectExportId);
        Assert.Null(export.RequestedByUserProfileId);

        // The subject's access grants are revoked (ON DELETE CASCADE removed the memberships).
        Assert.False(await context.OrganizationMembers.AsNoTracking().AnyAsync(m => m.Id == subjectOrgMembershipId));
        Assert.False(await context.WorkspaceMembers.AsNoTracking().AnyAsync(m => m.Id == subjectWorkspaceMembershipId));

        // The erasure is audited exactly once, by id only: organization-level (no workspace), capturing the
        // actor and the erased subject, never the erased PII.
        var audit = Assert.Single(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.UserProfileErased)
            .ToListAsync());
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(subjectProfileId, audit.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.IdentityAccess.UserProfile), audit.ResourceType);
        Assert.Null(audit.WorkspaceId);
        Assert.Null(audit.PreviousState);
        Assert.Null(audit.NewState);
        Assert.Null(audit.TargetParticipantId);

        // The PII-free immutable audit hash chain still verifies after the erasure.
        var verifier = scope.ServiceProvider.GetRequiredService<AuditLogChainVerifier>();
        var verification = await verifier.VerifyAsync(orgAId, CancellationToken.None);
        Assert.True(verification.IsValid);
    }

    // ---- 403: authenticated tenant member without Owner/Admin ----------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Erase_is_403_for_a_tenant_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid subjectMembershipId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            // A real Owner so the tenant is not ownerless (keeps the failure about the caller's role).
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, subjectMembershipId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Nothing was erased: the subject profile survives and no erasure was audited.
        await AssertSubjectIntactAsync(factory, subjectProfileId);
    }

    // ---- 404 hidden: cross-tenant / unknown (T1/T5) -------------------------

    [Fact]
    public async Task Erase_is_404_for_a_member_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the member exists in org B. Addressing it through org A is hidden
        // as 404 (the member id belongs to a tenant the caller cannot see).
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid membershipInBId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-b", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(orgB.Id, subject.Id, MembershipRole.Participant);
            membershipInBId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, membershipInBId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSubjectIntactAsync(factory, subjectProfileId);
    }

    [Fact]
    public async Task Erase_is_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        Guid subjectMembershipId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, subjectMembershipId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSubjectIntactAsync(factory, subjectProfileId);
    }

    [Fact]
    public async Task Erase_is_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // T5: the caller is an Owner of org A and names org A in the path, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid subjectMembershipId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectMembershipId = membership.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, subjectMembershipId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertSubjectIntactAsync(factory, subjectProfileId);
    }

    [Fact]
    public async Task Erase_is_404_for_an_unknown_member_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 409: sole-Owner orphan invariant -----------------------------------

    [Fact]
    public async Task Erase_is_409_for_the_sole_organization_owner_and_changes_nothing()
    {
        // Erasing the sole Owner would cascade-remove their membership and leave the tenant permanently
        // unreachable. A different Admin (so the subject stays the sole OWNER) attempts the erasure.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        const string ownerSubject = "owner-a";
        Guid ownerMembershipId = Guid.Empty;
        Guid ownerProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Admin);
            var owner = await db.AddUserAsync(_issuer, ownerSubject, _subjectDisplayName, _subjectEmail);
            ownerProfileId = owner.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            ownerMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(PersonalDataRoute(_orgA, ownerMembershipId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Nothing changed: the sole Owner's profile and membership survive, and no erasure was audited.
        await AssertSubjectIntactAsync(factory, ownerProfileId);
        Assert.True(await MembershipExistsAsync(factory, ownerMembershipId));
    }

    private static async Task AssertSubjectIntactAsync(WorkspaceApiFactory factory, Guid subjectProfileId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.True(await context.UserProfiles.AsNoTracking().AnyAsync(u => u.Id == subjectProfileId));
        Assert.Empty(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.UserProfileErased)
            .ToListAsync());
    }

    private static async Task<bool> MembershipExistsAsync(WorkspaceApiFactory factory, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.OrganizationMembers.AsNoTracking().AnyAsync(member => member.Id == membershipId);
    }
}
