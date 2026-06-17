// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for execution-strategy RETRY resilience of the multi-step command unit of work
/// (CORE-CONC-005). The production hosts run every <see cref="LiveCoreDbContext"/> with a RETRYING execution
/// strategy (<c>EnableRetryOnFailure</c>, CORE-CONC-003), so a transient database disruption re-runs the whole
/// unit of work; the default integration factory uses a plain non-retrying SQLite provider, so that re-run path
/// is never exercised — which is exactly why the change-tracker leak this story fixes stayed latent.
///
/// These tests close that gap on BOTH providers. The injected failure is a GENUINE transient PostgreSQL error
/// — a serialization failure (SQLSTATE 40001), one of the transaction-rollback transients the production
/// strategy retries — thrown exactly once at the durable session-event append (after the command's state/rule
/// change, audit and quota/idempotency writes have run inside the transaction), so the FIRST attempt rolls back
/// and the unit of work retries once. On the CI PostgreSQL leg the UNCHANGED production
/// <c>EnableRetryOnFailure</c> strategy retries it (the real production posture); on the default SQLite leg
/// <see cref="RetryingCommandApiFactory"/> installs an equivalent retrying execution strategy (SQLite has none
/// by default) so the same re-run path runs without a PostgreSQL server. The contract under test: the retried
/// command ultimately SUCCEEDS with correct state, because the unit of work clears the change tracker before the
/// retry and the command reloads its entities inside the delegate — so the retry runs against fresh state rather
/// than the first attempt's stale, already-mutated tracked entities (no
/// <see cref="InvalidSessionStateTransitionException"/>, no double-add). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class CommandRetryResilienceTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Session_start_succeeds_after_a_transient_failure_is_retried()
    {
        await using var factory = new RetryingCommandApiFactory();
        const string subject = "host-a";
        var seed = await SeedPreparedSessionWithQuotaAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.SendAsync(BuildStartRequest(seed.SessionId));

        // The first attempt threw a transient failure mid-delegate and rolled back; the retry re-applied the
        // Prepared -> Live transition to FRESH state (a stale Live tracked entity would have thrown
        // InvalidSessionStateTransitionException, surfacing as 500). The command ultimately succeeded.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ...and every effect happened EXACTLY ONCE — the rolled-back first attempt left nothing behind, and the
        // retry did not double-apply: the session is Live, with one audit fact, one durable event and one unit
        // of consumed quota.
        Assert.Equal(SessionStatus.Live, await SessionStatusAsync(factory, seed.OrganizationId, seed.SessionId));
        Assert.Equal(AuditAction.SessionStarted, Assert.Single(await AuditEntriesAsync(factory, seed.OrganizationId)).Action);
        Assert.Equal(SessionEventTypes.SessionStarted, Assert.Single(await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId)).EventType);
        Assert.Equal(1, await QuotaUsedAmountAsync(factory, seed.WorkspaceId));
    }

    [Fact]
    public async Task Reveal_succeeds_after_a_transient_failure_is_retried()
    {
        await using var factory = new RetryingCommandApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();

        // Seed an EXISTING hidden rule for the resource so the reveal FLIPS it (Hidden -> Visible). This is the
        // case that genuinely exercises the change-tracker leak: on the retry the rule still exists in the
        // database, so a stale, already-flipped tracked instance would be identity-resolved back as "already
        // Visible" and the reveal would no-op (emitting nothing) instead of re-applying the change.
        var seed = await SeedLiveSessionWithHiddenRuleAsync(factory, subject, MembershipRole.Host, resourceId);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        using var response = await client.SendAsync(BuildRevealRequest(seed.SessionId, resourceId, "key-1"));

        // The first attempt flipped the rule to Visible, audited it and recorded the idempotency key, then threw
        // a transient failure at the ContentRevealed append and rolled back. The retry re-ran against fresh state
        // — the rolled-back rule read back as Hidden (not the stale Visible tracked instance), so the reveal
        // re-applied the change and emitted its events — and committed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one rule (the seeded one, flipped to Visible — not duplicated), one audit fact, one idempotency
        // key and the two documented durable events.
        Assert.Equal(1, await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.Equal(VisibilityState.Visible, await RuleVisibilityAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.Equal(AuditAction.VisibilityRuleChanged, Assert.Single(await AuditEntriesAsync(factory, seed.OrganizationId)).Action);
        var events = await SessionEventsAsync(factory, seed.OrganizationId, seed.SessionId);
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Single(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.Equal(1, await IdempotencyKeyCountAsync(factory));
    }

    // =====================================================================
    // Request builders.
    // =====================================================================

    private static HttpRequestMessage BuildStartRequest(Guid sessionId)
        => new(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}");

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

    // =====================================================================
    // Seeding.
    // =====================================================================

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

    private static async Task<SeedResult> SeedLiveSessionWithHiddenRuleAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role,
        Guid resourceId)
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
            await db.AddVisibilityRuleAsync(
                org.Id, workspace.Id, session.Id, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    // =====================================================================
    // Persisted-state readers (each through a fresh scope, so it round-trips the database).
    // =====================================================================

    private static async Task<SessionStatus> SessionStatusAsync(WorkspaceApiFactory factory, Guid organizationId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var session = await context.Sessions.AsNoTracking()
            .SingleAsync(s => s.OrganizationId == organizationId && s.Id == sessionId);
        return session.Status;
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

    private static async Task<long> QuotaUsedAmountAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.QuotaUsage.AsNoTracking()
            .Where(usage => usage.SubjectId == workspaceId)
            .Select(usage => usage.UsedAmount)
            .SumAsync();
    }

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

    private static async Task<VisibilityState> RuleVisibilityAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rule = await context.VisibilityRules.AsNoTracking()
            .SingleAsync(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId);
        return rule.Visibility;
    }

    private static async Task<int> IdempotencyKeyCountAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.IdempotencyKeys.AsNoTracking().CountAsync();
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);

    // =====================================================================
    // Retrying factory + one-shot transient failure injection.
    // =====================================================================

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that exercises the execution-strategy retry path over real HTTP. Its
    /// <see cref="ISessionEventRepository"/> throws a one-shot transient PostgreSQL serialization failure on the
    /// first append, so a command's first attempt fails mid-delegate and the unit of work retries it. On the
    /// PostgreSQL leg the UNCHANGED production <c>EnableRetryOnFailure</c> strategy retries that transient error;
    /// on the default SQLite leg this factory installs an equivalent retrying execution strategy (SQLite has none
    /// by default), so the re-run path runs identically on both providers. Only those seams are swapped; the unit
    /// of work, the command services and every other repository are the production wiring.
    /// </summary>
    private sealed class RetryingCommandApiFactory : WorkspaceApiFactory
    {
        protected override void ConfigureSqlite(SqliteDbContextOptionsBuilder sqlite)
            => sqlite.ExecutionStrategy(dependencies => new TransientRetryingExecutionStrategy(dependencies));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISessionEventRepository>();
                services.AddScoped<ISessionEventRepository, TransientOnceSessionEventRepository>();
            });
        }
    }

    /// <summary>
    /// Creates the one-shot transient failure injected mid-command: a genuine PostgreSQL SERIALIZATION FAILURE
    /// (SQLSTATE 40001), one of the transaction-rollback transients the production <c>EnableRetryOnFailure</c>
    /// strategy retries. Using a real transient error means the PostgreSQL leg retries it with the unchanged
    /// production strategy (the real posture) and the SQLite leg retries it with the factory's equivalent
    /// strategy — modelling a routine transient disruption without needing a real database outage.
    /// </summary>
    private static PostgresException CreateTransientFailure()
        => new(
            "Injected transient failure at the session-event append (retried by the execution strategy).",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.SerializationFailure);

    /// <summary>
    /// The SQLite leg's analogue of the production Npgsql <c>EnableRetryOnFailure</c> strategy: a retrying
    /// <see cref="ExecutionStrategy"/> that retries the injected transient serialization failure (SQLSTATE
    /// 40001) — the same error the production strategy retries on the PostgreSQL leg. The first retry incurs no
    /// back-off delay (the EF Core formula yields zero for the first retry) and exactly one failure is injected,
    /// so the retry is immediate.
    /// </summary>
    private sealed class TransientRetryingExecutionStrategy : ExecutionStrategy
    {
        public TransientRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception)
            => exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure };
    }

    /// <summary>
    /// A scoped <see cref="ISessionEventRepository"/> decorator that throws a one-shot transient PostgreSQL
    /// serialization failure on the FIRST append of the request (mid-delegate, after the command's state/rule
    /// change, audit and quota/idempotency writes have run inside the transaction) and DELEGATES every later
    /// append to the real repository. The throw triggers exactly one execution-strategy retry; the retried
    /// attempt's appends then succeed. The counter is per request scope, so each command retries at most once.
    /// </summary>
    private sealed class TransientOnceSessionEventRepository : ISessionEventRepository
    {
        private readonly SessionEventRepository _inner;
        private int _appendCalls;

        public TransientOnceSessionEventRepository(LiveCoreDbContext dbContext)
        {
            _inner = new SessionEventRepository(dbContext);
        }

        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _appendCalls) == 1)
            {
                throw CreateTransientFailure();
            }

            return _inner.AppendAsync(sessionEvent, cancellationToken);
        }

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            Guid organizationId,
            Guid sessionId,
            CancellationToken cancellationToken)
            => _inner.ListBySessionAsync(organizationId, sessionId, cancellationToken);
    }
}
