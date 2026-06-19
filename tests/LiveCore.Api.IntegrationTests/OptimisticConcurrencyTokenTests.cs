// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration coverage for the optimistic-concurrency tokens on the mutable
/// aggregates (CORE-CONC-001). It exercises the headline acceptance scenarios — two
/// interleaved read-modify-write commands on ONE row where the SECOND write must be
/// rejected (a conflict) instead of silently overwriting the first, and the
/// single-writer happy path being unaffected — directly against the database through
/// two independent <see cref="LiveCoreDbContext"/> instances, exactly the
/// cross-context race the in-memory state-machine guards cannot catch.
///
/// <para>
/// The conflict is detected by the PostgreSQL <c>xmin</c> row-version token, which is
/// a PostgreSQL system column; the default in-memory SQLite test provider has no such
/// column, so the token is mapped ONLY on Npgsql (see
/// <see cref="LiveCoreDbContext"/>). These tests therefore assert the conflict only
/// when the suite runs against PostgreSQL (the CI <c>integration-postgres</c> job,
/// <see cref="PostgresTestDatabase.IsConfigured"/>); on a default local run (in-memory
/// SQLite) they are reported as skipped-with-reason rather than silently passing green
/// (CORE-TST-011), since the read-modify-write conflict the token guards cannot occur
/// without the Npgsql-only <c>xmin</c> mapping. The HTTP translation of the resulting
/// <see cref="DbUpdateConcurrencyException"/> into a 409 is covered by the
/// <c>ConcurrencyConflictMiddleware</c> unit tests; forcing a real conflict through
/// the HTTP layer is inherently racy because each request loads and saves within one
/// short scope, so the deterministic conflict is exercised here at the persistence
/// layer.
/// </para>
/// </summary>
public sealed class OptimisticConcurrencyTokenTests
{
    private static readonly DateTimeOffset _commandTime = TestData.SeedTime.AddMinutes(5);

    [SkippableFact]
    public async Task Interleaved_session_lifecycle_writes_make_the_second_writer_conflict()
    {
        // The interleaved conflict needs the Npgsql-only xmin token; on a default local run (SQLite) this is
        // skipped-with-reason rather than silently passing green (CORE-TST-011). The CI leg runs it for real.
        ProviderGate.RequirePostgres();

        await using var factory = new WorkspaceApiFactory();

        // Creating the client builds the host and provisions the test database.
        using var client = factory.CreateClient();

        Guid sessionId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var organization = await context.AddOrganizationAsync("concurrency-org");
            var workspace = await context.AddWorkspaceAsync(organization.Id, "concurrency-ws", "Concurrency Workspace");
            var session = await context.AddSessionAsync(
                organization.Id, workspace.Id, "Race", SessionStatus.Prepared);
            sessionId = session.Id;
        });

        // Two writers each load the SAME prepared session into their own context, so
        // both capture the same row version before either commits — the classic
        // interleaved read-modify-write the token guards.
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var firstSession = await firstContext.Sessions.SingleAsync(session => session.Id == sessionId);
        var secondSession = await secondContext.Sessions.SingleAsync(session => session.Id == sessionId);

        // The first writer starts the session and commits; its write wins and the row
        // version advances.
        firstSession.Start(_commandTime);
        await firstContext.SaveChangesAsync();

        // The second writer holds the now-stale snapshot and tries to drive the same
        // session. Both writers' in-memory guards passed (each loaded a Prepared
        // session), so only the persistence token can stop the second write from
        // silently overwriting the first.
        secondSession.Start(_commandTime);

        // The stale xmin no longer matches, so the second SaveChanges fails loudly
        // instead of losing the first writer's update.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());

        // The single-writer happy path is unaffected: a fresh reader can end the live
        // session and commit successfully.
        using var thirdScope = factory.Services.CreateScope();
        var thirdContext = thirdScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var liveSession = await thirdContext.Sessions.SingleAsync(session => session.Id == sessionId);
        Assert.Equal(SessionStatus.Live, liveSession.Status);

        liveSession.End(_commandTime);
        await thirdContext.SaveChangesAsync();

        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var endedSession = await verifyContext.Sessions.SingleAsync(session => session.Id == sessionId);
        Assert.Equal(SessionStatus.Ended, endedSession.Status);
    }

    [SkippableFact]
    public async Task Interleaved_reveal_and_hide_on_one_rule_make_the_second_writer_conflict()
    {
        ProviderGate.RequirePostgres();

        await using var factory = new WorkspaceApiFactory();

        using var client = factory.CreateClient();

        var resourceId = Guid.NewGuid();
        Guid ruleId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var organization = await context.AddOrganizationAsync("concurrency-org");
            var workspace = await context.AddWorkspaceAsync(organization.Id, "concurrency-ws", "Concurrency Workspace");
            var session = await context.AddSessionAsync(
                organization.Id, workspace.Id, "Concurrency Session", SessionStatus.Live);
            var rule = await context.AddVisibilityRuleAsync(
                organization.Id, workspace.Id, session.Id, VisibilityResourceType.Scene, resourceId, VisibilityState.Hidden);
            ruleId = rule.Id;
        });

        using var revealScope = factory.Services.CreateScope();
        using var hideScope = factory.Services.CreateScope();
        var revealContext = revealScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var hideContext = hideScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var revealRule = await revealContext.VisibilityRules.SingleAsync(rule => rule.Id == ruleId);
        var hideRule = await hideContext.VisibilityRules.SingleAsync(rule => rule.Id == ruleId);

        // The reveal commits first and wins (the resource becomes Visible).
        revealRule.ChangeVisibility(VisibilityState.Visible, _commandTime);
        await revealContext.SaveChangesAsync();

        // The hide raced on the stale snapshot.
        hideRule.ChangeVisibility(VisibilityState.Hidden, _commandTime);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => hideContext.SaveChangesAsync());

        // The conflict means the hide did NOT silently overwrite the reveal: the
        // resource is left in the reveal's committed state, not stale-hidden.
        using (var verifyScope = factory.Services.CreateScope())
        {
            var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
            var rule = await verifyContext.VisibilityRules.SingleAsync(r => r.Id == ruleId);
            Assert.Equal(VisibilityState.Visible, rule.Visibility);
        }

        // Single-writer happy path: a fresh reader can hide the resource and commit.
        using var happyScope = factory.Services.CreateScope();
        var happyContext = happyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var happyRule = await happyContext.VisibilityRules.SingleAsync(rule => rule.Id == ruleId);
        happyRule.ChangeVisibility(VisibilityState.Hidden, _commandTime);
        await happyContext.SaveChangesAsync();

        using var finalScope = factory.Services.CreateScope();
        var finalContext = finalScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var finalRule = await finalContext.VisibilityRules.SingleAsync(rule => rule.Id == ruleId);
        Assert.Equal(VisibilityState.Hidden, finalRule.Visibility);
    }
}
