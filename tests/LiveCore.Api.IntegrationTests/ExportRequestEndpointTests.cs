// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the async export-REQUEST endpoint (CORE-EXP-003, the "Async Export Request
/// Lifecycle" epic, <c>POST /api/v1/workspaces/{workspaceId}/exports</c>, csv/api_routes.csv roles
/// "Owner,Admin,Host"). They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>
/// (test authentication scheme + EF Core SQLite, foreign keys ON), so the documented request flow
/// (authentication → tenant context resolver → endpoint → object-level authorization → idempotent create) is
/// exercised end-to-end exactly as in production.
///
/// <para>
/// The request route closes ARC-GAP-119: before it the read route <c>GET /api/v1/exports/{exportId}</c> could
/// never be given a real export id because no route minted one. These tests prove the WHOLE lifecycle — request
/// then read — together, running the SAME worker export producer (CORE-EXP-002,
/// <see cref="ExportProcessingService"/>) in-test that the deployed worker runs on its interval, exactly as
/// <see cref="WorkerInclusiveJourneyEndpointTests"/> wires it.
/// </para>
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md "Export workspace"; threats T1/T5/T8):
/// <list type="bullet">
///   <item>POSITIVE: a Host requests a workspace export (201 with an exportId), the producer drains the queued
///   job into a manifest, and the read route then returns that manifest (200) — the request-then-read
///   lifecycle.</item>
///   <item>IDEMPOTENCY (CORE-DX-004): a retry of the create under the SAME <c>Idempotency-Key</c> returns the
///   ORIGINAL exportId (200) and creates NO second job.</item>
///   <item>NEGATIVE (fail-closed): a non-Owner/Admin/Host caller is a fail-closed 404 (the object-level rule of
///   the existing GET — a non-member never learns the workspace exists); a known member with an insufficient
///   role is 403 and mints nothing; a foreign-tenant token mismatch is 404; an unauthenticated caller is 401; a
///   missing organizationSlug is 400; a malformed Idempotency-Key is 400 (after authorization).</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so the role sweeps are explicit enumerations of the
/// authorized/denied sets, never an ordering comparison. All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class ExportRequestEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The "Export workspace" roles authorized to REQUEST a workspace export (ExportAccessPolicy.CanRequestExport).</summary>
    public static TheoryData<MembershipRole> RequesterRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
    ];

    /// <summary>
    /// The roles DENIED the export request fail-closed (403): CoHost (matrix "no"), the audience roles
    /// Participant/Observer (matrix "no"), and the deployment-optional Auditor (a metadata role, not an
    /// export requester).
    /// </summary>
    public static TheoryData<MembershipRole> DeniedRoles =>
    [
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    // ---- POSITIVE: request -> producer drains -> read returns the manifest ----

    [Theory]
    [MemberData(nameof(RequesterRoles))]
    public async Task Request_returns_201_with_a_pending_export_for_an_authorized_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid userProfileId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, userProfileId) = await SeedWorkspaceMemberAsync(db, subject, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await RequestExportAsync(client, workspaceId, _orgA);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        Assert.NotNull(body);

        // The minted job: a fresh Pending workspace export requested by the caller, in the caller's tenant/workspace.
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal(userProfileId, body.RequestedByUserProfileId);
        Assert.Equal(nameof(ExportScope.Workspace), body.Scope);
        Assert.Equal(nameof(ExportJobStatus.Pending), body.Status);
        Assert.Null(body.FailureReason);

        // Exactly one job was minted and it is persisted Pending (the worker has not run yet).
        var jobs = await ListJobsAsync(factory, organizationId, workspaceId);
        var job = Assert.Single(jobs);
        Assert.Equal(body.Id, job.Id);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(ExportScope.Workspace, job.Scope);
    }

    [Fact]
    public async Task Requested_export_is_drained_by_the_producer_and_then_readable_as_a_manifest()
    {
        // The full lifecycle the story closes (ARC-GAP-119): request -> the worker export producer drains the
        // queued job into a manifest -> GET /api/v1/exports/{exportId} returns that manifest. Before this route
        // the read could never be given a real id.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Request the export (201 with the exportId).
        var requestResponse = await RequestExportAsync(client, workspaceId, _orgA);
        Assert.Equal(HttpStatusCode.Created, requestResponse.StatusCode);
        var requested = await requestResponse.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        Assert.NotNull(requested);
        var exportId = requested.Id;

        // Until the producer runs, the export has no retrievable artifact yet (a Pending job is a 409).
        var beforeDrain = await client.GetAsync($"/api/v1/exports/{exportId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.Conflict, beforeDrain.StatusCode);

        // Run the SAME worker producer the deployed worker runs (CORE-EXP-002): it claims, starts, inventories
        // and completes the queued job, producing its manifest.
        var sweep = await RunExportProducerAsync(factory);
        Assert.Equal(1, sweep.Examined);
        Assert.Equal(1, sweep.Processed);

        // The export is now readable as its produced manifest (200), keyed by the real id the request minted.
        var afterDrain = await client.GetAsync($"/api/v1/exports/{exportId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, afterDrain.StatusCode);
        var manifest = await afterDrain.Content.ReadFromJsonAsync<ExportArtifactDto>(_json);
        Assert.NotNull(manifest);
        Assert.Equal(exportId, manifest.ExportJobId);
        Assert.Equal(nameof(ExportScope.Workspace), manifest.Scope);
        Assert.NotNull(manifest.TotalItemCount);

        // The producing job settled Completed.
        var jobs = await ListJobsAsync(factory, organizationId, workspaceId);
        var job = Assert.Single(jobs);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
    }

    // ---- IDEMPOTENCY (CORE-DX-004) ----

    [Fact]
    public async Task Retry_under_the_same_idempotency_key_returns_the_original_export_and_creates_no_second_job()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        const string key = "export-key-1";

        // FIRST request under the key: 201 with the minted export id.
        var first = await RequestExportAsync(client, workspaceId, _orgA, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        Assert.NotNull(firstBody);

        // RETRY under the SAME key: 200 with the ORIGINAL export id, no second job.
        var retry = await RequestExportAsync(client, workspaceId, _orgA, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryBody = await retry.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        Assert.NotNull(retryBody);
        Assert.Equal(firstBody.Id, retryBody.Id);

        // EXACTLY ONE export job exists for the workspace — the retry created none.
        var jobs = await ListJobsAsync(factory, organizationId, workspaceId);
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_different_idempotency_key_mints_a_second_export()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var first = await RequestExportAsync(client, workspaceId, _orgA, "key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await RequestExportAsync(client, workspaceId, _orgA, "key-2");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        var secondBody = await second.Content.ReadFromJsonAsync<ExportJobDto>(_json);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.NotEqual(firstBody.Id, secondBody.Id);

        var jobs = await ListJobsAsync(factory, organizationId, workspaceId);
        Assert.Equal(2, jobs.Count);
    }

    // ---- NEGATIVE authorization (fail-closed) ----

    [Fact]
    public async Task Request_is_404_for_a_tenant_member_who_is_not_a_member_of_the_workspace()
    {
        // THE required negative case: a non-Owner/Admin/Host caller is a fail-closed 404. The caller is an org
        // member (so the tenant resolves) but holds NO membership in the target workspace, so the workspace — and
        // its very existence — is hidden as 404, never 403 (the same object-level rule as the existing GET; T1/T5),
        // and no job is minted.
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var outsider = await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, outsider.Id, MembershipRole.Owner);

            // A separate workspace the outsider is NOT a member of.
            var creator = await db.AddUserAsync(_issuer, "creator");
            var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, ws.Id, creator.Id, MembershipRole.Host);
            organizationId = org.Id;
            workspaceId = ws.Id;
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await RequestExportAsync(client, workspaceId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ListJobsAsync(factory, organizationId, workspaceId));
    }

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public async Task Request_is_403_for_a_known_member_with_an_insufficient_role(MembershipRole role)
    {
        // A known member of the workspace whose role is not an authorized requester is 403 (authorized to know the
        // workspace exists, but not to request its export) — the SAME role gate as the export read. No job is
        // minted.
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, role);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await RequestExportAsync(client, workspaceId, _orgA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ListJobsAsync(factory, organizationId, workspaceId));
    }

    [Fact]
    public async Task Request_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is a full Host of org A and names org A in the body, but the token asserts only org B, so
        // tenant resolution denies and the workspace is hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await RequestExportAsync(client, workspaceId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ListJobsAsync(factory, organizationId, workspaceId));
    }

    [Fact]
    public async Task Request_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await RequestExportAsync(client, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (_, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/exports")
        {
            Content = JsonContent.Create(new { }, options: _json),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_with_a_malformed_idempotency_key_is_400_after_authorization()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid organizationId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            (organizationId, workspaceId, _) = await SeedWorkspaceMemberAsync(db, subject, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // A key over the 256-character bound is malformed; it is rejected 400 and mints nothing.
        var response = await RequestExportAsync(client, workspaceId, _orgA, new string('k', 300));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListJobsAsync(factory, organizationId, workspaceId));
    }

    // ---- helpers ----

    /// <summary>
    /// Seeds, in org A, a user that is both an organization member and a workspace member with
    /// <paramref name="role"/> (which drives the request gate), plus the workspace. Returns (organizationId,
    /// workspaceId, userProfileId).
    /// </summary>
    private static async Task<(Guid OrganizationId, Guid WorkspaceId, Guid UserProfileId)> SeedWorkspaceMemberAsync(
        LiveCoreDbContext db,
        string subject,
        MembershipRole role)
    {
        var user = await db.AddUserAsync(_issuer, subject);
        var org = await db.AddOrganizationAsync(_orgA);
        await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
        var ws = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
        await db.AddWorkspaceMemberAsync(org.Id, ws.Id, user.Id, role);
        return (org.Id, ws.Id, user.Id);
    }

    private static async Task<HttpResponseMessage> RequestExportAsync(
        HttpClient client,
        Guid workspaceId,
        string organizationSlug,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/exports")
        {
            Content = JsonContent.Create(new { organizationSlug }, options: _json),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Runs one real export-processing sweep against the same database, constructing the service over a scoped
    /// <see cref="LiveCoreDbContext"/> with its real repositories — the same wiring the worker host registers and
    /// <see cref="WorkerInclusiveJourneyEndpointTests"/> uses.
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

    private static async Task<IReadOnlyList<ExportJob>> ListJobsAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new ExportJobRepository(db).ListByWorkspaceAsync(organizationId, workspaceId, CancellationToken.None);
    }

    /// <summary>The export-request response shape (the full host export-job view; the wire enums are stable names).</summary>
    private sealed record ExportJobDto(
        Guid Id,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid? RequestedByUserProfileId,
        string Scope,
        string Status,
        string? FailureReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>A lenient view of the produced export artifact (its manifest) for the read-after-drain assertion.</summary>
    private sealed record ExportArtifactDto(
        Guid? Id,
        Guid? ExportJobId,
        string? Scope,
        int? TotalItemCount);
}
