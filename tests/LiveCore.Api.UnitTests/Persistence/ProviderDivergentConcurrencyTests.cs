// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Provider-divergent repository test for the optimistic-concurrency token
/// (CORE-TST-004, exercising CORE-CONC-001). It is a unit/repository-level test whose
/// outcome genuinely DIVERGES between the two providers, so running the unit suite
/// against real PostgreSQL (the CI <c>unit-smoke-postgres</c> job) exercises behavior
/// the default SQLite path can never reach.
///
/// <para>
/// Two writers each load the SAME workspace into independent
/// <see cref="LiveCore.Api.Persistence.LiveCoreDbContext"/> instances — the classic
/// interleaved read-modify-write the in-memory aggregate guards cannot catch — and
/// both commit a rename:
/// </para>
///
/// <list type="bullet">
///   <item>On <b>PostgreSQL</b> the system column <c>xmin</c> is mapped as a
///   row-version concurrency token (Npgsql only). The first write bumps it, so the
///   second writer's stale snapshot is rejected with a
///   <see cref="DbUpdateConcurrencyException"/> instead of silently overwriting the
///   first writer's update — the cross-context guarantee that exists ONLY on
///   PostgreSQL.</item>
///   <item>On <b>SQLite</b> (the default unit-test provider) there is no such token,
///   so the same interleaving is last-write-wins and does NOT throw — confirming the
///   provider-conditional mapping leaves the SQLite path fully working.</item>
/// </list>
///
/// Because the assertion is selected by <see cref="ProviderTestDatabase.IsPostgres"/>,
/// the test PASSES on both providers: it proves the concurrency conflict on PostgreSQL
/// and proves the absence of the token on SQLite. The HTTP translation of the
/// conflict to a <c>409</c> is covered by the
/// <c>ConcurrencyConflictMiddleware</c> tests; this test exercises the deterministic
/// persistence-layer conflict the integration suite proves at the API level
/// (<c>OptimisticConcurrencyTokenTests</c>) but now at the unit/repository level too.
/// </summary>
public sealed class ProviderDivergentConcurrencyTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _renamedAt = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Interleaved_workspace_rename_conflicts_on_postgres_but_is_last_write_wins_on_sqlite()
    {
        await using var database = ProviderTestDatabase.Create();

        // Seed an organization and an active workspace through one committed context.
        Guid workspaceId;
        await using (var seed = database.CreateContext())
        {
            var organization = Organization.Create("provider-divergence-org", "Provider Divergence Org", _createdAt);
            Assert.Equal(
                OrganizationAddResult.Added,
                await new OrganizationRepository(seed).AddAsync(organization, CancellationToken.None));

            var workspace = Workspace.Create(organization.Id, "provider-divergence-ws", "Original", _createdAt);
            Assert.Equal(
                WorkspaceAddResult.Added,
                await new WorkspaceRepository(seed).AddAsync(workspace, CancellationToken.None));
            workspaceId = workspace.Id;
        }

        // Two writers capture the same row version before either commits.
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = await firstContext.Workspaces.SingleAsync(workspace => workspace.Id == workspaceId);
        var second = await secondContext.Workspaces.SingleAsync(workspace => workspace.Id == workspaceId);

        // The first writer renames and commits; its write wins and (on PostgreSQL) the
        // row version advances.
        first.Rename("First Writer", _renamedAt);
        await firstContext.SaveChangesAsync();

        // The second writer holds the now-stale snapshot and renames the same row.
        second.Rename("Second Writer", _renamedAt);

        if (database.IsPostgres)
        {
            // The stale xmin no longer matches, so the second SaveChanges fails loudly
            // instead of losing the first writer's update (CORE-CONC-001).
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());

            await using var verify = database.CreateContext();
            var persisted = await verify.Workspaces.SingleAsync(workspace => workspace.Id == workspaceId);
            Assert.Equal("First Writer", persisted.Name);
        }
        else
        {
            // SQLite carries no xmin token, so the interleaving is last-write-wins and
            // does not throw — proving the Npgsql-only mapping does not break the
            // default test provider.
            await secondContext.SaveChangesAsync();

            await using var verify = database.CreateContext();
            var persisted = await verify.Workspaces.SingleAsync(workspace => workspace.Id == workspaceId);
            Assert.Equal("Second Writer", persisted.Name);
        }
    }
}
