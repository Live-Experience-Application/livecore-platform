// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the data-subject access/portability export (CORE-PRIV-004, GDPR Art.15 access /
/// Art.20 portability,
/// <c>GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export</c>,
/// csv/api_routes.csv roles "Data subject or Owner/Admin"). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (SQLite with foreign keys enforced), so the documented request flow
/// (authentication -> tenant context resolver -> endpoint -> inline authorization -> export command -> audit) is
/// exercised end-to-end.
///
/// The heart of the story (the story's required tests) plus the threat model (T1/T5/T7):
/// <list type="bullet">
///   <item>a data subject retrieves THEIR OWN personal-data export — the documented set (identity profile,
///   organization membership, workspace memberships, participant records, invitations) and NOTHING belonging to
///   another subject;</item>
///   <item>an Owner/Admin can obtain it on the subject's behalf, and the export is tenant-scoped (a record in
///   another tenant is never disclosed);</item>
///   <item>another tenant member who is neither the subject nor an Owner/Admin cannot retrieve it (403);</item>
///   <item>401 for missing auth; 404 (hidden) for a cross-tenant/unknown organization or member;</item>
///   <item>the access is audited by id (PersonalDataExported) and the response never carries an invite token
///   hash or any secret (T6/T7).</item>
/// </list>
/// </summary>
public sealed class PersonalDataExportEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _subjectEmail = "subject.person@example.test";
    private const string _subjectDisplayName = "Subject Person";
    private const string _subjectParticipantName = "Subject The Participant";
    private const string _otherEmail = "other.person@example.test";
    private const string _otherParticipantName = "Other The Participant";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static string ExportRoute(string organizationSlug, Guid memberId) =>
        $"/api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Export_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(ExportRoute(_orgA, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: the subject retrieves their OWN export (self-service) ----------

    [Fact]
    public async Task Subject_retrieves_their_own_export_with_the_documented_set_and_nothing_of_other_subjects()
    {
        await using var factory = new WorkspaceApiFactory();

        const string subjectSubject = "data-subject-a";
        Guid subjectProfileId = Guid.Empty;
        Guid orgAId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectWorkspaceMembershipId = Guid.Empty;
        Guid subjectParticipantId = Guid.Empty;
        Guid subjectInvitationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;

            var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;

            var workspace = await db.AddWorkspaceAsync(org.Id, "alpha", "Alpha");
            workspaceId = workspace.Id;
            var workspaceMembership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, subject.Id, MembershipRole.Participant);
            subjectWorkspaceMembershipId = workspaceMembership.Id;
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, subject.Id, _subjectParticipantName);
            subjectParticipantId = participant.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _subjectEmail);
            subjectInvitationId = invitation.Id;

            // CROSS-SUBJECT controls in the SAME tenant/workspace: another user's membership, participant and
            // invitation — none may appear in the subject's export.
            var other = await db.AddUserAsync(_issuer, "other-user-a", "Other Person", _otherEmail);
            await db.AddOrganizationMemberAsync(org.Id, other.Id, MembershipRole.Participant);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, other.Id, MembershipRole.Participant);
            await db.AddParticipantAsync(org.Id, workspace.Id, other.Id, _otherParticipantName);
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _otherEmail);
            // An anonymous participant (no user link) must never match the subject either.
            await db.AddParticipantAsync(org.Id, workspace.Id, null, "Guest");
        });

        // The subject themselves (a Participant) obtains their own export.
        using var client = factory.CreateClientFor(subjectSubject, _issuer, _orgA);
        var response = await client.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await ReadExportAsync(response);

        Assert.Equal(orgAId, export.OrganizationId);

        // The identity profile (the subject's own, disclosed under Art.15/20).
        Assert.Equal(subjectProfileId, export.Subject.Id);
        Assert.Equal(_issuer, export.Subject.Issuer);
        Assert.Equal(subjectSubject, export.Subject.Subject);
        Assert.Equal(_subjectDisplayName, export.Subject.DisplayName);
        Assert.Equal(_subjectEmail, export.Subject.Email);

        // The organization membership.
        Assert.NotNull(export.OrganizationMembership);
        Assert.Equal(subjectOrgMembershipId, export.OrganizationMembership!.Id);
        Assert.Equal("Participant", export.OrganizationMembership.Role);

        // Exactly the subject's own workspace membership, participant and invitation — nothing of the other user.
        var workspaceMembership = Assert.Single(export.WorkspaceMemberships);
        Assert.Equal(subjectWorkspaceMembershipId, workspaceMembership.Id);
        Assert.Equal(workspaceId, workspaceMembership.WorkspaceId);

        var participant = Assert.Single(export.Participants);
        Assert.Equal(subjectParticipantId, participant.Id);
        Assert.Equal(_subjectParticipantName, participant.DisplayName);

        var invitation = Assert.Single(export.Invitations);
        Assert.Equal(subjectInvitationId, invitation.Id);
        Assert.Equal(_subjectEmail, invitation.InvitedEmail);

        // Nothing belonging to another subject leaked anywhere in the response.
        Assert.DoesNotContain(_otherParticipantName, export.Participants.Select(p => p.DisplayName));
        Assert.DoesNotContain(_otherEmail, export.Invitations.Select(i => i.InvitedEmail));

        // The access is audited by id only (actor == the subject, the exported subject), org-level, no PII.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var audit = Assert.Single(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
        Assert.Equal(subjectProfileId, audit.ActorUserProfileId);
        Assert.Equal(subjectProfileId, audit.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.IdentityAccess.UserProfile), audit.ResourceType);
        Assert.Null(audit.WorkspaceId);
    }

    // ---- 200: an Owner/Admin obtains it on the subject's behalf, tenant-scoped

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Owner_or_admin_obtains_the_export_on_the_subjects_behalf_scoped_to_the_tenant(
        MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();

        const string adminSubject = "admin-a";
        Guid adminProfileId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, callerRole);

            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(orgA.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
            var workspaceA = await db.AddWorkspaceAsync(orgA.Id, "alpha", "Alpha");
            await db.AddParticipantAsync(orgA.Id, workspaceA.Id, subject.Id, _subjectParticipantName);

            // CROSS-TENANT control (T5): the SAME subject is also a member of org B with a participant there. The
            // org-A admin's export must never disclose the subject's org-B activity.
            await db.AddOrganizationMemberAsync(orgB.Id, subject.Id, MembershipRole.Participant);
            var workspaceB = await db.AddWorkspaceAsync(orgB.Id, "beta", "Beta");
            await db.AddParticipantAsync(orgB.Id, workspaceB.Id, subject.Id, "Subject In B");
            await db.AddWorkspaceInvitationAsync(orgB.Id, workspaceB.Id, MembershipRole.Participant, invitedEmail: _subjectEmail);
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await ReadExportAsync(response);

        // The subject's identity is disclosed to the data controller acting on their behalf.
        Assert.Equal(subjectProfileId, export.Subject.Id);
        // But only the org-A participant — the org-B records are NOT disclosed (tenant-scoped; T5).
        var participant = Assert.Single(export.Participants);
        Assert.Equal(_subjectParticipantName, participant.DisplayName);
        Assert.DoesNotContain("Subject In B", export.Participants.Select(p => p.DisplayName));
        // The org-B invitation to the subject's email is not disclosed through the org-A export either.
        Assert.Empty(export.Invitations);

        // Audited with the ADMIN as the actor (who obtained it) and the subject as the resource.
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var audit = Assert.Single(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(subjectProfileId, audit.ResourceId);
    }

    // ---- 403: a tenant member who is neither the subject nor Owner/Admin -----

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Export_is_403_for_a_tenant_member_that_is_not_the_subject_or_owner_admin(
        MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            // A real Owner so the tenant is not ownerless.
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Nothing was disclosed: no export was audited.
        await AssertNoExportAuditedAsync(factory);
    }

    // ---- 404 hidden: cross-tenant / unknown (T1/T5) -------------------------

    [Fact]
    public async Task Export_is_404_for_a_member_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the member exists in org B. Addressing it through org A is hidden
        // as 404 (the member id belongs to a tenant the caller cannot see).
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid membershipInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-b", _subjectDisplayName, _subjectEmail);
            var membership = await db.AddOrganizationMemberAsync(orgB.Id, subject.Id, MembershipRole.Participant);
            membershipInBId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.GetAsync(ExportRoute(_orgA, membershipInBId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoExportAuditedAsync(factory);
    }

    [Fact]
    public async Task Export_is_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // T5: the caller is an Owner of org A and names org A in the path, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid subjectOrgMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgB);
        var response = await client.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoExportAuditedAsync(factory);
    }

    [Fact]
    public async Task Export_is_404_for_an_unknown_member_in_the_callers_org()
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
        var response = await client.GetAsync(ExportRoute(_orgA, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_is_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        Guid subjectOrgMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoExportAuditedAsync(factory);
    }

    // ---- T6/T7: the response carries no invite token hash or any secret -----

    [Fact]
    public async Task Export_response_exposes_only_safe_fields_and_no_token_or_secret()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subjectSubject = "principal-subject";
        Guid subjectOrgMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;
            var workspace = await db.AddWorkspaceAsync(org.Id, "alpha", "Alpha");
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _subjectEmail);
        });

        using var client = factory.CreateClientFor(subjectSubject, _issuer, _orgA);
        var response = await client.GetAsync(ExportRoute(_orgA, subjectOrgMembershipId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // No property anywhere names a credential/token — in particular no invitation token hash (T6/T7). The
        // invited email IS the subject's disclosed personal datum and is allowed; "secret"/"token"/"hash"/etc are
        // not.
        var forbidden = new[] { "token", "secret", "password", "bearer", "credential", "hash", "tokenhash" };
        foreach (var name in AllPropertyNames(document.RootElement))
        {
            Assert.DoesNotContain(
                forbidden,
                fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in AllPropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in AllPropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static async Task AssertNoExportAuditedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Empty(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
    }

    private static async Task<ExportDto> ReadExportAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<ExportDto>(_json);
        Assert.NotNull(dto);
        Assert.NotNull(dto!.Subject);
        Assert.NotNull(dto.WorkspaceMemberships);
        Assert.NotNull(dto.Participants);
        Assert.NotNull(dto.Invitations);
        return dto;
    }

    private sealed record ExportDto(
        Guid OrganizationId,
        ExportSubjectDto Subject,
        ExportOrganizationMembershipDto? OrganizationMembership,
        ExportWorkspaceMembershipDto[] WorkspaceMemberships,
        ExportParticipantDto[] Participants,
        ExportInvitationDto[] Invitations);

    private sealed record ExportSubjectDto(Guid Id, string Issuer, string Subject, string? DisplayName, string? Email);

    private sealed record ExportOrganizationMembershipDto(Guid Id, Guid OrganizationId, string Role, DateTimeOffset CreatedAt);

    private sealed record ExportWorkspaceMembershipDto(Guid Id, Guid WorkspaceId, string Role, DateTimeOffset CreatedAt);

    private sealed record ExportParticipantDto(Guid Id, Guid WorkspaceId, string DisplayName, string Status, DateTimeOffset CreatedAt);

    private sealed record ExportInvitationDto(
        Guid Id,
        Guid WorkspaceId,
        string InvitedEmail,
        string Role,
        string Status,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);
}
