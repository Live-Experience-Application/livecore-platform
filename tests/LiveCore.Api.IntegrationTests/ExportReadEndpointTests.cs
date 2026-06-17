// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the export read/download endpoint (CORE-EXP-001, the "Vertical Authoring and Read
/// API Completeness" epic, <c>GET /api/v1/exports/{exportId}</c>, csv/api_routes.csv roles "Owner,Admin,Host").
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication
/// scheme + EF Core SQLite, foreign keys ON), so the documented request flow (authentication → tenant context
/// resolver → endpoint → object-level authorization → role-based projection) is exercised end-to-end exactly as
/// in production.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md "Export workspace"; threats T1/T5/T8):
/// <list type="bullet">
///   <item>POSITIVE: a completed workspace export's artifact (its produced manifest) is downloadable by
///   Host/Owner/Admin, returned as the FULL host view with the per-kind inventory.</item>
///   <item>AUTHORIZE-BEFORE-PRODUCE: a non-authoring role is 403 even when the export is incomplete (the role
///   gate runs BEFORE any artifact/state is produced or disclosed); a participant-scoped (audience) caller is
///   denied outright, so it never receives host-only export content.</item>
///   <item>DELIVERY (threat T4): the artifact is delivered in the authorized response body — never a
///   public/static URL; the response carries no URL field.</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a foreign tenant, a token-claim mismatch, an
///   unknown export, and a non-member of the export's workspace are ALL hidden as 404; a missing
///   organizationSlug is 400; a malformed export id is 404.</item>
///   <item>STATE: an incomplete (pending/running), failed, or manifest-less export is 409 (rejected) — only
///   ever disclosed to an authorized downloader.</item>
///   <item>ISOLATION (threat T5): one tenant's export is never returned through another tenant's slug.</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// authorized/denied sets, never an ordering comparison. All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class ExportReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The "Export workspace" roles authorized to download a completed export artifact
    /// (docs/06_AUTHORIZATION_MATRIX.md; ExportAccessPolicy.CanDownloadExport).
    /// </summary>
    public static TheoryData<MembershipRole> DownloaderRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
    ];

    /// <summary>
    /// The roles DENIED the export download fail-closed (403): CoHost (matrix "no"), the audience roles
    /// Participant/Observer (matrix "no"), and the deployment-optional Auditor (a metadata role, not an
    /// export-artifact downloader).
    /// </summary>
    public static TheoryData<MembershipRole> DeniedRoles =>
    [
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // ---- 401 / 400 / malformed id ------------------------------------------

    [Fact]
    public async Task Read_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"/api/v1/exports/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var exportId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, _, export) = await SeedCompletedExportAsync(db, subject, MembershipRole.Host);
            exportId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/exports/{exportId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Read_with_a_malformed_or_empty_export_id_is_404(string rawExportId)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{rawExportId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 200: the authorized downloader roles get the full artifact --------

    [Theory]
    [MemberData(nameof(DownloaderRoles))]
    public async Task Read_returns_the_completed_export_artifact_for_a_downloader_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid exportJobId = Guid.Empty;
        Guid manifestId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (org, ws, export, manifest) = await SeedCompletedExportWithManifestAsync(db, subject, role);
            organizationId = org;
            workspaceId = ws;
            exportJobId = export;
            manifestId = manifest;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportArtifactDto>(_json);
        Assert.NotNull(body);

        // The FULL host view: the artifact's identity, boundaries, scope and the per-kind inventory.
        Assert.Equal(manifestId, body.Id);
        Assert.Equal(exportJobId, body.ExportJobId);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal(nameof(ExportScope.Workspace), body.Scope);
        Assert.Equal(1, body.ManifestVersion);
        Assert.NotNull(body.GeneratedAt);
        Assert.NotNull(body.Entries);
        Assert.Equal(6, body.TotalItemCount);

        // The inventory carries per-kind COUNTS by stable name — never any exported content (threats T7/T8).
        var sessions = Assert.Single(body.Entries!, entry => entry.Kind == nameof(ExportResourceKind.Session));
        Assert.Equal(2, sessions.ItemCount);
    }

    [Fact]
    public async Task Read_delivers_the_artifact_in_the_body_not_as_a_url()
    {
        // T4: the export is delivered as an authorized stream (the response body), NEVER as a public/static or
        // signed URL — the response carries no URL of any kind.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedCompletedExportWithManifestAsync(db, subject, MembershipRole.Host);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("http://", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 403: a non-authoring role is denied -------------------------------

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public async Task Read_is_403_for_a_non_authoring_role(MembershipRole role)
    {
        // A known member of the export's workspace whose role is not an authorized downloader is 403 — never the
        // artifact. A participant-scoped (audience) caller is denied outright, so it never receives any host-only
        // export content (the "non-authoring role 403" / "participant-scoped export excludes host-only content"
        // criteria).
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedCompletedExportWithManifestAsync(db, subject, role);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Read_authorizes_before_disclosing_an_incomplete_exports_state()
    {
        // The download is authorized BEFORE any artifact/state is produced: a non-authoring caller asking for an
        // INCOMPLETE export still gets 403 (the role gate), never the 409 an authorized caller would see — so the
        // export's state is never disclosed to an unauthorized caller.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "participant-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedExportAsync(
                db, subject, MembershipRole.Participant, ExportJobStatus.Running);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 409: incomplete / failed / manifest-less export -------------------

    [Theory]
    [InlineData(ExportJobStatus.Pending)]
    [InlineData(ExportJobStatus.Running)]
    [InlineData(ExportJobStatus.Failed)]
    public async Task Read_is_409_when_the_export_is_not_a_completed_artifact(ExportJobStatus status)
    {
        // An incomplete (pending/running) or failed export has no retrievable artifact, so an authorized
        // downloader is rejected 409 (the "expired or incomplete export is rejected" criterion).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedExportAsync(db, subject, MembershipRole.Host, status);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Read_surfaces_a_dead_lettered_export_as_failed_to_an_authorized_requester()
    {
        // CORE-RES-002: a worker-dead-lettered export (terminal Failed) is reported to an authorized requester
        // with a DISTINCT 409 detail saying it FAILED — so a requester polling the route learns the export
        // permanently failed instead of mistaking it for one still in progress (or waiting on a job stuck Pending
        // forever). It is the 409 "surfaces as failed to its requester" acceptance criterion.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid exportId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedExportAsync(db, subject, MembershipRole.Host, ExportJobStatus.Failed);
            exportId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(_json);
        Assert.NotNull(problem);
        // The detail tells the requester the export FAILED (distinct from the generic "not available" a
        // still-processing export returns), and it never leaks the internal failure reason or any content.
        Assert.Contains("failed", problem.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_hides_a_dead_lettered_export_from_a_non_member_as_404_not_as_failed()
    {
        // The distinct "failed" disclosure is POST-authorization: a caller who is not a member of the export's
        // workspace still gets 404 (existence hidden), never the failed-state 409 — so dead-lettering never leaks
        // the export's state to an unauthorized caller (threats T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid exportId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var outsider = await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, outsider.Id, MembershipRole.Owner);

            // A separate workspace the outsider is NOT a member of, holding a dead-lettered (Failed) export.
            var creator = await db.AddUserAsync(_issuer, "creator");
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, creator.Id, MembershipRole.Host);
            var job = await db.AddExportJobAsync(org.Id, ws.Id, creator.Id, ExportScope.Workspace, ExportJobStatus.Failed);
            exportId = job.Id;
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_409_when_a_completed_export_has_no_manifest()
    {
        // Defensive: a completed job always has exactly one manifest (the worker commits them atomically), but if
        // the artifact is somehow absent it is not retrievable, so the route fails closed with 409 rather than an
        // empty body.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // A completed job WITHOUT seeding its manifest.
            var (_, _, export, _) = await SeedExportAsync(db, subject, MembershipRole.Host, ExportJobStatus.Completed);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- 404 hidden: non-member / foreign tenant / unknown -----------------

    [Fact]
    public async Task Read_is_404_for_a_tenant_member_who_is_not_a_member_of_the_exports_workspace()
    {
        // The caller is an org member (so the tenant resolves) but holds NO membership in the export's workspace,
        // so the export — and its very existence — is hidden as 404, never 403 (T1/T5).
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var outsider = await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, outsider.Id, MembershipRole.Owner);

            // A separate workspace the outsider is NOT a member of, holding the completed export.
            var creator = await db.AddUserAsync(_issuer, "creator");
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, creator.Id, MembershipRole.Host);
            var job = await db.AddExportJobAsync(org.Id, ws.Id, creator.Id);
            await db.AddExportManifestAsync(job);
            exportJobId = job.Id;
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is a full member of org A and names org A in the query, but the token asserts only org
        // B, so tenant resolution denies and the export is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var (_, _, export, _) = await SeedCompletedExportWithManifestAsync(db, subject, MembershipRole.Host);
            exportJobId = export;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync(
            $"/api/v1/exports/{exportJobId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_never_returns_another_tenants_export_through_this_tenants_slug()
    {
        // T5 tenant isolation: the caller is a full Host of BOTH tenants. Org B holds a completed export; the
        // caller asks for that export id but scoped to org A — the export is not in org A, so it is hidden as 404.
        // Reading it scoped to org B succeeds.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-both";
        Guid exportInB = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);

            var orgA = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Host);
            var wsA = await db.AddWorkspaceAsync(orgA.Id, "a-show", "A Show");
            await db.AddWorkspaceMemberAsync(orgA.Id, wsA.Id, user.Id, MembershipRole.Host);

            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Host);
            var wsB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, wsB.Id, user.Id, MembershipRole.Host);
            var job = await db.AddExportJobAsync(orgB.Id, wsB.Id, user.Id);
            await db.AddExportManifestAsync(job);
            exportInB = job.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        // Org B's export is NOT reachable through org A's slug.
        var crossTenant = await client.GetAsync(
            $"/api/v1/exports/{exportInB}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        // It IS reachable through its own tenant's slug.
        var sameTenant = await client.GetAsync(
            $"/api/v1/exports/{exportInB}?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_export_in_an_entitled_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/exports/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Seeds, in org A, a user that is both an organization member (so tenant resolution succeeds) and a
    /// workspace member with <paramref name="role"/> (which drives the download gate), a workspace and an export
    /// job in the given <paramref name="status"/> (no manifest). Returns (organizationId, workspaceId,
    /// exportJobId, userProfileId).
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid WorkspaceId, Guid ExportJobId, Guid UserProfileId)> SeedExportAsync(
        LiveCoreDbContext db,
        string subject,
        MembershipRole role,
        ExportJobStatus status)
    {
        var user = await db.AddUserAsync(_issuer, subject);
        var org = await db.AddOrganizationAsync(_orgA);
        await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
        await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, role);
        var job = await db.AddExportJobAsync(org.Id, ws.Id, user.Id, ExportScope.Workspace, status);
        return (org.Id, ws.Id, job.Id, user.Id);
    }

    /// <summary>
    /// Seeds a COMPLETED workspace export job (no manifest yet). Returns (organizationId, workspaceId,
    /// exportJobId, userProfileId).
    /// </summary>
    private static Task<(Guid OrganizationId, Guid WorkspaceId, Guid ExportJobId, Guid UserProfileId)> SeedCompletedExportAsync(
        LiveCoreDbContext db,
        string subject,
        MembershipRole role)
        => SeedExportAsync(db, subject, role, ExportJobStatus.Completed);

    /// <summary>
    /// Seeds a COMPLETED workspace export job AND its produced manifest (the full downloadable artifact).
    /// Returns (organizationId, workspaceId, exportJobId, manifestId).
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid WorkspaceId, Guid ExportJobId, Guid ManifestId)> SeedCompletedExportWithManifestAsync(
        LiveCoreDbContext db,
        string subject,
        MembershipRole role)
    {
        var user = await db.AddUserAsync(_issuer, subject);
        var org = await db.AddOrganizationAsync(_orgA);
        await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
        await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, role);
        var job = await db.AddExportJobAsync(org.Id, ws.Id, user.Id);
        var manifest = await db.AddExportManifestAsync(job);
        return (org.Id, ws.Id, job.Id, manifest.Id);
    }

    /// <summary>
    /// A lenient view of the export artifact response that carries BOTH projections' fields, all nullable, so a
    /// test can assert which fields the server actually returned. The full host view populates all of them; the
    /// audience summary view populates only <c>Id</c> and <c>Scope</c>. The <c>Scope</c>/<c>Kind</c> enums are
    /// deserialized as their stable string names (the wire contract; docs/08).
    /// </summary>
    private sealed record ExportArtifactDto(
        Guid Id,
        Guid? ExportJobId,
        Guid? OrganizationId,
        Guid? WorkspaceId,
        string? Scope,
        int? ManifestVersion,
        DateTimeOffset? GeneratedAt,
        ExportEntryDto[]? Entries,
        int? TotalItemCount);

    private sealed record ExportEntryDto(string Kind, int ItemCount);

    /// <summary>A minimal view of an RFC 7807 Problem Details body for asserting the failure disclosure.</summary>
    private sealed record ProblemDto(string? Detail, string? Code);
}
