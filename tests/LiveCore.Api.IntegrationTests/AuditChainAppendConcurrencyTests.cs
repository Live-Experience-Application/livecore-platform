// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Real WRITE-CONCURRENCY coverage for the audit-log append path on PostgreSQL (CORE-CONC-008). Every audit
/// append now runs inside a transactional unit of work (CORE-CONC-002, <see cref="TransactionalUnitOfWork"/>): the
/// per-tenant <c>audit_log_sequences</c> allocation, the previous-hash read and the insert commit together, so the
/// sequence-counter row lock is held until the insert commits. Before this story the previously direct appends
/// (the organization/workspace member changes, the personal-data export, the quota-exceeded fact) allocated the
/// number in its own auto-committed statement, releasing the lock BEFORE the insert — so two concurrent
/// same-tenant appends could each read a predecessor the other had not committed yet and seal off a FORKED chain,
/// which the <see cref="AuditLogChainVerifier"/> (CORE-SEC-003) then reports as tampering: a false positive that
/// masks real detection.
///
/// <para>
/// The test races MANY appends for the SAME tenant, each on its OWN <see cref="LiveCoreDbContext"/> (its own DI
/// scope, and so its own Npgsql connection on the PostgreSQL leg). On the CI <c>integration-postgres</c> leg they
/// run TRULY concurrently, so the second appender genuinely contends on the sequence row lock the first holds; the
/// unit of work makes them serialize and chain off the just-committed predecessor instead of forking. On the
/// default SQLite path the single shared connection cannot overlap, so the appends run SEQUENTIALLY — the same
/// gap-free, verifiable chain results, only without the true concurrency the PostgreSQL leg adds, keeping the test
/// green on both providers. This mirrors <see cref="MoneyPathWriteConcurrencyTests"/>.
/// </para>
///
/// <para>
/// The invariant the story asks for: many concurrent same-tenant audited actions produce a SINGLE unbroken,
/// gap-free hash chain — sequences exactly <c>1..N</c>, no fork, and the chain verifier reports it valid (no
/// false-positive tamper detection). All fixtures are generic Core vocabulary (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </para>
/// </summary>
public sealed class AuditChainAppendConcurrencyTests
{
    private const string _slug = "northwind-labs";

    // Several appends contend at once; PostgresTestDatabase caps each per-test pool at 5 connections, so at most a
    // handful overlap and the rest queue for a connection — the lock holder always owns its own connection and can
    // always commit, so the race makes progress without deadlocking.
    private const int _appendCount = 12;

    [Fact]
    public async Task Many_concurrent_same_tenant_appends_form_a_single_unbroken_gap_free_chain()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var organizationId = Guid.Empty;
        await factory.SeedAsync(async context =>
            organizationId = (await context.AddOrganizationAsync(_slug)).Id);

        // Distinct entries (own ids/actors), all for the SAME tenant, appended concurrently.
        var entries = Enumerable.Range(0, _appendCount)
            .Select(i => GenericEntry(organizationId, TestData.SeedTime.AddSeconds(i)))
            .ToArray();

        await AppendConcurrentlyAsync(factory, entries);

        // Read the persisted chain in append (sequence) order through a fresh scope.
        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var repository = new AuditLogRepository(verifyContext);

        var chain = await repository.ListChainByOrganizationAsync(organizationId, CancellationToken.None);

        // Every append persisted exactly once, on a strictly monotonic, gap-free 1..N sequence: no number skipped,
        // none duplicated, no concurrent appender forked off its own genesis.
        Assert.Equal(_appendCount, chain.Count);
        Assert.Equal(
            Enumerable.Range(1, _appendCount).Select(i => (long)i),
            chain.Select(entry => entry.Sequence));
        Assert.Equal(_appendCount, chain.Select(entry => entry.Id).Distinct().Count());

        // The crown-jewel invariant: the verifier reports a single unbroken chain — no fork, no false-positive
        // tamper detection masking real detection (CORE-SEC-003).
        var result = await new AuditLogChainVerifier(repository)
            .VerifyAsync(organizationId, CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal(_appendCount, result.VerifiedCount);
        Assert.Equal(0, result.LegacyCount);
        Assert.Null(result.FirstBrokenEntryId);
    }

    /// <summary>
    /// Appends every entry through the production <see cref="AuditLogRepository.AppendAsync"/> path, each on its
    /// own DI scope/context. On the PostgreSQL leg they run TRULY concurrently (each on its own Npgsql connection),
    /// so the sequence row lock is genuinely contended; on SQLite (one shared connection that cannot overlap) they
    /// run sequentially. The same unbroken gap-free chain results either way.
    /// </summary>
    private static async Task AppendConcurrentlyAsync(WorkspaceApiFactory factory, AuditLogEntry[] entries)
    {
        if (PostgresTestDatabase.IsConfigured)
        {
            await Task.WhenAll(entries.Select(entry => Task.Run(() => AppendOnOwnScopeAsync(factory, entry))))
                .ConfigureAwait(false);
            return;
        }

        foreach (var entry in entries)
        {
            await AppendOnOwnScopeAsync(factory, entry).ConfigureAwait(false);
        }
    }

    private static async Task AppendOnOwnScopeAsync(WorkspaceApiFactory factory, AuditLogEntry entry)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        await new AuditLogRepository(context).AppendAsync(entry, CancellationToken.None);
    }

    private static AuditLogEntry GenericEntry(Guid organizationId, DateTimeOffset createdAt)
        => AuditLogEntry.Create(
            organizationId,
            workspaceId: Guid.CreateVersion7(),
            AuditAction.SessionStarted,
            actorUserProfileId: Guid.CreateVersion7(),
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
}
