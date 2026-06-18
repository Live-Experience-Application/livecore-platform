// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Postgres-leg tests for the two write-concurrency behaviors the adversarial review flagged as
/// Postgres-specific and unproven over real HTTP (CORE-TST-007):
///
/// <list type="number">
///   <item>SAVEPOINT RECOVERY ON A UNIQUE VIOLATION INSIDE THE UNIT-OF-WORK TRANSACTION. The reveal
///   create-race in <see cref="VisibilityRuleRepository"/> (and the mirror in
///   <see cref="LiveCore.Api.Entitlements.QuotaUsageRepository"/>) runs its INSERT inside the reveal
///   command's explicit unit-of-work transaction (<see cref="TransactionalUnitOfWork"/>). On PostgreSQL a
///   failed statement inside a transaction would ABORT the whole transaction — so the repository's
///   "<c>catch (DbUpdateException) -&gt; re-read -&gt; resolve duplicate</c>" recovery would itself fail
///   ("current transaction is aborted") were it not for the EF Core savepoint EF takes before each
///   <c>SaveChanges</c> in a user transaction: on the unique violation EF rolls back to that savepoint,
///   leaving the transaction ALIVE so the re-read can run and the two concurrent first-reveals converge on
///   exactly ONE rule. This savepoint behavior is invisible on the default SQLite test provider (a failed
///   statement there does not abort the transaction), so it was never exercised by a Postgres test. This
///   test drives two concurrent first-reveals of the SAME resource over real HTTP and proves the loser
///   recovers (200, not an aborted-transaction 500) to a single rule.</item>
///   <item>THE HTTP 409 CONCURRENCY-CONFLICT RESPONSE. The mutable aggregates carry the PostgreSQL
///   <c>xmin</c> row-version token (<see cref="LiveCoreDbContext"/>), so a cross-context lifecycle
///   read-modify-write race makes the loser's <c>SaveChanges</c> fail with a
///   <see cref="DbUpdateConcurrencyException"/>, which <see cref="ConcurrencyConflictMiddleware"/>
///   translates to a <c>409 Conflict</c>. <see cref="OptimisticConcurrencyTokenTests"/> proves the
///   conflict at the persistence layer but explicitly defers the HTTP translation as "inherently racy
///   because each request loads and saves within one short scope". This test forces that race
///   DETERMINISTICALLY end-to-end: a concurrent host start (the WINNER) holds the session row's write lock
///   uncommitted while the real start command over HTTP (the LOSER) reloads the still-Prepared row and
///   parks its UPDATE on that lock; releasing the winner then advances the row version, so the loser's
///   stale-version UPDATE matches zero rows and surfaces as a 409 — the start/end lifecycle conflict the
///   persistence test could only prove below the HTTP boundary.</item>
/// </list>
///
/// <para>
/// Both behaviors are PostgreSQL-specific (the savepoint-on-statement-abort and the <c>xmin</c> token are
/// Npgsql-only), so the conflict legs run only when the suite is on PostgreSQL
/// (<see cref="PostgresTestDatabase.IsConfigured"/>, the CI <c>integration-postgres</c> job). On the
/// default SQLite path the same scenarios run on a single shared connection (the savepoint test
/// sequentially, the conflict test as the single-writer happy path), so every assertion stays green on
/// both providers while the true Postgres race is exercised on the CI leg. The fixtures reuse the existing
/// reveal/session HTTP harness and seeding helpers (CORE-TST-007: "Reuse the Postgres harness + the
/// existing reveal/session tests") and are all generic Core vocabulary (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </para>
/// </summary>
public sealed class SavepointRecoveryAndHttpConflictTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly DateTimeOffset _commandTime = TestData.SeedTime.AddMinutes(5);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // =====================================================================
    // 1) Savepoint recovery: two concurrent first-reveals of one resource.
    // =====================================================================

    [Fact]
    public async Task Two_concurrent_first_reveals_of_one_resource_recover_via_savepoint_to_a_single_rule()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var resourceId = Guid.CreateVersion7();
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Live);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Two FIRST reveals of the SAME resource — same session, type, id and audience-wide dimension — each
        // with its OWN idempotency key, so neither short-circuits the other and both reach the
        // single-rule-per-dimension create. On PostgreSQL the loser's INSERT collides with the winner's
        // committed unique row and rolls back to the EF savepoint (the transaction stays alive), then its
        // re-read resolves the duplicate and the reveal re-resolves onto the winner's rule. On SQLite the two
        // run sequentially over the shared connection; the same invariant is asserted without the true race.
        var (statusA, statusB) = await RaceAsync(
            () => PostRevealStatusAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-a"),
            () => PostRevealStatusAsync(client, seed.SessionId, Body(_orgA, "Entity", resourceId), "key-b"));

        // BOTH reveals succeed (200): the loser recovered via the savepoint instead of failing on an aborted
        // transaction (no 500). Were the savepoint absent, the loser's in-transaction re-read would raise
        // "current transaction is aborted" on PostgreSQL and the reveal would be a 500.
        Assert.Equal(HttpStatusCode.OK, statusA);
        Assert.Equal(HttpStatusCode.OK, statusB);

        // The crown-jewel invariant: the create race converged on EXACTLY ONE rule (no second, un-hideable
        // ghost), and the resource is visible to the audience.
        Assert.Equal(
            1,
            await RuleCountAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
        Assert.True(
            await ResourceVisibleAsync(factory, seed.OrganizationId, seed.WorkspaceId, VisibilityResourceType.Entity, resourceId));
    }

    // =====================================================================
    // 2) HTTP 409: a concurrent session lifecycle write loses the xmin race.
    // =====================================================================

    [Fact]
    public async Task A_concurrent_session_lifecycle_write_surfaces_a_409_over_http()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedSessionAsync(factory, subject, MembershipRole.Host, SessionStatus.Prepared);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        if (!PostgresTestDatabase.IsConfigured)
        {
            // SQLite carries no xmin row-version token, so a cross-context lifecycle race is last-write-wins,
            // never a 409 — the conflict is a PostgreSQL-only guarantee (and the persistence-layer divergence
            // is already covered by OptimisticConcurrencyTokenTests / ProviderDivergentConcurrencyTests). Here
            // we only assert the single-writer happy path so the test stays green on the default provider; the
            // real end-to-end 409 is proven on the Postgres leg below.
            var happyPath = await StartSessionStatusAsync(client, seed.SessionId);
            Assert.Equal(HttpStatusCode.OK, happyPath);
            await AssertSessionStatusAsync(factory, seed.SessionId, SessionStatus.Live);
            return;
        }

        // The per-test database the production Npgsql registration points at (a fresh copy of the migrated
        // template). A SECOND, NON-retrying connection string off it gives the out-of-band winner its own
        // pool and lets it open a user transaction (the production retrying strategy forbids one — see
        // LiveCoreNpgsqlOptions); the xmin token is still mapped because it is an Npgsql connection.
        var outOfBandConnectionString = new NpgsqlConnectionStringBuilder(ConnectionStringFor(factory))
        {
            ApplicationName = "livecore-conflict-oob",
        }.ConnectionString;

        // WINNER (out of band): a concurrent host starts the SAME Prepared session and HOLDS its transaction
        // open. Its UPDATE wins the row's write lock (xmin V -> V', not yet visible to other transactions),
        // so the loser below will read the still-committed Prepared row and only collide at its own UPDATE.
        var winnerOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(outOfBandConnectionString)
            .Options;
        await using var winner = new LiveCoreDbContext(winnerOptions);
        await using var winnerTransaction = await winner.Database.BeginTransactionAsync();
        var winnerSession = await winner.Sessions.SingleAsync(session => session.Id == seed.SessionId);
        winnerSession.Start(_commandTime);
        await winner.SaveChangesAsync();

        // LOSER: the real start command over HTTP. Its pre-transaction load and its in-unit-of-work reload
        // both read the still-committed Prepared row (the winner is uncommitted, xmin = V), so the in-memory
        // CanStart guard passes; its UPDATE ... WHERE xmin = V then PARKS on the winner's row lock.
        var loserTask = Task.Run(() => StartSessionStatusAsync(client, seed.SessionId));

        // Wait until the loser's UPDATE is genuinely parked on the winner's lock, so committing the winner can
        // only resolve as a row-version conflict. (Committing the winner BEFORE the loser reloads would let
        // the loser read the post-commit Live row, an in-memory InvalidSessionStateTransition rather than the
        // 409 under test — so the deterministic order matters.)
        await WaitUntilOneBackendBlockedAsync(outOfBandConnectionString, TimeSpan.FromSeconds(30));

        // The winner COMMITS: the row version advances to V'. The loser's parked UPDATE re-evaluates its
        // WHERE xmin = V against the now-V' row, matches zero rows, and EF raises a
        // DbUpdateConcurrencyException — which ConcurrencyConflictMiddleware maps to a 409 end to end.
        await winnerTransaction.CommitAsync();

        var loserStatus = await loserTask;
        Assert.Equal(HttpStatusCode.Conflict, loserStatus);

        // Exactly one start won: the winner's transition stands and the loser changed nothing (it rolled back
        // with the 409), so the session is Live, not double-started.
        await AssertSessionStatusAsync(factory, seed.SessionId, SessionStatus.Live);
    }

    // =====================================================================
    // Race + HTTP helpers.
    // =====================================================================

    /// <summary>
    /// Runs two colliding operations and returns both outcomes. On the PostgreSQL leg they run TRULY
    /// concurrently (each request on its own pooled Npgsql connection), so the loser genuinely races the
    /// winner's committed unique row. On SQLite (one shared in-memory connection that cannot overlap) they
    /// run sequentially; the invariant under test is identical, only without the true concurrency the
    /// PostgreSQL leg adds. Mirrors <see cref="MoneyPathWriteConcurrencyTests"/>.
    /// </summary>
    private static async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        if (PostgresTestDatabase.IsConfigured)
        {
            var results = await Task.WhenAll(Task.Run(first), Task.Run(second)).ConfigureAwait(false);
            return (results[0], results[1]);
        }

        var firstResult = await first().ConfigureAwait(false);
        var secondResult = await second().ConfigureAwait(false);
        return (firstResult, secondResult);
    }

    private static async Task<HttpStatusCode> PostRevealStatusAsync(
        HttpClient client,
        Guid sessionId,
        object body,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/reveal")
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> StartSessionStatusAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.PostAsync(
            $"/api/v1/sessions/{sessionId}/start?organizationSlug={_orgA}",
            content: null);
        return response.StatusCode;
    }

    /// <summary>
    /// Polls <c>pg_stat_activity</c> until at least one OTHER backend in this test's database is waiting on a
    /// heavyweight lock — the loser's UPDATE parked on the winner's row lock. Filtering by
    /// <c>current_database()</c> isolates this from the other test collections running in parallel against
    /// their own databases (CORE-OPS-002), and excluding this polling backend keeps it from counting itself.
    /// The wait is bounded so a missed signal fails loudly rather than hanging the suite.
    /// </summary>
    private static async Task WaitUntilOneBackendBlockedAsync(string connectionString, TimeSpan timeout)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND pid <> pg_backend_pid();
                    """;
                var blocked = (long)(await command.ExecuteScalarAsync() ?? 0L);
                if (blocked >= 1)
                {
                    return;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The concurrent session start did not park on the row lock within the timeout.");
            }

            await Task.Delay(25);
        }
    }

    // =====================================================================
    // Seeding + persisted-state readers (each through a fresh scope).
    // =====================================================================

    /// <summary>Seeds an org + a workspace-member caller in the given role + a session in the given status, all in org A.</summary>
    private static async Task<SeedResult> SeedSessionAsync(
        WorkspaceApiFactory factory,
        string subject,
        MembershipRole role,
        SessionStatus status)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "S", status);
            seed = new SeedResult(org.Id, workspace.Id, session.Id);
        });
        return seed;
    }

    private static object Body(string? organizationSlug, string resourceType, Guid resourceId)
        => new { organizationSlug, resourceType, resourceId };

    /// <summary>
    /// The per-test database connection string the production registration is bound to, WITH credentials. Read
    /// from the factory's configured source (not a live, already-opened connection, whose string Npgsql strips
    /// the password from) so the out-of-band connection can authenticate.
    /// </summary>
    private static string ConnectionStringFor(WorkspaceApiFactory factory)
        => factory.ConfiguredDatabaseConnectionString;

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

    private static async Task<bool> ResourceVisibleAsync(
        WorkspaceApiFactory factory,
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var rules = await context.VisibilityRules.AsNoTracking()
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .ToListAsync();
        return rules.Any(rule => rule.IsVisibleToAudience());
    }

    private static async Task AssertSessionStatusAsync(
        WorkspaceApiFactory factory,
        Guid sessionId,
        SessionStatus expected)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var session = await context.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(expected, session.Status);
    }

    private readonly record struct SeedResult(Guid OrganizationId, Guid WorkspaceId, Guid SessionId);
}
