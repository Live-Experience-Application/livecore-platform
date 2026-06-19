// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the USER-DATA export producer and its download (CORE-EXP-002, "Add the user-data
/// export producer", GDPR Art.15 access / Art.20 portability). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (EF Core SQLite, foreign keys ON), exercising the whole flow: the
/// worker PRODUCES a queued <see cref="ExportScope.UserData"/> job into a retrievable completed export, and the
/// existing export download route (<c>GET /api/v1/exports/{exportId}</c>, CORE-EXP-001) then DISCLOSES the data
/// subject's personal data — assembled tenant-scoped and audited through the reused
/// <see cref="LiveCore.Api.IdentityAccess.PersonalDataExportService"/> (CORE-PRIV-004), delivered as the
/// authorized response stream.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md "Export data subject personal data"; threats T1/T5/T8):
/// <list type="bullet">
///   <item>PRODUCED + DOWNLOADABLE: a queued user-data job is produced by the worker and is then downloadable by
///   the authorized data subject, returning exactly the subject's records and NOTHING of another subject.</item>
///   <item>ON BEHALF: an Owner/Admin downloads it on the subject's behalf, tenant-scoped.</item>
///   <item>DENIED: a tenant member who is neither the subject nor an Owner/Admin is 403; an unauthenticated
///   caller is 401; a user-data export in another tenant is hidden as 404 (T5).</item>
///   <item>STATE: a not-yet-produced (still pending) export is 409 for an authorized caller.</item>
///   <item>AUDITED: a successful disclosure appends a <c>PersonalDataExported</c> fact by id (actor + subject),
///   and a denied request audits nothing.</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// authorized/denied sets, never an ordering comparison. All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class UserDataExportEndpointTests
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

    /// <summary>The roles DENIED a user-data export they are not the subject of (matrix: only the subject or Owner/Admin).</summary>
    public static TheoryData<MembershipRole> NonEntitledRoles =>
    [
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // ---- 401 unauthenticated ------------------------------------------------

    [Fact]
    public async Task Download_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/exports/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- the producer makes the export downloadable by the subject ----------

    [Fact]
    public async Task Worker_produces_the_export_and_the_subject_downloads_their_records_and_no_other_subjects()
    {
        await using var factory = new WorkspaceApiFactory();

        const string subjectSubject = "data-subject-a";
        Guid subjectProfileId = Guid.Empty;
        Guid orgAId = Guid.Empty;
        Guid subjectOrgMembershipId = Guid.Empty;
        Guid subjectWorkspaceMembershipId = Guid.Empty;
        Guid subjectParticipantId = Guid.Empty;
        Guid subjectInvitationId = Guid.Empty;
        Guid exportJobId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            orgAId = org.Id;

            var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            var membership = await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            subjectOrgMembershipId = membership.Id;

            var workspace = await db.AddWorkspaceAsync(org.Id, "alpha", "Alpha");
            var workspaceMembership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, subject.Id, MembershipRole.Participant);
            subjectWorkspaceMembershipId = workspaceMembership.Id;
            var participant = await db.AddParticipantAsync(org.Id, workspace.Id, subject.Id, _subjectParticipantName);
            subjectParticipantId = participant.Id;
            var (invitation, _) = await db.AddWorkspaceInvitationAsync(
                org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _subjectEmail);
            subjectInvitationId = invitation.Id;

            // CROSS-SUBJECT controls in the SAME tenant/workspace: none may appear in the subject's export.
            var other = await db.AddUserAsync(_issuer, "other-user-a", "Other Person", _otherEmail);
            await db.AddOrganizationMemberAsync(org.Id, other.Id, MembershipRole.Participant);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, other.Id, MembershipRole.Participant);
            await db.AddParticipantAsync(org.Id, workspace.Id, other.Id, _otherParticipantName);
            await db.AddWorkspaceInvitationAsync(org.Id, workspace.Id, MembershipRole.Participant, invitedEmail: _otherEmail);

            // The data subject requests their own data: a PENDING user-data export job (no producer has run yet).
            var job = await db.AddExportJobAsync(
                org.Id, workspace.Id, subject.Id, ExportScope.UserData, ExportJobStatus.Pending);
            exportJobId = job.Id;
        });

        // The producer (worker) runs and drives the user-data job to terminal Completed — it is now retrievable.
        var result = await RunExportProducerAsync(factory);
        Assert.Equal(1, result.Processed);
        var producedJob = await FindExportJobAsync(factory, orgAId, exportJobId);
        Assert.NotNull(producedJob);
        Assert.Equal(ExportJobStatus.Completed, producedJob!.Status);

        // The data subject downloads their produced export.
        using var client = factory.CreateClientFor(subjectSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await ReadExportAsync(response);

        Assert.Equal(orgAId, export.OrganizationId);

        // The subject's own identity profile (disclosed under Art.15/20).
        Assert.Equal(subjectProfileId, export.Subject.Id);
        Assert.Equal(_subjectDisplayName, export.Subject.DisplayName);
        Assert.Equal(_subjectEmail, export.Subject.Email);

        // Exactly the subject's own records — nothing of the other user.
        Assert.NotNull(export.OrganizationMembership);
        Assert.Equal(subjectOrgMembershipId, export.OrganizationMembership!.Id);
        var workspaceMembership = Assert.Single(export.WorkspaceMemberships);
        Assert.Equal(subjectWorkspaceMembershipId, workspaceMembership.Id);
        var participant = Assert.Single(export.Participants);
        Assert.Equal(subjectParticipantId, participant.Id);
        Assert.Equal(_subjectParticipantName, participant.DisplayName);
        var invitation = Assert.Single(export.Invitations);
        Assert.Equal(subjectInvitationId, invitation.Id);
        Assert.Equal(_subjectEmail, invitation.InvitedEmail);

        // Nothing belonging to another subject leaked anywhere in the response.
        Assert.DoesNotContain(_otherParticipantName, export.Participants.Select(p => p.DisplayName));
        Assert.DoesNotContain(_otherEmail, export.Invitations.Select(i => i.InvitedEmail));

        // The disclosure is audited by id only (actor == the subject who obtained it, exported subject), org-level.
        var audit = await SingleExportAuditAsync(factory);
        Assert.Equal(subjectProfileId, audit.ActorUserProfileId);
        Assert.Equal(subjectProfileId, audit.ResourceId);
        Assert.Equal(nameof(LiveCore.Api.IdentityAccess.UserProfile), audit.ResourceType);
        Assert.Null(audit.WorkspaceId);
    }

    // ---- the delivery carries no signed/public URL field --------------------

    [Fact]
    public async Task Download_delivers_the_personal_data_in_the_body_with_no_url_field()
    {
        // T4/T8: the export is delivered as the authorized response stream, NEVER as a public/static or signed
        // URL — no property in the response is a delivery URL. (The subject's OIDC issuer is legitimately a URL
        // VALUE — their personal datum — so the check is on PROPERTY NAMES, never on URL substrings in values.)
        await using var factory = new WorkspaceApiFactory();
        const string subjectSubject = "data-subject-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, _, job) = await SeedCompletedUserDataExportAsync(db, subjectSubject);
            exportJobId = job;
        });

        using var client = factory.CreateClientFor(subjectSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var name in AllPropertyNames(document.RootElement))
        {
            Assert.DoesNotContain("url", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- an Owner/Admin downloads it on the subject's behalf, tenant-scoped --

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Owner_or_admin_downloads_the_export_on_the_subjects_behalf_scoped_to_the_tenant(
        MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();

        const string adminSubject = "admin-a";
        Guid adminProfileId = Guid.Empty;
        Guid subjectProfileId = Guid.Empty;
        Guid exportJobId = Guid.Empty;

        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, callerRole);

            var subject = await db.AddUserAsync(_issuer, "data-subject-a", _subjectDisplayName, _subjectEmail);
            subjectProfileId = subject.Id;
            await db.AddOrganizationMemberAsync(orgA.Id, subject.Id, MembershipRole.Participant);
            var workspaceA = await db.AddWorkspaceAsync(orgA.Id, "alpha", "Alpha");
            await db.AddParticipantAsync(orgA.Id, workspaceA.Id, subject.Id, _subjectParticipantName);
            var job = await db.AddExportJobAsync(
                orgA.Id, workspaceA.Id, subject.Id, ExportScope.UserData, ExportJobStatus.Completed);
            exportJobId = job.Id;

            // CROSS-TENANT control (T5): the SAME subject is also active in org B; the org-A admin's export must
            // never disclose the subject's org-B activity.
            await db.AddOrganizationMemberAsync(orgB.Id, subject.Id, MembershipRole.Participant);
            var workspaceB = await db.AddWorkspaceAsync(orgB.Id, "beta", "Beta");
            await db.AddParticipantAsync(orgB.Id, workspaceB.Id, subject.Id, "Subject In B");
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.GetAsync($"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = await ReadExportAsync(response);

        // The subject's identity is disclosed to the controller acting on their behalf, but only org-A records.
        Assert.Equal(subjectProfileId, export.Subject.Id);
        var participant = Assert.Single(export.Participants);
        Assert.Equal(_subjectParticipantName, participant.DisplayName);
        Assert.DoesNotContain("Subject In B", export.Participants.Select(p => p.DisplayName));

        // Audited with the ADMIN as the actor and the subject as the resource.
        var audit = await SingleExportAuditAsync(factory);
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(subjectProfileId, audit.ResourceId);
    }

    // ---- 403: a tenant member who is neither the subject nor Owner/Admin -----

    [Theory]
    [MemberData(nameof(NonEntitledRoles))]
    public async Task Download_is_403_for_a_tenant_member_that_is_not_the_subject_or_owner_admin(
        MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            // A real Owner so the tenant is not ownerless.
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);

            // The data subject (a different user) and their completed user-data export.
            var (_, _, _, job) = await SeedCompletedUserDataExportAsync(db, "data-subject-a", org);
            exportJobId = job;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Nothing was disclosed: no export was audited.
        await AssertNoExportAuditedAsync(factory);
    }

    // ---- 404 hidden: a user-data export in another tenant (T5) --------------

    [Fact]
    public async Task Download_is_404_for_a_user_data_export_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the export lives in org B. Addressing it through org A's slug is
        // hidden as 404 — the export is loaded tenant-scoped, so it is never reachable through another tenant.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid exportInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);

            var orgB = await db.AddOrganizationAsync(_orgB);
            var (_, _, _, job) = await SeedCompletedUserDataExportAsync(db, "data-subject-b", orgB);
            exportInB = job;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportInB}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoExportAuditedAsync(factory);
    }

    // ---- 409: the export has not been produced yet --------------------------

    [Fact]
    public async Task Download_is_409_when_the_user_data_export_is_not_yet_produced()
    {
        // A still-pending user-data export (the producer has not run) has no retrievable personal data yet, so an
        // authorized caller (the subject) is rejected 409 — only ever disclosed to an authorized caller.
        await using var factory = new WorkspaceApiFactory();
        const string subjectSubject = "data-subject-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
            await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
            var workspace = await db.AddWorkspaceAsync(org.Id, "alpha", "Alpha");
            var job = await db.AddExportJobAsync(
                org.Id, workspace.Id, subject.Id, ExportScope.UserData, ExportJobStatus.Pending);
            exportJobId = job.Id;
        });

        using var client = factory.CreateClientFor(subjectSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoExportAuditedAsync(factory);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Runs one real export-processing sweep (the producer) against the same database, exactly as the worker host
    /// runs it: all collaborators share the ONE scoped <see cref="LiveCoreDbContext"/>.
    /// </summary>
    private static async Task<ExportProcessingResult> RunExportProducerAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var service = new ExportProcessingService(
            new QueuedExportJobReader(db),
            new ExportJobRepository(db),
            new WorkspaceExportInventoryReader(db),
            new ExportManifestRepository(db),
            new ExportProcessingOptions(TimeSpan.FromHours(1), batchSize: 50),
            TimeProvider.System,
            NullLogger<ExportProcessingService>.Instance);
        return await service.ProcessQueuedExportsAsync(CancellationToken.None);
    }

    private static async Task<ExportJob?> FindExportJobAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid exportJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new ExportJobRepository(db)
            .FindByIdInOrganizationAsync(organizationId, exportJobId, CancellationToken.None);
    }

    /// <summary>
    /// Seeds, in <paramref name="organization"/> (a new org A when omitted), a data subject with one participant
    /// record and a COMPLETED user-data export job requested by that subject. Returns
    /// (organizationId, workspaceId, subjectProfileId, exportJobId).
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SubjectProfileId, Guid ExportJobId)> SeedCompletedUserDataExportAsync(
        LiveCoreDbContext db,
        string subjectSubject,
        Organization? organization = null)
    {
        var org = organization ?? await db.AddOrganizationAsync(_orgA);
        var subject = await db.AddUserAsync(_issuer, subjectSubject, _subjectDisplayName, _subjectEmail);
        await db.AddOrganizationMemberAsync(org.Id, subject.Id, MembershipRole.Participant);
        var workspace = await db.AddWorkspaceAsync(org.Id, $"ws-{subjectSubject}", "Workspace");
        await db.AddParticipantAsync(org.Id, workspace.Id, subject.Id, _subjectParticipantName);
        var job = await db.AddExportJobAsync(
            org.Id, workspace.Id, subject.Id, ExportScope.UserData, ExportJobStatus.Completed);
        return (org.Id, workspace.Id, subject.Id, job.Id);
    }

    private static async Task<AuditLogEntry> SingleExportAuditAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return Assert.Single(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
    }

    private static async Task AssertNoExportAuditedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.Empty(await context.AuditLogs.AsNoTracking()
            .Where(e => e.Action == AuditAction.PersonalDataExported)
            .ToListAsync());
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
