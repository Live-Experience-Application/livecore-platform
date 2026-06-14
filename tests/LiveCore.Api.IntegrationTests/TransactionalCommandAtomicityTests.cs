using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the multi-step command unit of work (CORE-CONC-002). They drive the real
/// reveal and session-start commands over real HTTP (<see cref="WorkspaceApiFactory"/>: test auth + EF Core
/// SQLite, foreign keys ON) and assert the story's transactional-integrity contract end-to-end: each command
/// that performs a rule/state change PLUS audit PLUS event-append PLUS quota change commits them in ONE
/// database transaction, a part-way failure rolls EVERYTHING back, and realtime delivery happens AFTER the
/// commit.
///
/// The story's two required tests, applied to both handler families (reveal and session start):
/// <list type="bullet">
///   <item>FAILURE INJECTION: a failure injected at the event-append step (a throwing
///   <see cref="ISessionEventRepository"/>) — after the rule/state change, the audit and the
///   idempotency/quota writes — rolls the ENTIRE command back, so NOTHING is persisted: no half-applied
///   reveal, no orphan audit, no orphan event, no consumed quota, no recorded idempotency key.</item>
///   <item>HAPPY PATH: a successful command persists every effect EXACTLY ONCE (one rule/state change, one
///   audit fact, the documented events, one quota change, one idempotency key).</item>
/// </list>
/// A third test pins COMMIT-THEN-PUBLISH: a realtime delivery failure (a throwing
/// <see cref="IRealtimeBackplane"/>) happens AFTER the commit, so it canNOT roll back the already-committed
/// state — the rule, audit and durable events are still persisted. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class TransactionalCommandAtomicityTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // Reveal command (rule change + audit + idempotency key + event append).
    // =====================================================================

    [Fact]
    public async Task Reveal_failure_at_the_event_append_rolls_back_the_entire_command()
    {
        // Inject a failure AT the durable event-append step — after the reveal's rule change, audit record
        // and idempotency-key write have run inside the transaction.
        await using var factory = new FailingEventAppendApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedLiveSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var status = await SendCapturingStatusAsync(client, BuildRevealRequest(seed.SessionId, resourceId, "key-1"));

        // The command did not succeed.
        Assert.NotEqual(HttpStatusCode.OK, status ?? HttpStatusCode.InternalServerError);

        // ...and NOTHING was persisted: the rule change, the audit fact, the durable events and the
        // idempotency key all rolled back together, so the append-only stream never diverged from state.
        Assert.Equal(0, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.Empty(await AuditEntriesAsync(factory, seed.OrganizationId));
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(0, await IdempotencyKeyCountAsync(factory));
    }

    [Fact]
    public async Task Reveal_happy_path_persists_every_effect_exactly_once()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedLiveSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.SendAsync(BuildRevealRequest(seed.SessionId, resourceId, "key-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one of each effect — the rule change, the audit fact and the idempotency key — and exactly
        // the two documented durable events (ContentRevealed + VisibilityRuleChanged for a non-Scene reveal).
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        var audit = await AuditEntriesAsync(factory, seed.OrganizationId);
        Assert.Equal(AuditAction.VisibilityRuleChanged, Assert.Single(audit).Action);
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Single(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.Equal(1, await IdempotencyKeyCountAsync(factory));
    }

    [Fact]
    public async Task Reveal_delivery_failure_after_commit_does_not_roll_back_committed_state()
    {
        // COMMIT-THEN-PUBLISH: the realtime delivery runs AFTER the transaction commits, so a transport
        // failure cannot roll back the committed reveal — the rule, the audit and the durable events stay
        // persisted (a reconnecting client replays the missed push later, CORE-RT-005).
        await using var factory = new FailingDeliveryApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedLiveSessionAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        await SendCapturingStatusAsync(client, BuildRevealRequest(seed.SessionId, resourceId, "key-1"));

        // The reveal's effects are committed even though delivery failed afterwards.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.Equal(AuditAction.VisibilityRuleChanged, Assert.Single(await AuditEntriesAsync(factory, seed.OrganizationId)).Action);
        Assert.Equal(2, (await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId)).Count);
        Assert.Equal(1, await IdempotencyKeyCountAsync(factory));
    }

    // =====================================================================
    // Session start command (state change + quota change + audit + event append).
    // =====================================================================

    [Fact]
    public async Task Session_start_failure_at_the_event_append_rolls_back_the_entire_command()
    {
        // Inject a failure at the event-append step — after session.Start, the quota consumption and the
        // audit record have run inside the transaction.
        await using var factory = new FailingEventAppendApiFactory();
        const string subject = "host-a";
        var seed = await SeedPreparedSessionWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var status = await SendCapturingStatusAsync(client, BuildStartRequest(seed.SessionId));

        Assert.NotEqual(HttpStatusCode.OK, status ?? HttpStatusCode.InternalServerError);

        // The session stayed Prepared (no half-applied state change), and the audit, the durable event and
        // the quota consumption all rolled back: no orphan effect.
        Assert.Equal(SessionStatus.Prepared, await SessionStatusAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Empty(await AuditEntriesAsync(factory, seed.OrganizationId));
        Assert.Empty(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(0, await QuotaUsedAmountAsync(factory, seed.WorkspaceId));
    }

    [Fact]
    public async Task Session_start_happy_path_persists_every_effect_exactly_once()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedPreparedSessionWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.SendAsync(BuildStartRequest(seed.SessionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The state change, the audit fact, the durable event and the quota consumption each happened
        // exactly once.
        Assert.Equal(SessionStatus.Live, await SessionStatusAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(AuditAction.SessionStarted, Assert.Single(await AuditEntriesAsync(factory, seed.OrganizationId)).Action);
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(SessionEventTypes.SessionStarted, Assert.Single(events).EventType);
        Assert.Equal(1, await QuotaUsedAmountAsync(factory, seed.WorkspaceId));
    }

    // =====================================================================
    // Request builders.
    // =====================================================================

    private static HttpRequestMessage BuildRevealRequest(Guid sessionId, Guid resourceId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(
                new { organizationSlug = _orgA, resourceType = "Entity", resourceId },
                options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static HttpRequestMessage BuildStartRequest(Guid sessionId)
        => new(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}");

    /// <summary>
    /// Sends the request and returns its status code, or <see langword="null"/> if the injected failure
    /// surfaces as an unhandled exception from the test server rather than a response. Either way the command
    /// did NOT succeed; the persisted-state assertions are the authoritative check.
    /// </summary>
    private static async Task<HttpStatusCode?> SendCapturingStatusAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    // Seeding.
    // =====================================================================

    private static async Task<SeedResult> SeedLiveSessionAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Live);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    /// <summary>
    /// Seeds a Host + a PREPARED session and a GOVERNING <c>session.active.max</c> quota (definition +
    /// entitlement granting the workspace a limit), so starting the session both passes the quota check and
    /// RECORDS a consumption — making the quota change a real, observable effect of the unit of work.
    /// </summary>
    private static async Task<SeedResult> SeedPreparedSessionWithQuotaAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);

            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.Workspace, workspace.Id, entitlement, limit: 5);

            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    // =====================================================================
    // Persisted-state readers (each through a fresh scope, so it round-trips the database).
    // =====================================================================

    private static async Task<int> RuleCountAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.VisibilityRules.AsNoTracking()
            .CountAsync(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId);
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> AuditEntriesAsync(WorkspaceApiFactory factory, Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }

    private static async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync();
    }

    private static async Task<int> IdempotencyKeyCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.IdempotencyKeys.AsNoTracking().CountAsync();
    }

    private static async Task<SessionStatus> SessionStatusAsync(WorkspaceApiFactory factory, Guid organizationId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var session = await context.Sessions.AsNoTracking()
            .SingleAsync(s => s.OrganizationId == organizationId && s.Id == sessionId);
        return session.Status;
    }

    private static async Task<long> QuotaUsedAmountAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.QuotaUsage.AsNoTracking()
            .Where(usage => usage.SubjectId == workspaceId)
            .Select(usage => usage.UsedAmount)
            .SumAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    // =====================================================================
    // Factories that inject a failure into a single production seam.
    // =====================================================================

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> whose <see cref="ISessionEventRepository"/> THROWS on append, so a
    /// command's durable event-append step fails after its rule/state change, audit and idempotency/quota
    /// writes have run inside the transaction. Only that one seam is swapped; the unit of work, the command
    /// services and the repositories are otherwise the production wiring, so the test exercises the real
    /// rollback path.
    /// </summary>
    private sealed class FailingEventAppendApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISessionEventRepository>();
                services.AddScoped<ISessionEventRepository, ThrowingSessionEventRepository>();
            });
        }
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> whose <see cref="IRealtimeBackplane"/> THROWS on send, so the
    /// AFTER-commit realtime delivery fails while the durable append (inside the transaction) succeeds —
    /// pinning that a delivery failure cannot roll back committed state (commit-then-publish).
    /// </summary>
    private sealed class FailingDeliveryApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRealtimeBackplane>();
                services.AddSingleton<IRealtimeBackplane>(new ThrowingRealtimeBackplane());
            });
        }
    }

    private sealed class ThrowingSessionEventRepository : ISessionEventRepository
    {
        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected failure at the session-event append step.");

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            Guid organizationId,
            Guid sessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingRealtimeBackplane : IRealtimeBackplane
    {
        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected realtime transport failure (after commit).");
    }
}
