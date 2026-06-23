// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Workspaces;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="WorkspaceMemberRepository"/> (CORE-WS-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL
/// translation, the foreign keys into
/// <c>organizations</c>/<c>workspaces</c>/<c>users</c> and the unique
/// (<c>workspace_id</c>, <c>user_id</c>) index are exercised on every test run
/// without any database server or Docker. The behaviors under test
/// (scoped equality lookups, unique index enforcement, full isolation between
/// workspaces and between tenants, exact-role matching) are relational semantics
/// shared with PostgreSQL; provider-specific verification happens against
/// PostgreSQL in the deployment pipeline (livecore-deploy) and the isolation
/// test story.
///
/// The epic acceptance for Workspaces is "Workspace operations are generic and
/// authorized" with required authorization negative tests. At the aggregate +
/// repository level (no HTTP endpoints yet — those are CORE-WS-003), the
/// authorization-relevant property proven here is OBJECT-LEVEL ISOLATION
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization; docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked
/// before workspace boundary): a member of workspace W1 is not a member of
/// workspace W2; a cross-workspace lookup returns null/deny; a member of a
/// workspace in organization A is never resolved via organization B; a duplicate
/// (workspace, subject) is rejected leaving the existing row unchanged; the role
/// carried is exactly the stored one (exact match, no ordering); and the same
/// OIDC subject under two issuers is two users and therefore two memberships.
/// These negative cases are mandatory (AGENTS.md;
/// docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class WorkspaceMemberRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _otherSubject = "11111111-2222-3333-4444-555555555555";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public WorkspaceMemberRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads genuinely
        // round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on
        // so the FK constraints in the model are genuinely exercised.
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
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync(string issuer, string subjectId, string? displayName = null)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId, displayName),
            _createdAt);
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        Assert.Equal(UserProfileAddResult.Added, await repository.AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<WorkspaceMember> SeedMembershipAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = WorkspaceMember.Create(organizationId, workspaceId, userProfileId, role, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);
        Assert.Equal(
            WorkspaceMemberAddResult.Added,
            await repository.AddAsync(member, CancellationToken.None));
        return member;
    }

    [Fact]
    public async Task Membership_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedMembershipAsync(organization.Id, workspace.Id, user.Id, MembershipRole.Admin);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);
        var loaded = await repository.FindAsync(organization.Id, workspace.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(user.Id, loaded.UserProfileId);
        Assert.Equal(MembershipRole.Admin, loaded.Role);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lookup_for_a_subject_without_a_membership_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);
        var loaded = await repository.FindAsync(organization.Id, workspace.Id, user.Id, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_member_of_workspace_W1_is_not_a_member_of_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): the subject is a member
        // of workspace W1 only. Looking the subject up in workspace W2 must
        // return null even though both the subject and W2 exist; membership in
        // W1 grants nothing in W2. A cross-workspace lookup returns null/deny.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organization.Id, workspace1.Id, user.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var inW1 = await repository.FindAsync(organization.Id, workspace1.Id, user.Id, CancellationToken.None);
        var inW2 = await repository.FindAsync(organization.Id, workspace2.Id, user.Id, CancellationToken.None);

        Assert.NotNull(inW1);
        Assert.Equal(workspace1.Id, inW1.WorkspaceId);
        Assert.Null(inW2);
        Assert.True(await repository.IsMemberAsync(
            organization.Id, workspace1.Id, user.Id, CancellationToken.None));
        Assert.False(await repository.IsMemberAsync(
            organization.Id, workspace2.Id, user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_member_of_a_workspace_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary): the subject is a member of a workspace owned by
        // organization A. Looking the SAME workspace id and subject up under
        // organization B's id must return null/deny even though the workspace id
        // and subject id are correct: the wrong tenant denies access.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organizationA.Id, workspaceInA.Id, user.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var underA = await repository.FindAsync(
            organizationA.Id, workspaceInA.Id, user.Id, CancellationToken.None);
        var underB = await repository.FindAsync(
            organizationB.Id, workspaceInA.Id, user.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
        Assert.True(await repository.IsMemberAsync(
            organizationA.Id, workspaceInA.Id, user.Id, CancellationToken.None));
        Assert.False(await repository.IsMemberAsync(
            organizationB.Id, workspaceInA.Id, user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsMember_denies_a_subject_with_no_membership()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        // Deny-by-default: a user that exists but holds no membership in the
        // workspace is not a member.
        Assert.False(await repository.IsMemberAsync(
            organization.Id, workspace.Id, user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsMember_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.IsMemberAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.IsMemberAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.IsMemberAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task HasRole_matches_the_stored_role_exactly_and_only_within_the_scope()
    {
        // The role carried is exactly the stored one (exact match, no ordering),
        // and the role check is scoped: it is true only for the exact role in
        // the exact workspace under the exact organization (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md non-linear matrix).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspace1 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organizationA.Id, workspace1.Id, user.Id, MembershipRole.Host);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        // Exact role in the exact scope: true.
        Assert.True(await repository.HasRoleAsync(
            organizationA.Id, workspace1.Id, user.Id, MembershipRole.Host, CancellationToken.None));
        // A different role never matches, in either direction (non-linear).
        Assert.False(await repository.HasRoleAsync(
            organizationA.Id, workspace1.Id, user.Id, MembershipRole.Owner, CancellationToken.None));
        Assert.False(await repository.HasRoleAsync(
            organizationA.Id, workspace1.Id, user.Id, MembershipRole.Participant, CancellationToken.None));
        // The same role is never granted through a foreign workspace or a
        // foreign tenant.
        Assert.False(await repository.HasRoleAsync(
            organizationA.Id, workspace2.Id, user.Id, MembershipRole.Host, CancellationToken.None));
        Assert.False(await repository.HasRoleAsync(
            organizationB.Id, workspace1.Id, user.Id, MembershipRole.Host, CancellationToken.None));
    }

    [Fact]
    public async Task HasRole_rejects_an_undefined_role()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.HasRoleAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                (MembershipRole)999,
                CancellationToken.None));
    }

    [Fact]
    public async Task HasRole_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.HasRoleAsync(
                Guid.Empty,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                MembershipRole.Owner,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.HasRoleAsync(
                Guid.CreateVersion7(),
                Guid.Empty,
                Guid.CreateVersion7(),
                MembershipRole.Owner,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.HasRoleAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.Empty,
                MembershipRole.Owner,
                CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_membership_is_rejected_and_the_existing_row_stays_unchanged()
    {
        // Mandatory uniqueness test: the unique (workspace_id, user_id) index is
        // the database-level guarantee that a second writer can never create a
        // conflicting standing for the same subject in the same workspace
        // (threat T5). The duplicate's higher role must not overwrite the
        // existing standing.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var existing = await SeedMembershipAsync(
            organization.Id, workspace.Id, user.Id, MembershipRole.Participant);
        var duplicate = WorkspaceMember.Create(
            organization.Id, workspace.Id, user.Id, MembershipRole.Owner, _updatedAt);

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceMemberRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(WorkspaceMemberAddResult.DuplicateMembership, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceMemberRepository(context);
            var loaded = await repository.FindAsync(
                organization.Id, workspace.Id, user.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            // The original row (its id and its role) is untouched; the
            // duplicate's higher role never overwrote the existing standing.
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal(MembershipRole.Participant, loaded.Role);
        }
    }

    [Fact]
    public async Task The_same_subject_can_be_a_member_of_two_workspaces_with_independent_roles()
    {
        // Object-level isolation works in both directions: one subject may hold
        // a membership in each of two workspaces, and each membership is scoped
        // to and resolved only within its own workspace. The owner role in W1
        // never leaks into W2.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inW1 = await SeedMembershipAsync(organization.Id, workspace1.Id, user.Id, MembershipRole.Owner);
        var inW2 = await SeedMembershipAsync(
            organization.Id, workspace2.Id, user.Id, MembershipRole.Participant);

        Assert.NotEqual(inW1.Id, inW2.Id);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var loadedW1 = await repository.FindAsync(organization.Id, workspace1.Id, user.Id, CancellationToken.None);
        var loadedW2 = await repository.FindAsync(organization.Id, workspace2.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loadedW1);
        Assert.NotNull(loadedW2);
        Assert.Equal(MembershipRole.Owner, loadedW1.Role);
        Assert.Equal(MembershipRole.Participant, loadedW2.Role);
        Assert.True(loadedW2.HasRole(MembershipRole.Participant));
        Assert.False(loadedW2.HasRole(MembershipRole.Owner));
    }

    [Fact]
    public async Task Two_subjects_in_one_workspace_are_addressed_independently()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var otherUser = await SeedUserAsync(_issuer, _otherSubject);
        await SeedMembershipAsync(organization.Id, workspace.Id, user.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organization.Id, workspace.Id, otherUser.Id, MembershipRole.Observer);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var first = await repository.FindAsync(organization.Id, workspace.Id, user.Id, CancellationToken.None);
        var second = await repository.FindAsync(
            organization.Id, workspace.Id, otherUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MembershipRole.Owner, first.Role);
        Assert.Equal(MembershipRole.Observer, second.Role);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Same_oidc_subject_under_two_issuers_are_distinct_members()
    {
        // The subject is identified by the user profile surrogate id, which is
        // keyed by the OIDC identity pair (issuer, subject). The same OIDC
        // subject value under two issuers is two distinct users and therefore
        // two distinct memberships, even within one workspace (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var foreignIssuerUser = await SeedUserAsync(_foreignIssuer, _subject);

        Assert.NotEqual(user.Id, foreignIssuerUser.Id);

        await SeedMembershipAsync(organization.Id, workspace.Id, user.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organization.Id, workspace.Id, foreignIssuerUser.Id, MembershipRole.Auditor);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var first = await repository.FindAsync(organization.Id, workspace.Id, user.Id, CancellationToken.None);
        var second = await repository.FindAsync(
            organization.Id, workspace.Id, foreignIssuerUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MembershipRole.Owner, first.Role);
        Assert.Equal(MembershipRole.Auditor, second.Role);
    }

    [Fact]
    public async Task ListByWorkspace_returns_audience_safe_entries_scoped_to_the_workspace_and_tenant()
    {
        // CORE-WSM-001: the roster read returns the workspace's members with the membership id, the role and the
        // audience-safe display name joined read-only from the users profile, and is fully tenant- and
        // workspace-scoped (threat T5): a member of another workspace or another tenant is never returned.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspace1 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var named = await SeedUserAsync(_issuer, _subject, "Ada Lovelace");
        var unnamed = await SeedUserAsync(_issuer, _otherSubject);
        var inW2 = await SeedUserAsync(_foreignIssuer, _subject);
        var memberNamed = await SeedMembershipAsync(organizationA.Id, workspace1.Id, named.Id, MembershipRole.Owner);
        var memberUnnamed = await SeedMembershipAsync(
            organizationA.Id, workspace1.Id, unnamed.Id, MembershipRole.Participant);
        // A member of workspace2 must never appear in workspace1's roster.
        await SeedMembershipAsync(organizationA.Id, workspace2.Id, inW2.Id, MembershipRole.Host);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var roster = await repository.ListByWorkspaceAsync(
            organizationA.Id, workspace1.Id, 0, 50, CancellationToken.None);

        Assert.Equal(2, roster.Count);
        var namedEntry = Assert.Single(roster, entry => entry.Id == memberNamed.Id);
        Assert.Equal(named.Id, namedEntry.UserProfileId);
        Assert.Equal(MembershipRole.Owner, namedEntry.Role);
        Assert.Equal("Ada Lovelace", namedEntry.DisplayName);
        Assert.Equal(workspace1.Id, namedEntry.WorkspaceId);
        Assert.Equal(organizationA.Id, namedEntry.OrganizationId);

        // The member with no profile display name reads back a null display metadatum, never an error.
        var unnamedEntry = Assert.Single(roster, entry => entry.Id == memberUnnamed.Id);
        Assert.Null(unnamedEntry.DisplayName);
        Assert.Equal(MembershipRole.Participant, unnamedEntry.Role);

        // Tenant isolation: the same workspace id read under organization B's id returns nothing (threat T5).
        var underForeignTenant = await repository.ListByWorkspaceAsync(
            organizationB.Id, workspace1.Id, 0, 50, CancellationToken.None);
        Assert.Empty(underForeignTenant);
    }

    [Fact]
    public async Task ListByWorkspace_pages_bounded_and_ordered_by_id()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var userOne = await SeedUserAsync(_issuer, _subject);
        var userTwo = await SeedUserAsync(_issuer, _otherSubject);
        var userThree = await SeedUserAsync(_foreignIssuer, _subject);
        var first = await SeedMembershipAsync(organization.Id, workspace.Id, userOne.Id, MembershipRole.Owner);
        var second = await SeedMembershipAsync(organization.Id, workspace.Id, userTwo.Id, MembershipRole.Host);
        var third = await SeedMembershipAsync(
            organization.Id, workspace.Id, userThree.Id, MembershipRole.Participant);

        // The membership ids are time-ordered (UUIDv7), so the deterministic oldest-first order is by id.
        var expectedOrder = new[] { first.Id, second.Id, third.Id }.OrderBy(id => id).ToArray();

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        var firstPage = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, 0, 2, CancellationToken.None);
        var secondPage = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, 2, 2, CancellationToken.None);

        Assert.Equal(expectedOrder.Take(2), firstPage.Select(entry => entry.Id));
        Assert.Equal(expectedOrder.Skip(2), secondPage.Select(entry => entry.Id));
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids_and_out_of_range_paging()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.Empty, Guid.CreateVersion7(), 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(Guid.CreateVersion7(), Guid.Empty, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), -1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), 0, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_membership_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a
        // membership for a non-existent workspace is rejected, so a dangling
        // membership can never exist outside a real workspace boundary
        // (threat T5). The organization and subject are real to isolate the
        // workspace FK as the failing constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = WorkspaceMember.Create(
            organization.Id, Guid.CreateVersion7(), user.Id, MembershipRole.Owner, _createdAt);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_membership_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a membership whose tenant
        // does not exist is rejected even when the workspace and subject exist,
        // so the row can never carry a tenant boundary that is not a real
        // organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var ghost = WorkspaceMember.Create(
            Guid.CreateVersion7(), workspace.Id, user.Id, MembershipRole.Owner, _createdAt);

        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_persists_a_role_change_in_place_keeping_the_id_and_natural_key()
    {
        // CORE-WSM-002: a role change is the first in-place update to a membership. The persisted row keeps its
        // surrogate id and (workspace, subject) natural key; only the role and the updated timestamp move.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedMembershipAsync(
            organization.Id, workspace.Id, user.Id, MembershipRole.Participant);

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceMemberRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);
            loaded!.ChangeRole(MembershipRole.Admin, _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using var verifyContext = CreateContext();
        var verifyRepository = new WorkspaceMemberRepository(verifyContext);
        var reloaded = await verifyRepository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(seeded.Id, reloaded!.Id);
        Assert.Equal(workspace.Id, reloaded.WorkspaceId);
        Assert.Equal(user.Id, reloaded.UserProfileId);
        Assert.Equal(MembershipRole.Admin, reloaded.Role);
        Assert.Equal(_updatedAt, reloaded.UpdatedAt);
        // Still exactly one membership for the (workspace, subject) pair — an update, not an insert.
        Assert.Equal(1, await verifyContext.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspace.Id));
    }

    [Fact]
    public async Task UpdateAsync_rejects_a_null_member()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => repository.UpdateAsync(null!, CancellationToken.None));
    }
}
