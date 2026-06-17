// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Content;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Proves the database now ENFORCES the high-value domain invariants the aggregates already guard in memory
/// (CORE-CONC-009): a raw <c>INSERT</c>/<c>UPDATE</c> that bypasses the aggregate and writes an out-of-range
/// enum or a negative order/usage is REJECTED by a CHECK constraint, while every value the aggregate produces
/// is still accepted (the constraints are additive and behaviour-neutral).
///
/// <para>
/// Each test arranges a single valid row through the real aggregate factories
/// (<see cref="TestData"/>) and then issues raw SQL — the same code-bypassing write a direct DB connection, a
/// future raw-SQL path or a mapping regression could attempt — against the test database. The out-of-range
/// write must fail with a CHECK constraint violation; a subsequent in-range write must succeed, proving valid
/// writes are unaffected.
/// </para>
///
/// <para>
/// The CHECK constraints are provider-agnostic, so the assertions hold on BOTH the default SQLite path (the
/// schema comes from <c>EnsureCreated()</c> over the model) and the CI <c>integration-postgres</c> leg (the
/// schema comes from the real, checked-in migration). <see cref="AssertCheckConstraintViolationAsync"/>
/// recognises either provider's constraint-violation error (PostgreSQL <c>23514</c> /
/// <c>SQLITE_CONSTRAINT</c>), so the true PostgreSQL behaviour the acceptance criterion names is exercised in
/// CI while the same invariant stays green locally.
/// </para>
/// </summary>
public sealed class DomainInvariantCheckConstraintTests
{
    private const string _issuer = "https://issuer.test";

    // Microsoft.Data.Sqlite surfaces SQLITE_CONSTRAINT (primary result code 19) for any constraint failure;
    // the CHECK sub-type carries the extended code 275. We assert on the primary code so the check is robust.
    private const int _sqliteConstraint = 19;

    [Fact]
    public async Task An_out_of_range_session_status_is_rejected_while_a_valid_status_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync("northwind-labs");
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);
        });

        // The status is stored by its stable enum NAME; a name outside the lifecycle allowlist is rejected.
        await AssertCheckConstraintViolationAsync(factory, "UPDATE sessions SET status = 'Bogus';");

        // A real lifecycle status the aggregate produces is unaffected.
        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE sessions SET status = 'Live';"));
    }

    [Fact]
    public async Task A_negative_scene_order_is_rejected_while_a_non_negative_order_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync("northwind-labs");
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddSceneAsync(org.Id, workspace.Id, "Opening", order: 0);
        });

        await AssertCheckConstraintViolationAsync(factory, "UPDATE scenes SET scene_order = -1;");

        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE scenes SET scene_order = 5;"));
    }

    [Fact]
    public async Task A_below_one_content_block_revision_number_is_rejected_while_a_valid_revision_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync("northwind-labs");
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Opening", order: 0);
            await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "A generic body.");
        });

        await AssertCheckConstraintViolationAsync(factory, "UPDATE content_blocks SET revision_number = 0;");

        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE content_blocks SET revision_number = 2;"));
    }

    [Fact]
    public async Task A_negative_quota_usage_is_rejected_while_zero_usage_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        var subjectId = Guid.CreateVersion7();
        await factory.SeedAsync(async db =>
        {
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.SessionActiveMax);
            var definition = await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.Workspace);
            await db.AddQuotaUsageAsync(EntitlementSubjectType.Workspace, subjectId, definition, usedAmount: 1);
        });

        await AssertCheckConstraintViolationAsync(factory, "UPDATE quota_usage SET used_amount = -1;");

        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE quota_usage SET used_amount = 0;"));
    }

    [Fact]
    public async Task A_negative_asset_size_bytes_is_rejected_while_null_and_non_negative_are_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, "host-a");
            var org = await db.AddOrganizationAsync("northwind-labs");
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
        });

        await AssertCheckConstraintViolationAsync(factory, "UPDATE assets SET size_bytes = -1;");

        // A confirmed asset carries a non-negative byte count; a pending asset carries null. Both are valid.
        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE assets SET size_bytes = 100;"));
        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE assets SET size_bytes = NULL;"));
    }

    [Fact]
    public async Task A_below_one_session_event_sequence_is_rejected_while_a_positive_sequence_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, "host-a");
            var org = await db.AddOrganizationAsync("northwind-labs");
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            var session = await db.AddSessionAsync(org.Id, workspace.Id, "Opening Night", SessionStatus.Prepared);

            // Seed an append-only event the way the append path persists one: with a real per-session
            // sequence (at least 1) stamped before the row is saved.
            var sessionEvent = SessionEvent.Create(
                org.Id, workspace.Id, session.Id, "ContentRevealed", user.Id,
                targetParticipantId: null, payload: "{}", schemaVersion: 1, createdAt: TestData.SeedTime);
            sessionEvent.AssignSequence(1);
            db.SessionEvents.Add(sessionEvent);
            await db.SaveChangesAsync();
        });

        // The per-session sequence is strictly positive; the unassigned 0 a fresh event carries must never
        // reach the table.
        await AssertCheckConstraintViolationAsync(factory, "UPDATE session_events SET sequence = 0;");

        Assert.Equal(1, await ExecuteAsync(factory, "UPDATE session_events SET sequence = 2;"));
    }

    /// <summary>
    /// Runs raw SQL against the test database through a scoped production <see cref="LiveCoreDbContext"/> and
    /// returns the number of rows affected. Used for the in-range writes that must succeed unchanged.
    /// </summary>
    private static async Task<int> ExecuteAsync(WorkspaceApiFactory factory, string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>
    /// Asserts the raw SQL is rejected by a CHECK constraint on whichever provider the suite runs against
    /// (PostgreSQL <c>23514</c> on the CI leg, <c>SQLITE_CONSTRAINT</c> on the default SQLite path).
    /// </summary>
    private static async Task AssertCheckConstraintViolationAsync(WorkspaceApiFactory factory, string sql)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => context.Database.ExecuteSqlRawAsync(sql));
        AssertCheckConstraintViolation(exception);
    }

    private static void AssertCheckConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
                return;
            }

            if (current is SqliteException sqlite)
            {
                Assert.Equal(_sqliteConstraint, sqlite.SqliteErrorCode);
                return;
            }
        }

        Assert.Fail($"Expected a CHECK constraint violation but got: {exception}");
    }
}
