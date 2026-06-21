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
/// <see cref="WorkspaceInvitationRepository"/> (CORE-WS-004).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), mirroring the workspace membership
/// repository tests, so the real model mapping, the foreign keys into
/// <c>organizations</c>/<c>workspaces</c>, the unique token-hash index and the
/// tenant/workspace scoping are exercised without any database server.
///
/// The security-relevant properties proven here (threats T5/T6/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md): only the token HASH is ever persisted (the
/// plaintext never reaches the database), a token hash is resolved only within
/// the exact (organization, workspace) it was scoped to, the unique token-hash
/// index rejects a duplicate token, and dangling invitations (non-existent
/// workspace or tenant) are rejected by the foreign keys.
/// </summary>
public sealed class WorkspaceInvitationRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";
    private const string _email = "invitee@example.test";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public WorkspaceInvitationRepositoryTests()
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

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            OrganizationAddResult.Added,
            await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            WorkspaceAddResult.Added,
            await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync(string issuer, string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId),
            _createdAt);
        await using var context = CreateContext();
        Assert.Equal(
            UserProfileAddResult.Added,
            await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    [Fact]
    public async Task Invitation_round_trips_and_only_the_hash_is_persisted()
    {
        // T6/T7: a created invitation persists only the token hash. The plaintext
        // must never be found anywhere in the stored row, and the loaded row's
        // hash must match the hash of the original plaintext.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var invitation = WorkspaceInvitation.Create(
            organization.Id, workspace.Id, _email, MembershipRole.Admin, _createdAt, out var token);

        await using (var context = CreateContext())
        {
            Assert.Equal(
                WorkspaceInvitationAddResult.Added,
                await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var loaded = await new WorkspaceInvitationRepository(context).FindByTokenHashAsync(
                organization.Id, workspace.Id, WorkspaceInvitationToken.Hash(token), CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(invitation.Id, loaded.Id);
            Assert.Equal(MembershipRole.Admin, loaded.Role);
            Assert.Equal(WorkspaceInvitationStatus.Pending, loaded.Status);
            Assert.Equal(WorkspaceInvitationToken.Hash(token), loaded.TokenHash);
            // The plaintext token is NOT the stored hash.
            Assert.NotEqual(token, loaded.TokenHash);
            // A presented token still matches via the aggregate.
            Assert.True(loaded.MatchesToken(token));
        }

        // Defence in depth: scan the raw stored column for the plaintext — it
        // must not appear anywhere at rest (threats T6/T7).
        await using (var context = CreateContext())
        {
            var storedHashes = await context.WorkspaceInvitations
                .Select(i => i.TokenHash)
                .ToListAsync();
            Assert.DoesNotContain(token, storedHashes);
        }
    }

    [Fact]
    public async Task Token_hash_is_resolved_only_within_its_scope()
    {
        // T5/T6: an invitation for a workspace in organization A is never resolved
        // through organization B's id, nor through another workspace's id, even
        // when the token hash is correct. The token is honoured only inside the
        // scope it was minted for.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var invitation = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, _email, MembershipRole.Host, _createdAt, out var token);

        await using (var context = CreateContext())
        {
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        await using var read = CreateContext();
        var repository = new WorkspaceInvitationRepository(read);
        var hash = WorkspaceInvitationToken.Hash(token);

        // Correct scope: found.
        Assert.NotNull(await repository.FindByTokenHashAsync(
            organizationA.Id, workspaceA.Id, hash, CancellationToken.None));
        // Wrong tenant: denied.
        Assert.Null(await repository.FindByTokenHashAsync(
            organizationB.Id, workspaceA.Id, hash, CancellationToken.None));
        // Wrong workspace: denied.
        Assert.Null(await repository.FindByTokenHashAsync(
            organizationA.Id, workspaceB.Id, hash, CancellationToken.None));
    }

    [Fact]
    public async Task A_wrong_token_hash_resolves_to_nothing()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var invitation = WorkspaceInvitation.Create(
            organization.Id, workspace.Id, _email, MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        await using var read = CreateContext();
        var otherHash = WorkspaceInvitationToken.Hash(WorkspaceInvitationToken.Generate());
        Assert.Null(await new WorkspaceInvitationRepository(read).FindByTokenHashAsync(
            organization.Id, workspace.Id, otherHash, CancellationToken.None));
    }

    [Fact]
    public async Task FindByTokenHash_rejects_empty_ids_and_malformed_hashes()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceInvitationRepository(context);
        var validHash = WorkspaceInvitationToken.Hash(WorkspaceInvitationToken.Generate());

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByTokenHashAsync(
            Guid.Empty, Guid.CreateVersion7(), validHash, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByTokenHashAsync(
            Guid.CreateVersion7(), Guid.Empty, validHash, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByTokenHashAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "not-a-hash", CancellationToken.None));
    }

    [Fact]
    public async Task FindById_resolves_an_invitation_only_within_its_scope()
    {
        // CORE-WS-007 (T1/T5): the by-id lookup the revoke command uses is scoped by BOTH organization and
        // workspace, so an invitation's surrogate id is honoured only inside the exact (organization, workspace)
        // it belongs to: a correct id under another tenant or another workspace is never returned.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var invitation = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, _email, MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        await using var read = CreateContext();
        var repository = new WorkspaceInvitationRepository(read);

        // Correct scope: found.
        Assert.NotNull(await repository.FindByIdAsync(
            organizationA.Id, workspaceA.Id, invitation.Id, CancellationToken.None));
        // Wrong tenant: denied.
        Assert.Null(await repository.FindByIdAsync(
            organizationB.Id, workspaceA.Id, invitation.Id, CancellationToken.None));
        // Wrong workspace: denied.
        Assert.Null(await repository.FindByIdAsync(
            organizationA.Id, workspaceB.Id, invitation.Id, CancellationToken.None));
        // Unknown id within the correct scope: denied.
        Assert.Null(await repository.FindByIdAsync(
            organizationA.Id, workspaceA.Id, Guid.CreateVersion7(), CancellationToken.None));
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceInvitationRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Update_persists_the_revoked_status_transition()
    {
        // CORE-WS-007: the revoke command transitions the invitation Pending -> Revoked and persists it through
        // UpdateAsync. The stored row must reflect the new status, so a later read (and any redeem) can never see
        // the revoked token as still Pending — the revocation control holds at rest (threat T6).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var invitation = WorkspaceInvitation.Create(
            organization.Id, workspace.Id, _email, MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        // Load by id, revoke and persist through the repository, exactly as the revoke handler does.
        await using (var context = CreateContext())
        {
            var repository = new WorkspaceInvitationRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, invitation.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Revoke(_createdAt.AddMinutes(1));
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var stored = await context.WorkspaceInvitations.SingleAsync(i => i.Id == invitation.Id);
            Assert.Equal(WorkspaceInvitationStatus.Revoked, stored.Status);
            // The immutable scope is untouched by the update.
            Assert.Equal(organization.Id, stored.OrganizationId);
            Assert.Equal(workspace.Id, stored.WorkspaceId);
            // A revoked invitation is no longer redeemable, so the token can never be used.
            Assert.False(stored.IsRedeemable(_createdAt.AddMinutes(2)));
        }
    }

    [Fact]
    public async Task The_token_hash_column_is_uniquely_indexed()
    {
        // T6: one scoped token addresses at most one invitation. The public
        // create API yields a fresh 256-bit token per invitation, so two rows
        // cannot legitimately share a hash; the database-level guarantee is the
        // unique index on token_hash, asserted here against the model metadata.
        await using var context = CreateContext();
        var index = context.Model
            .FindEntityType(typeof(WorkspaceInvitation))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1
                && i.Properties[0].Name == nameof(WorkspaceInvitation.TokenHash));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public async Task Re_adding_the_same_invitation_is_reported_as_a_duplicate_token()
    {
        // T6: persisting a row whose token hash already exists is rejected as a
        // duplicate (the unique token-hash index fires), and the repository
        // reports DuplicateToken rather than throwing. Re-adding the same
        // aggregate instance carries the same token hash, exercising exactly this
        // path (a replay of an already-stored scoped token).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var invitation = WorkspaceInvitation.Create(
            organization.Id, workspace.Id, _email, MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            Assert.Equal(
                WorkspaceInvitationAddResult.Added,
                await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var result = await new WorkspaceInvitationRepository(context)
                .AddAsync(invitation, CancellationToken.None);
            Assert.Equal(WorkspaceInvitationAddResult.DuplicateToken, result);
        }
    }

    [Fact]
    public async Task Update_persists_the_redeemed_status_transition()
    {
        // CORE-WS-006: the redeem command transitions the invitation Pending -> Accepted and persists it
        // through UpdateAsync. The stored row must reflect the new single-use status, so a later read can never
        // see the consumed token as still Pending (the single-use guarantee at rest).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var invitation = WorkspaceInvitation.Create(
            organization.Id, workspace.Id, _email, MembershipRole.Host, _createdAt, out var token);

        await using (var context = CreateContext())
        {
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        // Load, redeem and persist through the repository, exactly as the accept handler does inside its unit
        // of work.
        await using (var context = CreateContext())
        {
            var repository = new WorkspaceInvitationRepository(context);
            var loaded = await repository.FindByTokenHashAsync(
                organization.Id, workspace.Id, WorkspaceInvitationToken.Hash(token), CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Redeem(_createdAt.AddMinutes(1));
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var stored = await context.WorkspaceInvitations.SingleAsync(i => i.Id == invitation.Id);
            Assert.Equal(WorkspaceInvitationStatus.Accepted, stored.Status);
            // The immutable scope is untouched by the update.
            Assert.Equal(organization.Id, stored.OrganizationId);
            Assert.Equal(workspace.Id, stored.WorkspaceId);
            Assert.Equal(MembershipRole.Host, stored.Role);
            // A redeemed invitation is no longer redeemable, so the single-use token can never be reused.
            Assert.False(stored.IsRedeemable(_createdAt.AddMinutes(2)));
        }
    }

    [Fact]
    public async Task Update_rejects_a_null_invitation()
    {
        await using var context = CreateContext();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new WorkspaceInvitationRepository(context).UpdateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task An_invitation_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced: an invitation for a
        // non-existent workspace is rejected, so a dangling invite can never
        // exist outside a real workspace boundary (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = WorkspaceInvitation.Create(
            organization.Id, Guid.CreateVersion7(), _email, MembershipRole.Host, _createdAt, out _);

        await using var context = CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(
            () => new WorkspaceInvitationRepository(context).AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task An_invitation_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: an invitation whose tenant
        // does not exist is rejected even when the workspace exists, so the row
        // can never carry a tenant boundary that is not a real organization
        // (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = WorkspaceInvitation.Create(
            Guid.CreateVersion7(), workspace.Id, _email, MembershipRole.Host, _createdAt, out _);

        await using var context = CreateContext();
        await Assert.ThrowsAsync<DbUpdateException>(
            () => new WorkspaceInvitationRepository(context).AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task ListPendingByWorkspace_returns_only_pending_invitations_within_the_scope()
    {
        // CORE-WS-008 (T1/T5): the pending-invitations list is scoped by BOTH organization and workspace and
        // filtered to the Pending lifecycle status. An accepted or revoked invitation, an invitation in another
        // workspace of the same tenant, and an invitation in another tenant must all be excluded — only the
        // workspace's own outstanding invites are returned, oldest first.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var otherWorkspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, "b-show");

        // Two pending invitations in workspace A, created at distinct times so the order is deterministic.
        var pendingOne = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, "one@example.test", MembershipRole.Host, _createdAt, out _);
        var pendingTwo = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, "two@example.test", MembershipRole.Admin, _createdAt.AddMinutes(1), out _);
        // An accepted and a revoked invitation in workspace A (must be excluded).
        var accepted = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, "accepted@example.test", MembershipRole.Host, _createdAt, out _);
        accepted.Redeem(_createdAt.AddMinutes(2));
        var revoked = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, "revoked@example.test", MembershipRole.Host, _createdAt, out _);
        revoked.Revoke(_createdAt.AddMinutes(2));
        // A pending invitation in another workspace of the same tenant, and one in another tenant (excluded).
        var pendingOtherWorkspace = WorkspaceInvitation.Create(
            organizationA.Id, otherWorkspaceA.Id, "other-ws@example.test", MembershipRole.Host, _createdAt, out _);
        var pendingOtherTenant = WorkspaceInvitation.Create(
            organizationB.Id, workspaceB.Id, "other-org@example.test", MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceInvitationRepository(context);
            foreach (var invitation in new[]
                { pendingOne, pendingTwo, accepted, revoked, pendingOtherWorkspace, pendingOtherTenant })
            {
                await repository.AddAsync(invitation, CancellationToken.None);
            }
        }

        await using var read = CreateContext();
        var pending = await new WorkspaceInvitationRepository(read)
            .ListPendingByWorkspaceAsync(organizationA.Id, workspaceA.Id, CancellationToken.None);

        // Exactly the two pending invitations of workspace A, oldest first.
        Assert.Equal(new[] { pendingOne.Id, pendingTwo.Id }, pending.Select(i => i.Id).ToArray());
        Assert.All(pending, i => Assert.Equal(WorkspaceInvitationStatus.Pending, i.Status));

        // Wrong tenant and wrong workspace yield nothing (never another scope's invites).
        Assert.Empty(await new WorkspaceInvitationRepository(read)
            .ListPendingByWorkspaceAsync(organizationB.Id, workspaceA.Id, CancellationToken.None));
        Assert.Empty(await new WorkspaceInvitationRepository(read)
            .ListPendingByWorkspaceAsync(organizationA.Id, workspaceB.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListPendingByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceInvitationRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListPendingByWorkspaceAsync(
            Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListPendingByWorkspaceAsync(
            Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListPendingPageByInvitedEmailInOrganizations_returns_only_matching_pending_invitations_across_the_tenant_set()
    {
        // CORE-INV-002 (T1/T5/T6): the self-discovery read keys ONLY on the invited email AND restricts to the
        // caller's claimed tenant set. A Pending invitation addressed to the email in EITHER claimed tenant is
        // returned; an accepted/revoked one is excluded by the Pending filter; an invitation to a DIFFERENT email,
        // or in a tenant NOT in the set, is never returned. (Expiry is excluded by the endpoint, not this read.)
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var organizationC = await SeedOrganizationAsync("globex-inc");
        var workspaceA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organizationB.Id, "b-show");
        var workspaceC = await SeedWorkspaceAsync(organizationC.Id, "c-show");
        const string mine = "alice@example.test";

        // Two pending invitations to the caller's email, one in each claimed tenant (A and B), distinct times.
        var pendingInA = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, mine, MembershipRole.Host, _createdAt, out _);
        var pendingInB = WorkspaceInvitation.Create(
            organizationB.Id, workspaceB.Id, mine, MembershipRole.Admin, _createdAt.AddMinutes(1), out _);
        // A revoked and an accepted invitation to the caller's email in A (excluded by the Pending filter).
        var revoked = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, mine, MembershipRole.Host, _createdAt, out _);
        revoked.Revoke(_createdAt.AddMinutes(2));
        var accepted = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, mine, MembershipRole.Host, _createdAt, out _);
        accepted.Redeem(_createdAt.AddMinutes(2));
        // A pending invitation to a DIFFERENT email in A (excluded by the email key).
        var someoneElse = WorkspaceInvitation.Create(
            organizationA.Id, workspaceA.Id, "bob@example.test", MembershipRole.Host, _createdAt, out _);
        // A pending invitation to the caller's email in tenant C, which is NOT in the claimed set (excluded).
        var pendingInC = WorkspaceInvitation.Create(
            organizationC.Id, workspaceC.Id, mine, MembershipRole.Host, _createdAt, out _);

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceInvitationRepository(context);
            foreach (var invitation in new[]
                { pendingInA, pendingInB, revoked, accepted, someoneElse, pendingInC })
            {
                await repository.AddAsync(invitation, CancellationToken.None);
            }
        }

        await using var read = CreateContext();
        var repositoryRead = new WorkspaceInvitationRepository(read);

        // Claimed tenants = { A, B }. Exactly the two pending caller-addressed invitations, oldest first.
        var page = await repositoryRead.ListPendingPageByInvitedEmailInOrganizationsAsync(
            new[] { organizationA.Id, organizationB.Id }, mine, skip: 0, take: 50, CancellationToken.None);
        Assert.Equal(new[] { pendingInA.Id, pendingInB.Id }, page.Select(i => i.Id).ToArray());
        Assert.All(page, i => Assert.Equal(WorkspaceInvitationStatus.Pending, i.Status));

        // Tenant C is never searched, so its caller-addressed invitation is never returned even though the email
        // matches (threat T5).
        Assert.DoesNotContain(page, i => i.Id == pendingInC.Id);
    }

    [Fact]
    public async Task ListPendingPageByInvitedEmailInOrganizations_pages_with_an_over_fetch()
    {
        // Bounded paging (threat T9): Skip/Take return exactly the requested window in oldest-first id order, so
        // the endpoint's over-fetch-by-one HasMore technique works.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        const string mine = "alice@example.test";

        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var invitation = WorkspaceInvitation.Create(
                organization.Id, workspace.Id, mine, MembershipRole.Host, _createdAt.AddMinutes(i), out _);
            created.Add(invitation.Id);
            await using var context = CreateContext();
            await new WorkspaceInvitationRepository(context).AddAsync(invitation, CancellationToken.None);
        }

        await using var read = CreateContext();
        var repository = new WorkspaceInvitationRepository(read);

        // First page of 2 (+1 over-fetch = 3): the three oldest, in order.
        var firstPage = await repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            new[] { organization.Id }, mine, skip: 0, take: 3, CancellationToken.None);
        Assert.Equal(created.Take(3).ToArray(), firstPage.Select(i => i.Id).ToArray());

        // Second page (skip 2, take 3): the remaining three.
        var secondPage = await repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            new[] { organization.Id }, mine, skip: 2, take: 3, CancellationToken.None);
        Assert.Equal(created.Skip(2).ToArray(), secondPage.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task ListPendingPageByInvitedEmailInOrganizations_returns_empty_for_an_empty_tenant_set()
    {
        // A caller with no claimed tenant matches nothing — and the read short-circuits without a query.
        await using var context = CreateContext();
        var repository = new WorkspaceInvitationRepository(context);

        var page = await repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            Array.Empty<Guid>(), "alice@example.test", skip: 0, take: 50, CancellationToken.None);

        Assert.Empty(page);
    }

    [Fact]
    public async Task ListPendingPageByInvitedEmailInOrganizations_rejects_a_blank_email_and_bad_paging()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceInvitationRepository(context);
        var ids = new[] { Guid.CreateVersion7() };

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            ids, "  ", skip: 0, take: 50, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            null!, "alice@example.test", skip: 0, take: 50, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            ids, "alice@example.test", skip: -1, take: 50, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListPendingPageByInvitedEmailInOrganizationsAsync(
            ids, "alice@example.test", skip: 0, take: 0, CancellationToken.None));
    }
}
