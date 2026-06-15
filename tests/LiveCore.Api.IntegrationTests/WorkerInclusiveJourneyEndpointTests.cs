using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// THE WORKER-INCLUSIVE END-TO-END COMPOSITION test (CORE-E2E-003, the "End-to-End Scenarios" epic). The
/// worker services (<see cref="RecapGenerationService"/> CORE-JOB-001 and <see cref="ExportProcessingService"/>
/// CORE-JOB-002) are thoroughly unit-tested in ISOLATION over in-memory fakes, but the
/// API-&gt;worker-&gt;persistence chain is never exercised TOGETHER: nothing proves that a session driven to
/// completion through the public API leaves a database the real worker jobs then read, recap and export
/// consistently. This test closes that async-composition gap.
///
/// HOW IT COMPOSES (the story note). The session lifecycle is driven through the WRITE endpoints over real HTTP
/// (<see cref="WorkspaceApiFactory"/>: test authentication scheme + EF Core SQLite, foreign keys ON), exactly
/// as CORE-E2E-001 does. Then the recap-generation and export-processing services are invoked DIRECTLY against
/// the same database — constructed over a scoped <see cref="LiveCoreDbContext"/> resolved from the running
/// host (<c>WorkspaceApiFactory.Services</c>) with their real repositories, exactly as the worker host
/// wires them and as the worker unit tests construct the service — so the jobs read the rows the API wrote.
///
/// WHAT IS PROVEN:
/// <list type="bullet">
///   <item>RECAP (CORE-RCP-001/002): ending a session through the API then running the recap job produces
///   EXACTLY ONE system recap for the ended session, whose body is composed from — and consistent with — that
///   session's own append-only <c>session_events</c> stream (the event counts the recap reports equal the
///   counts in the persisted stream), and which carries NO hidden content (no revealed resource id, no content
///   body) — the visibility-safe projection (threat T7).</item>
///   <item>EXPORT (CORE-JOB-002): requesting a workspace export then running the export job produces a manifest
///   scoped to the requesting tenant/workspace whose per-kind inventory counts EXACTLY the workspace's own
///   resources and never a concurrent foreign tenant's (threat T5 "no over-export").</item>
///   <item>VISIBILITY/AUTHORIZATION, FAIL-CLOSED: recaps and exports have no HTTP read endpoint by design, so
///   authorization is asserted at the projection layer — the host-only recap body and the export inventory are
///   stripped for an audience role (<see cref="RecapProjection"/> / <see cref="ExportManifestProjection"/> fail
///   closed) — and at the persistence layer — a FOREIGN tenant's scoped read returns nothing (threats
///   T1/T5).</item>
///   <item>IDEMPOTENT: re-running both jobs against the same database produces NO duplicate recap and NO
///   duplicate manifest.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class WorkerInclusiveJourneyEndpointTests
{
    private const string _issuer = TestAuthenticationHandler.DefaultIssuer;
    private const string _orgSlug = "northwind-labs";
    private const string _foreignOrgSlug = "acme-co";

    private const string _hostSubject = "host-user";
    private const string _participantASubject = "participant-a";
    private const string _participantBSubject = "participant-b";

    // The content-block bodies are HOST content; the over-export assertions prove neither ever reaches the
    // recap body (the recap carries counts and the timeline only, never resolved content — threat T7).
    private const string _privateBlockBody = "A note meant for one viewer.";
    private const string _hiddenBlockBody = "A note shown then withdrawn.";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // End a session via the API -> run the recap job -> exactly one recap is
    // produced reflecting the session.
    // =====================================================================

    [Fact]
    public async Task Ending_a_session_then_running_the_recap_job_produces_exactly_one_recap_reflecting_the_session()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunJourneyAsync(factory);

        // Run the real recap-generation sweep against the same database the API just wrote.
        var result = await RunRecapJobAsync(factory);

        // Exactly one recap was generated this sweep. The CONCURRENT still-live session 2 is NOT eligible
        // (eligibility is "ended and not yet recapped"), so the sweep recaps only the one ended session.
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Generated);
        Assert.Equal(0, result.Deduplicated);
        Assert.Equal(0, result.Failed);

        // EXACTLY ONE recap exists for the ended session, scoped to its own tenant/workspace/session, and it is
        // SYSTEM-produced (no user) — the worker, not a host, generated it.
        var recaps = await ListRecapsAsync(factory, journey.OrganizationId, journey.WorkspaceId, journey.EndedSessionId);
        var recap = Assert.Single(recaps);
        Assert.Equal(journey.OrganizationId, recap.OrganizationId);
        Assert.Equal(journey.WorkspaceId, recap.WorkspaceId);
        Assert.Equal(journey.EndedSessionId, recap.SessionId);
        Assert.Null(recap.GeneratedByUserProfileId);

        // The concurrent live session never received a recap.
        var liveSessionRecaps = await ListRecapsAsync(factory, journey.OrganizationId, journey.WorkspaceId, journey.LiveSessionId);
        Assert.Empty(liveSessionRecaps);

        // CONSISTENT WITH THE EVENT STREAM (CORE-RCP-002): the recap body reports exactly the counts that are in
        // the session's own persisted append-only stream. Each count is derived INDEPENDENTLY from the stream
        // here (not from the composer), so this proves the worker composed the recap from the real events the
        // API produced. The substrings omit the trailing plural "s" so they match either singular or plural.
        var stream = await ReadSessionStreamAsync(factory, journey.OrganizationId, journey.EndedSessionId);
        Assert.NotEmpty(stream);
        Assert.Contains($"recorded {stream.Count} event", recap.Summary);
        Assert.Contains($"{Count(stream, SessionEventTypes.SceneActivated)} scene activation", recap.Summary);
        Assert.Contains($"{Count(stream, SessionEventTypes.ContentRevealed)} content reveal", recap.Summary);
        Assert.Contains($"{Count(stream, SessionEventTypes.ContentHidden)} content hide", recap.Summary);
        Assert.Contains($"{Count(stream, SessionEventTypes.ParticipantJoined)} participant join", recap.Summary);
        Assert.Contains("Live timeline ran from", recap.Summary);

        // NO OVER-EXPORT OF HIDDEN DATA (threat T7): the host-only recap body carries counts and the timeline
        // only — never a revealed/hidden resource id and never any content block body, including the block that
        // was revealed privately to a single participant and the one that was revealed then hidden.
        Assert.DoesNotContain(_privateBlockBody, recap.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(_hiddenBlockBody, recap.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(journey.SceneId.ToString(), recap.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(journey.PrivateContentBlockId.ToString(), recap.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(journey.HiddenContentBlockId.ToString(), recap.Summary, StringComparison.Ordinal);

        // The recap body is host content: the role-based projection gives the host the FULL view (with the body)
        // and an AUDIENCE role the stripped summary view (without it) — fail-closed (threat T2).
        Assert.IsType<RecapView>(RecapProjection.Project(recap, MembershipRole.Host));
        Assert.IsType<RecapSummaryView>(RecapProjection.Project(recap, MembershipRole.Participant));
    }

    // =====================================================================
    // Request + run an export -> the manifest is produced and tenant/visibility
    // scoped.
    // =====================================================================

    [Fact]
    public async Task Requesting_then_running_an_export_produces_a_tenant_scoped_manifest_consistent_with_the_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunJourneyAsync(factory);

        // A concurrent FOREIGN tenant with its OWN workspace and several sessions. If the export inventory were
        // not tenant/workspace-scoped, these would inflate the host workspace's session count (threat T5).
        await factory.SeedAsync(async db =>
        {
            var foreignOrg = await db.AddOrganizationAsync(_foreignOrgSlug);
            var foreignWorkspace = await db.AddWorkspaceAsync(foreignOrg.Id, "rival-show", "Rival Show");
            for (var i = 0; i < 5; i++)
            {
                await db.AddSessionAsync(foreignOrg.Id, foreignWorkspace.Id, $"Foreign {i}", SessionStatus.Prepared);
            }
        });

        // Request a workspace export for the host's tenant (no HTTP route by design — request it through the
        // existing aggregate + repository, exactly as a future endpoint would), then run the real processing
        // sweep against the same database.
        var exportJobId = await RequestWorkspaceExportAsync(
            factory, journey.OrganizationId, journey.WorkspaceId, journey.HostUserProfileId);

        var result = await RunExportJobAsync(factory);

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);

        // A manifest was produced, scoped to the REQUESTING tenant/workspace and to the workspace export scope.
        var manifest = await FindManifestAsync(factory, journey.OrganizationId, journey.WorkspaceId, exportJobId);
        Assert.NotNull(manifest);
        Assert.Equal(exportJobId, manifest.ExportJobId);
        Assert.Equal(journey.OrganizationId, manifest.OrganizationId);
        Assert.Equal(journey.WorkspaceId, manifest.WorkspaceId);
        Assert.Equal(ExportScope.Workspace, manifest.Scope);

        // The producing job settled Completed (its guarded Pending->Running->Completed transitions committed
        // atomically with the manifest).
        var job = await FindExportJobAsync(factory, journey.OrganizationId, journey.WorkspaceId, exportJobId);
        Assert.NotNull(job);
        Assert.Equal(ExportJobStatus.Completed, job.Status);

        // TENANT/WORKSPACE SCOPED, NO OVER-EXPORT (threat T5): the per-kind inventory counts EXACTLY the host
        // workspace's own resources — the two sessions (the ended one + the concurrent live one), the one scene,
        // the two content blocks and the two participants — and NEVER the foreign tenant's five sessions.
        Assert.Equal(6, manifest.Entries.Count);
        Assert.Equal(2, EntryCount(manifest, ExportResourceKind.Session));
        Assert.Equal(1, EntryCount(manifest, ExportResourceKind.Scene));
        Assert.Equal(2, EntryCount(manifest, ExportResourceKind.ContentBlock));
        Assert.Equal(0, EntryCount(manifest, ExportResourceKind.Entity));
        Assert.Equal(2, EntryCount(manifest, ExportResourceKind.Participant));
        Assert.Equal(0, EntryCount(manifest, ExportResourceKind.Asset));
        Assert.Equal(7, manifest.TotalItemCount);

        // The per-kind inventory reveals the shape of the workspace's host content, so the role-based projection
        // gives a host/metadata role the FULL view (with the entries) and an AUDIENCE role the stripped summary
        // view (without them) — fail-closed (threat T8 "export role-based projection").
        Assert.IsType<ExportManifestView>(ExportManifestProjection.Project(manifest, MembershipRole.Host));
        Assert.IsType<ExportManifestSummaryView>(ExportManifestProjection.Project(manifest, MembershipRole.Observer));
    }

    // =====================================================================
    // Re-running the jobs produces no duplicates (idempotency).
    // =====================================================================

    [Fact]
    public async Task Re_running_the_recap_and_export_jobs_produces_no_duplicates()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunJourneyAsync(factory);

        var exportJobId = await RequestWorkspaceExportAsync(
            factory, journey.OrganizationId, journey.WorkspaceId, journey.HostUserProfileId);

        // First sweep of each job: produce the artifacts.
        var firstRecap = await RunRecapJobAsync(factory);
        var firstExport = await RunExportJobAsync(factory);
        Assert.Equal(1, firstRecap.Generated);
        Assert.Equal(1, firstExport.Processed);

        // Second sweep of each job against the same database: the ended session now has a recap (no longer
        // eligible) and the export job is now terminal (no longer queued), so neither produces anything more.
        var secondRecap = await RunRecapJobAsync(factory);
        var secondExport = await RunExportJobAsync(factory);
        Assert.Equal(0, secondRecap.Examined);
        Assert.Equal(0, secondRecap.Generated);
        Assert.Equal(0, secondExport.Examined);
        Assert.Equal(0, secondExport.Processed);

        // Exactly one recap and exactly one manifest survive the re-runs — no duplicates.
        var recaps = await ListRecapsAsync(factory, journey.OrganizationId, journey.WorkspaceId, journey.EndedSessionId);
        Assert.Single(recaps);

        var manifest = await FindManifestAsync(factory, journey.OrganizationId, journey.WorkspaceId, exportJobId);
        Assert.NotNull(manifest);
    }

    // =====================================================================
    // NEGATIVE authorization: a foreign tenant and an unauthorized role cannot
    // reach the recap or the manifest — fail-closed (threats T1/T5).
    // =====================================================================

    [Fact]
    public async Task Foreign_tenant_and_unauthorized_role_cannot_reach_the_recap_or_manifest_fail_closed()
    {
        await using var factory = new WorkspaceApiFactory();
        var journey = await RunJourneyAsync(factory);

        // A real, separate tenant addressing the journey's artifacts.
        Guid foreignOrganizationId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var foreignOrg = await db.AddOrganizationAsync(_foreignOrgSlug);
            foreignOrganizationId = foreignOrg.Id;
        });

        var exportJobId = await RequestWorkspaceExportAsync(
            factory, journey.OrganizationId, journey.WorkspaceId, journey.HostUserProfileId);
        await RunRecapJobAsync(factory);
        await RunExportJobAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var recaps = new RecapRepository(db);
        var manifests = new ExportManifestRepository(db);
        var exportJobs = new ExportJobRepository(db);

        // The in-tenant reads succeed — the artifacts exist and are reachable through their OWN tenant.
        Assert.Single(await recaps.ListBySessionAsync(
            journey.OrganizationId, journey.WorkspaceId, journey.EndedSessionId, CancellationToken.None));
        Assert.NotNull(await manifests.FindByExportJobIdAsync(
            journey.OrganizationId, journey.WorkspaceId, exportJobId, CancellationToken.None));
        Assert.NotNull(await exportJobs.FindByIdAsync(
            journey.OrganizationId, journey.WorkspaceId, exportJobId, CancellationToken.None));

        // FOREIGN TENANT (threat T5): the same reads through another tenant's organization id return NOTHING —
        // the recap, the manifest and the export job are never reachable across the tenant boundary, even though
        // the workspace and ids are otherwise valid. Fail-closed at the persistence layer.
        Assert.Empty(await recaps.ListBySessionAsync(
            foreignOrganizationId, journey.WorkspaceId, journey.EndedSessionId, CancellationToken.None));
        Assert.Null(await manifests.FindByExportJobIdAsync(
            foreignOrganizationId, journey.WorkspaceId, exportJobId, CancellationToken.None));
        Assert.Null(await exportJobs.FindByIdAsync(
            foreignOrganizationId, journey.WorkspaceId, exportJobId, CancellationToken.None));

        // UNAUTHORIZED ROLE: recaps/exports have no HTTP read route, so role authorization is enforced by the
        // role-based projection. The host-only recap body and the export inventory are NEVER exposed to an
        // audience role — the projection falls closed to the stripped, content-free summary shapes.
        var recap = Assert.Single(await recaps.ListBySessionAsync(
            journey.OrganizationId, journey.WorkspaceId, journey.EndedSessionId, CancellationToken.None));
        var manifest = await manifests.FindByExportJobIdAsync(
            journey.OrganizationId, journey.WorkspaceId, exportJobId, CancellationToken.None);
        Assert.NotNull(manifest);

        Assert.IsType<RecapSummaryView>(RecapProjection.Project(recap, MembershipRole.Participant));
        Assert.IsType<RecapSummaryView>(RecapProjection.Project(recap, MembershipRole.Observer));
        Assert.IsType<ExportManifestSummaryView>(ExportManifestProjection.Project(manifest, MembershipRole.Participant));
        Assert.IsType<ExportManifestSummaryView>(ExportManifestProjection.Project(manifest, MembershipRole.Observer));
    }

    // =====================================================================
    // The API-driven journey: drive a session to completion through the public
    // write endpoints, leaving the database the worker jobs then read.
    // =====================================================================

    /// <summary>
    /// Drives the session lifecycle THROUGH THE PUBLIC API (asserting the real HTTP status of every write
    /// step), seeding only the minimal bootstrap that has no public write route, and returns the coordinates the
    /// worker-job assertions consume. The workspace ends with TWO sessions (one ended, one still live), one
    /// scene, two content blocks and two participants — a realistic, recappable/exportable workspace.
    /// </summary>
    private static async Task<Journey> RunJourneyAsync(WorkspaceApiFactory factory)
    {
        using var hostClient = factory.CreateClientFor(_hostSubject, _issuer, _orgSlug);

        // Organization + workspace, through the API (the host becomes the founding Owner).
        var organization = await CreateOrganizationAsync(hostClient, _orgSlug, "Northwind Labs");
        var workspaceId = await CreateWorkspaceAsync(hostClient, _orgSlug, "summer-show", "Summer Show");

        // Minimal bootstrap with NO public write route: the host's WORKSPACE membership, the participants'
        // ORGANIZATION memberships and the PARTICIPANT records (there is no participant-create endpoint, only
        // join/leave over an existing participant). The host's org Owner membership was produced by org-create.
        Guid hostUserProfileId = Guid.Empty;
        Guid participantAId = Guid.Empty;
        Guid participantBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var hostProfile = await db.UserProfiles.SingleAsync(u => u.Issuer == _issuer && u.SubjectId == _hostSubject);
            hostUserProfileId = hostProfile.Id;
            await db.AddWorkspaceMemberAsync(organization.Id, workspaceId, hostProfile.Id, MembershipRole.Host);

            var participantAUser = await db.AddUserAsync(_issuer, _participantASubject);
            var participantBUser = await db.AddUserAsync(_issuer, _participantBSubject);
            await db.AddOrganizationMemberAsync(organization.Id, participantAUser.Id, MembershipRole.Participant);
            await db.AddOrganizationMemberAsync(organization.Id, participantBUser.Id, MembershipRole.Participant);

            var participantA = await db.AddParticipantAsync(organization.Id, workspaceId, participantAUser.Id, "A");
            var participantB = await db.AddParticipantAsync(organization.Id, workspaceId, participantBUser.Id, "B");
            participantAId = participantA.Id;
            participantBId = participantB.Id;
        });

        // Sessions, through the API: the session that will be ended (and recapped) plus a second, CONCURRENT
        // session that stays live (so it is never eligible for a recap).
        var endedSessionId = await CreateSessionAsync(hostClient, workspaceId, _orgSlug, "Opening Run");
        await StartSessionAsync(hostClient, endedSessionId, _orgSlug);

        var liveSessionId = await CreateSessionAsync(hostClient, workspaceId, _orgSlug, "Parallel Run");
        await StartSessionAsync(hostClient, liveSessionId, _orgSlug);

        // Scene + content blocks, through the API.
        var sceneId = await CreateSceneAsync(hostClient, workspaceId, _orgSlug, "Opening Scene");
        var privateContentBlockId = await CreateContentBlockAsync(hostClient, sceneId, _orgSlug, "Text", _privateBlockBody);
        var hiddenContentBlockId = await CreateContentBlockAsync(hostClient, sceneId, _orgSlug, "Text", _hiddenBlockBody);

        // Participant presence + reveals/hide, through the API — a realistic activity stream the recap reflects.
        await JoinAsync(hostClient, endedSessionId, participantAId, _orgSlug);
        await JoinAsync(hostClient, endedSessionId, participantBId, _orgSlug);

        await RevealAsync(hostClient, endedSessionId, _orgSlug, "Scene", sceneId, targetParticipantId: null, "reveal-scene");
        await RevealAsync(
            hostClient, endedSessionId, _orgSlug, "ContentBlock", privateContentBlockId, participantAId, "reveal-private");
        await RevealAsync(
            hostClient, endedSessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null, "reveal-hidden");
        await HideAsync(
            hostClient, endedSessionId, _orgSlug, "ContentBlock", hiddenContentBlockId, targetParticipantId: null, "hide-hidden");

        // End the session, through the API — this is what makes it eligible for an automatic recap.
        await EndSessionAsync(hostClient, endedSessionId, _orgSlug);

        return new Journey(
            organization.Id,
            workspaceId,
            hostUserProfileId,
            endedSessionId,
            liveSessionId,
            sceneId,
            privateContentBlockId,
            hiddenContentBlockId,
            participantAId,
            participantBId);
    }

    // =====================================================================
    // Worker-job seams: construct each real service over a scoped DbContext
    // resolved from the running host, exactly as the worker host wires it.
    // =====================================================================

    /// <summary>
    /// Runs one real recap-generation sweep against the same database the API wrote, constructing the service
    /// over a scoped <see cref="LiveCoreDbContext"/> with its real repositories — the same wiring the worker
    /// host registers (<c>AddRecapGeneration</c>) and the worker unit tests construct.
    /// </summary>
    private static async Task<RecapGenerationResult> RunRecapJobAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var service = new RecapGenerationService(
            new RecapEligibleSessionReader(db),
            new RecapRepository(db),
            new SessionEventRepository(db),
            new RecapGenerationOptions(TimeSpan.FromHours(1), batchSize: 50),
            TimeProvider.System,
            NullLogger<RecapGenerationService>.Instance);
        return await service.GenerateDueRecapsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs one real export-processing sweep against the same database. All four collaborators share the ONE
    /// scoped <see cref="LiveCoreDbContext"/>, so the job's guarded status transitions commit atomically with
    /// the produced manifest in a single unit of work — exactly as the worker host runs the sweep per scope.
    /// </summary>
    private static async Task<ExportProcessingResult> RunExportJobAsync(WorkspaceApiFactory factory)
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

    /// <summary>
    /// Requests a workspace export by registering a Pending workspace-scoped <see cref="ExportJob"/> through the
    /// existing aggregate factory and repository (recaps/exports have no HTTP request route by design). Returns
    /// the new job's id.
    /// </summary>
    private static async Task<Guid> RequestWorkspaceExportAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid requesterId)
    {
        Guid exportJobId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var job = ExportJob.Create(organizationId, workspaceId, requesterId, ExportScope.Workspace, TestData.SeedTime);
            await new ExportJobRepository(db).AddAsync(job, CancellationToken.None);
            exportJobId = job.Id;
        });
        return exportJobId;
    }

    private static async Task<IReadOnlyList<Recap>> ListRecapsAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new RecapRepository(db).ListBySessionAsync(organizationId, workspaceId, sessionId, CancellationToken.None);
    }

    private static async Task<ExportManifest?> FindManifestAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid exportJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new ExportManifestRepository(db)
            .FindByExportJobIdAsync(organizationId, workspaceId, exportJobId, CancellationToken.None);
    }

    private static async Task<ExportJob?> FindExportJobAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid exportJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new ExportJobRepository(db).FindByIdAsync(organizationId, workspaceId, exportJobId, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<SessionEvent>> ReadSessionStreamAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await new SessionEventRepository(db).ListBySessionAsync(organizationId, sessionId, CancellationToken.None);
    }

    private static int Count(IReadOnlyList<SessionEvent> stream, string eventType)
        => stream.Count(sessionEvent => sessionEvent.EventType == eventType);

    private static int EntryCount(ExportManifest manifest, ExportResourceKind kind)
        => manifest.Entries.Single(entry => entry.Kind == kind).ItemCount;

    // =====================================================================
    // Write-endpoint helpers (each asserts the real HTTP status).
    // =====================================================================

    private static async Task<OrganizationDto> CreateOrganizationAsync(HttpClient client, string slug, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { slug, name }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create organization");
        var body = await response.Content.ReadFromJsonAsync<OrganizationDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body;
    }

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string organizationSlug, string slug, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new { organizationSlug, slug, name }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create workspace");
        var body = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client, Guid workspaceId, string organizationSlug, string title)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions", new { organizationSlug, title }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Prepared", body.Status);
        return body.Id;
    }

    private static async Task StartSessionAsync(HttpClient client, Guid sessionId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={organizationSlug}", content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "start session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Live", body.Status);
    }

    private static async Task EndSessionAsync(HttpClient client, Guid sessionId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/end?organizationSlug={organizationSlug}", content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "end session");
        var body = await response.Content.ReadFromJsonAsync<SessionDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Ended", body.Status);
    }

    private static async Task<Guid> CreateSceneAsync(HttpClient client, Guid workspaceId, string organizationSlug, string title)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes", new { organizationSlug, title }, _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create scene");
        var body = await response.Content.ReadFromJsonAsync<SceneDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task<Guid> CreateContentBlockAsync(
        HttpClient client, Guid sceneId, string organizationSlug, string type, string blockBody)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/scenes/{sceneId}/content-blocks?organizationSlug={organizationSlug}",
            new { type, body = blockBody },
            _json);
        await EnsureStatusAsync(response, HttpStatusCode.Created, "create content block");
        var body = await response.Content.ReadFromJsonAsync<ContentBlockDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        return body.Id;
    }

    private static async Task JoinAsync(HttpClient client, Guid sessionId, Guid participantId, string organizationSlug)
    {
        var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/participants/{participantId}/join?organizationSlug={organizationSlug}",
            content: null);
        await EnsureStatusAsync(response, HttpStatusCode.OK, "join participant");
        var body = await response.Content.ReadFromJsonAsync<PresenceDto>(_json);
        Assert.NotNull(body);
        Assert.Equal("Joined", body.Outcome);
    }

    private static async Task RevealAsync(
        HttpClient client,
        Guid sessionId,
        string organizationSlug,
        string resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        string idempotencyKey)
    {
        var payload = new { organizationSlug, resourceType, resourceId, participantId = targetParticipantId };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(payload, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        await EnsureStatusAsync(response, HttpStatusCode.OK, $"reveal {resourceType}");
        var body = await response.Content.ReadFromJsonAsync<VisibilityCommandDto>(_json);
        Assert.NotNull(body);
        Assert.True(body.Visible);
    }

    private static async Task HideAsync(
        HttpClient client,
        Guid sessionId,
        string organizationSlug,
        string resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        string idempotencyKey)
    {
        var payload = new { organizationSlug, resourceType, resourceId, participantId = targetParticipantId };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/hide")
        {
            Content = JsonContent.Create(payload, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        await EnsureStatusAsync(response, HttpStatusCode.OK, $"hide {resourceType}");
        var body = await response.Content.ReadFromJsonAsync<VisibilityCommandDto>(_json);
        Assert.NotNull(body);
        Assert.False(body.Visible);
    }

    private static async Task EnsureStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Journey step '{step}' expected {expected} but got {(int)response.StatusCode}. Body: {body}");
        }
    }

    // =====================================================================
    // Captured journey context + response DTOs (only the fields the assertions read).
    // =====================================================================

    private readonly record struct Journey(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid HostUserProfileId,
        Guid EndedSessionId,
        Guid LiveSessionId,
        Guid SceneId,
        Guid PrivateContentBlockId,
        Guid HiddenContentBlockId,
        Guid ParticipantAId,
        Guid ParticipantBId);

    private sealed record OrganizationDto(Guid Id, string Slug, string Name);

    private sealed record WorkspaceDto(Guid Id);

    private sealed record SessionDto(Guid Id, string Status);

    private sealed record SceneDto(Guid Id);

    private sealed record ContentBlockDto(Guid Id);

    private sealed record PresenceDto(Guid SessionId, Guid ParticipantId, string Outcome);

    private sealed record VisibilityCommandDto(string ResourceType, Guid ResourceId, bool Visible, string Outcome);
}
