using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="AuditLogRepository"/> — the append-only
/// audit log persistence of the generic audit log (CORE-AUD-001, building on CORE-VIS-006).
///
/// They run against an in-memory SQLite database so the real model mapping, the <c>organization_id</c>
/// foreign key into <c>organizations</c> and the documented critical index
/// <c>audit_logs(organization_id, created_at)</c> are exercised on every run without a database server or
/// Docker (the relational semantics are shared with PostgreSQL; provider-specific verification happens in
/// the deployment pipeline). The suite covers the two operations the append-only contract exposes —
/// <see cref="AuditLogRepository.AppendAsync"/> (audit creation) and the tenant-scoped
/// <see cref="AuditLogRepository.ListByOrganizationAsync"/> read — for both a generic action and a
/// visibility change, plus the mandatory negative tenant-isolation cases (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; AGENTS.md; docs/17_DEFINITION_OF_DONE.md): one tenant's audit
/// records are NEVER returned through another tenant's id, and an entry can never be written for a tenant
/// that does not exist. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogRepositoryTests : IDisposable
{
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AuditLogRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step still
        // uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the tenant FK in the
        // model is genuinely exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private static AuditLogEntry GenericEntry(Guid organizationId, AuditAction action)
        => AuditLogEntry.Create(
            organizationId,
            workspaceId: Guid.NewGuid(),
            action,
            actorUserProfileId: Guid.NewGuid(),
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt: _now);

    [Fact]
    public async Task A_generic_entry_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entry = GenericEntry(organization.Id, AuditAction.SessionStarted);

        await using (var context = CreateContext())
        {
            await new AuditLogRepository(context).AppendAsync(entry, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var loaded = await new AuditLogRepository(context)
                .ListByOrganizationAsync(organization.Id, CancellationToken.None);

            var stored = Assert.Single(loaded);
            Assert.Equal(entry.Id, stored.Id);
            Assert.Equal(AuditAction.SessionStarted, stored.Action);
            Assert.Equal(entry.ActorUserProfileId, stored.ActorUserProfileId);
            Assert.Null(stored.ResourceType);
            Assert.Null(stored.ResourceId);
            Assert.Null(stored.NewState);
        }
    }

    [Fact]
    public async Task A_visibility_rule_change_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var entry = AuditLogEntry.ForVisibilityRuleChange(
            organization.Id, Guid.NewGuid(), Guid.NewGuid(), "ContentBlock", Guid.NewGuid(),
            targetParticipantId: null, previousState: "Hidden", newState: "Visible", createdAt: _now);

        await using (var context = CreateContext())
        {
            await new AuditLogRepository(context).AppendAsync(entry, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var loaded = await new AuditLogRepository(context)
                .ListByOrganizationAsync(organization.Id, CancellationToken.None);

            var stored = Assert.Single(loaded);
            Assert.Equal(AuditAction.VisibilityRuleChanged, stored.Action);
            Assert.Equal("ContentBlock", stored.ResourceType);
            Assert.Equal("Hidden", stored.PreviousState);
            Assert.Equal("Visible", stored.NewState);
        }
    }

    [Fact]
    public async Task ListByOrganization_never_returns_another_tenants_records()
    {
        // Mandatory negative tenant-isolation test (threat T5): an audit entry written for tenant A is
        // NEVER returned through tenant B's id, even though both tenants and both entries exist.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        var inA = GenericEntry(organizationA.Id, AuditAction.SessionStarted);
        var inB = GenericEntry(organizationB.Id, AuditAction.SessionEnded);

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            await repository.AppendAsync(inA, CancellationToken.None);
            await repository.AppendAsync(inB, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            var aRecords = await repository.ListByOrganizationAsync(organizationA.Id, CancellationToken.None);
            var bRecords = await repository.ListByOrganizationAsync(organizationB.Id, CancellationToken.None);

            Assert.Equal(inA.Id, Assert.Single(aRecords).Id);
            Assert.Equal(inB.Id, Assert.Single(bRecords).Id);
            // Tenant A's read never leaks tenant B's record (and vice versa).
            Assert.DoesNotContain(aRecords, e => e.Id == inB.Id);
            Assert.DoesNotContain(bRecords, e => e.Id == inA.Id);
        }
    }

    [Fact]
    public async Task Entries_are_listed_in_creation_order()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var first = GenericEntry(organization.Id, AuditAction.SessionStarted);
        var second = GenericEntry(organization.Id, AuditAction.SessionEnded);

        await using (var context = CreateContext())
        {
            var repository = new AuditLogRepository(context);
            await repository.AppendAsync(first, CancellationToken.None);
            await repository.AppendAsync(second, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var loaded = await new AuditLogRepository(context)
                .ListByOrganizationAsync(organization.Id, CancellationToken.None);

            // Ordered by the time-ordered surrogate id (UUIDv7), so reads are chronological.
            Assert.Collection(
                loaded,
                e => Assert.Equal(first.Id, e.Id),
                e => Assert.Equal(second.Id, e.Id));
        }
    }

    [Fact]
    public async Task Append_for_a_nonexistent_tenant_is_rejected_by_the_foreign_key()
    {
        // The organization_id foreign key is the row-level tenant boundary: an entry cannot be written
        // for a tenant that does not exist (threat T5).
        var orphan = GenericEntry(Guid.NewGuid(), AuditAction.SessionStarted);

        await using var context = CreateContext();
        var repository = new AuditLogRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AppendAsync(orphan, CancellationToken.None));
    }

    [Fact]
    public async Task ListByOrganization_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new AuditLogRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByOrganizationAsync(Guid.Empty, CancellationToken.None));
    }
}
