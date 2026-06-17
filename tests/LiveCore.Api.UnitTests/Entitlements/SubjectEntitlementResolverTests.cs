// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// POSITIVE and NEGATIVE entitlement lookup tests for <see cref="SubjectEntitlementResolver"/> (CORE-ENTL-002) —
/// the required tests of the subject entitlement assignment and lookup story. The resolver is the single
/// server-side path that turns a subject's stored assignments into its effective premium state, so these tests
/// pin the acceptance criterion "User-visible premium state comes only from server entitlements"
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md): a granted, in-force entitlement resolves to its value
/// (positive), while an absent, revoked, or other-subject entitlement resolves to NOT entitled (negative,
/// fail-closed).
///
/// They run against an in-memory SQLite database through the real <see cref="SubjectEntitlementRepository"/>, so
/// the active-only read and the per-subject scoping are exercised in SQL exactly as in production. All fixtures
/// are generic (AGENTS.md).
/// </summary>
public sealed class SubjectEntitlementResolverTests : IDisposable
{
    private static readonly DateTimeOffset _grantedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SubjectEntitlementResolverTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private SubjectEntitlementResolver CreateResolver(LiveCoreDbContext context)
        => new(new SubjectEntitlementRepository(context));

    private async Task<EntitlementDefinition> SeedDefinitionAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        context.EntitlementDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    private async Task SeedAssignmentAsync(SubjectEntitlement assignment)
    {
        await using var context = CreateContext();
        var repository = new SubjectEntitlementRepository(context);
        Assert.Equal(SubjectEntitlementAddResult.Added, await repository.AddAsync(assignment, CancellationToken.None));
    }

    private static EntitlementDefinition FlagDefinition(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Flag", null, _grantedAt);

    private static EntitlementDefinition QuotaDefinition(string key)
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _grantedAt);

    // --- Positive ---------------------------------------------------------------

    [Fact]
    public async Task Resolve_returns_the_granted_flag_and_quota_for_a_subject()
    {
        var subjectId = Guid.CreateVersion7();
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        var workspaceLimit = await SeedDefinitionAsync(QuotaDefinition("workspace.active.max"));
        await SeedAssignmentAsync(SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, subjectId, adFree, value: true, null, _grantedAt));
        await SeedAssignmentAsync(SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, subjectId, workspaceLimit, limit: 5, null, _grantedAt));

        await using var context = CreateContext();
        var effective = await CreateResolver(context).ResolveAsync(
            EntitlementSubjectType.User, subjectId, CancellationToken.None);

        Assert.True(effective.IsFlagEnabled("ads.disabled"));
        Assert.True(effective.TryGetQuotaLimit("workspace.active.max", out var limit));
        Assert.Equal(5, limit);
        Assert.Equal(2, effective.Count);
    }

    // --- Negative: nothing granted ----------------------------------------------

    [Fact]
    public async Task Resolve_for_a_subject_with_no_assignments_is_entitled_to_nothing()
    {
        await using var context = CreateContext();
        var effective = await CreateResolver(context).ResolveAsync(
            EntitlementSubjectType.User, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(0, effective.Count);
        Assert.False(effective.IsFlagEnabled("ads.disabled")); // a client cannot assert premium the server never granted
    }

    // --- Negative: revoked ------------------------------------------------------

    [Fact]
    public async Task Resolve_excludes_a_revoked_assignment()
    {
        var subjectId = Guid.CreateVersion7();
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        var revoked = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, subjectId, adFree, value: true, null, _grantedAt);
        revoked.Revoke(_grantedAt.AddHours(1));
        await SeedAssignmentAsync(revoked);

        await using var context = CreateContext();
        var effective = await CreateResolver(context).ResolveAsync(
            EntitlementSubjectType.User, subjectId, CancellationToken.None);

        // A refund / cancellation / downgrade removes the premium state (docs/21).
        Assert.False(effective.IsFlagEnabled("ads.disabled"));
        Assert.Equal(0, effective.Count);
    }

    // --- Negative: cross-subject isolation --------------------------------------

    [Fact]
    public async Task Resolve_never_returns_another_subjects_premium_state()
    {
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        await SeedAssignmentAsync(SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, subjectA, adFree, value: true, null, _grantedAt));

        await using var context = CreateContext();
        var resolver = CreateResolver(context);

        // Subject A is premium; subject B (never granted) is not — premium state is keyed strictly by subject.
        Assert.True((await resolver.ResolveAsync(EntitlementSubjectType.User, subjectA, CancellationToken.None))
            .IsFlagEnabled("ads.disabled"));
        Assert.False((await resolver.ResolveAsync(EntitlementSubjectType.User, subjectB, CancellationToken.None))
            .IsFlagEnabled("ads.disabled"));
    }

    // --- Negative: cross-subject-type isolation ---------------------------------

    [Fact]
    public async Task Resolve_does_not_cross_subject_types_sharing_a_guid()
    {
        var sharedId = Guid.CreateVersion7();
        var adFree = await SeedDefinitionAsync(FlagDefinition("ads.disabled"));
        await SeedAssignmentAsync(SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, sharedId, adFree, value: true, null, _grantedAt));

        await using var context = CreateContext();
        var resolver = CreateResolver(context);

        // The same guid as a workspace subject is a different subject: it has no entitlements.
        Assert.True((await resolver.ResolveAsync(EntitlementSubjectType.User, sharedId, CancellationToken.None))
            .IsFlagEnabled("ads.disabled"));
        Assert.Equal(0, (await resolver.ResolveAsync(EntitlementSubjectType.Workspace, sharedId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Resolve_rejects_an_empty_subject_id()
    {
        await using var context = CreateContext();
        var resolver = CreateResolver(context);

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(
            EntitlementSubjectType.User, Guid.Empty, CancellationToken.None));
    }
}
