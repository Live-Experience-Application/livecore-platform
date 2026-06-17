using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceInvitationRepository"/>
/// (CORE-WS-004), backed by the <c>workspace_invitations</c> table mapped in
/// <see cref="WorkspaceInvitationConfiguration"/>.
/// </summary>
internal sealed class WorkspaceInvitationRepository : IWorkspaceInvitationRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceInvitationRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitationAddResult> AddAsync(
        WorkspaceInvitation invitation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        _dbContext.WorkspaceInvitations.Add(invitation);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return WorkspaceInvitationAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by
            // a later SaveChanges on the same scope.
            _dbContext.Entry(invitation).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this token
            // hash exists now, the unique token-hash index rejected the insert
            // as a duplicate. Any other failure (for example a foreign-key
            // violation for a non-existent workspace or tenant) is rethrown
            // unchanged. The plaintext token is never involved here — only its
            // hash (threats T6/T7).
            var duplicateExists = await _dbContext.WorkspaceInvitations
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.TokenHash == invitation.TokenHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return WorkspaceInvitationAddResult.DuplicateToken;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitation?> FindByTokenHashAsync(
        Guid organizationId,
        Guid workspaceId,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        // Empty ids and malformed hashes can never address a stored invitation,
        // so the lookup fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!WorkspaceInvitationToken.IsValidHash(tokenHash))
        {
            throw new ArgumentException("Token hash violates the hash invariants.", nameof(tokenHash));
        }

        // All three predicates translate to parameterized SQL equality. Scoping
        // by the organization (the tenant, checked before the workspace
        // boundary) and the workspace means a matching token hash under another
        // organization or workspace is never returned: the token is only ever
        // honoured inside the scope it was minted for (threats T5/T6). The
        // unique token-hash index guarantees at most one row.
        return await _dbContext.WorkspaceInvitations
            .FirstOrDefaultAsync(
                invitation => invitation.OrganizationId == organizationId
                    && invitation.WorkspaceId == workspaceId
                    && invitation.TokenHash == tokenHash,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceInvitation?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored invitation (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id must not be empty.", nameof(invitationId));
        }

        // All three predicates translate to parameterized SQL equality. The id is the row's own key, but the
        // organization_id and workspace_id predicates additionally pin the tenant and workspace boundaries (the
        // organization boundary checked before the workspace boundary), so an invitation with that id in another
        // workspace, or in the same workspace id under a different organization, is never returned (threats
        // T1/T5). This by-id lookup never touches the token secret — neither the plaintext nor its hash.
        return await _dbContext.WorkspaceInvitations
            .FirstOrDefaultAsync(
                invitation => invitation.Id == invitationId
                    && invitation.OrganizationId == organizationId
                    && invitation.WorkspaceId == workspaceId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListPendingByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a tenant or workspace (ids are generated non-empty), so the lookup fails
        // fast instead of scanning the whole table.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // All three predicates translate to parameterized SQL equality. Scoping by the organization (the
        // tenant, checked before the workspace boundary) and the workspace means an invitation under another
        // organization or workspace is never returned (threats T1/T5); the tenant-scoped composite index leads
        // with organization_id (WorkspaceInvitationConfiguration). The status is persisted as its stable name
        // (HasConversion<string>), so EF translates this equality to the stored name; the lifecycle is
        // non-linear, so this is an EXACT Pending match, never an ordering comparison. The read is tracking-free
        // (it never mutates) and ordered oldest-first by the surrogate id, which is a time-ordered UUIDv7, so it
        // yields a deterministic chronological page WITHOUT ordering by a DateTimeOffset column (which the SQLite
        // provider cannot translate), mirroring every other tenant-scoped list query. The token hash rides along
        // on the aggregate but the endpoint projects to a PII-safe DTO that never emits it (threats T6/T7).
        return await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .Where(invitation => invitation.OrganizationId == organizationId
                && invitation.WorkspaceId == workspaceId
                && invitation.Status == WorkspaceInvitationStatus.Pending)
            .OrderBy(invitation => invitation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListPendingPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a tenant or workspace, so the lookup fails fast (mirrors the unbounded
        // list).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // The same tenant- AND workspace-scoped, Pending-only, oldest-first (UUIDv7 id) read as
        // ListPendingByWorkspaceAsync (threats T1/T5), but bounded by Skip/Take so an unbounded list is never
        // materialized (threat T9). Tracking-free (it never mutates); the endpoint projects to a PII-safe DTO
        // that never emits the token hash (threats T6/T7).
        return await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .Where(invitation => invitation.OrganizationId == organizationId
                && invitation.WorkspaceId == workspaceId
                && invitation.Status == WorkspaceInvitationStatus.Pending)
            .OrderBy(invitation => invitation.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(WorkspaceInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        // The invitation was loaded and mutated within this scope's change tracker; only the lifecycle
        // status and the update timestamp change. The tenant, workspace, role, token hash and expiry are
        // immutable on the aggregate, so an update can never move the row to another tenant or workspace
        // (threat T5) nor re-open a consumed token. On PostgreSQL the row carries an xmin optimistic-
        // concurrency token (LiveCoreDbContext), so two concurrent redemptions of one scoped token make the
        // second SaveChanges fail loudly (a DbUpdateConcurrencyException -> 409) instead of silently granting
        // a second membership: the single-use guarantee holds even under a race (threat T6).
        _dbContext.WorkspaceInvitations.Update(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> AnonymizeByInvitedEmailAsync(
        string invitedEmail,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        // A blank email can never address a stored invitation, so the erasure fails fast instead of matching
        // an arbitrary set of rows.
        if (string.IsNullOrWhiteSpace(invitedEmail))
        {
            throw new ArgumentException("Invited email must not be blank.", nameof(invitedEmail));
        }

        // CROSS-TENANT BY DESIGN (GDPR Art.17): the predicate matches the invited email ALONE, not a tenant or
        // workspace, so it scrubs the subject's email everywhere it was recorded. The match is exact (ordinal)
        // equality, which EF translates to parameterized SQL — the invited email is stored as trimmed,
        // case-preserved data. This is safe under threat T5 — it is keyed by the subject's own email, returns
        // only a count and is reachable only from the authorized erasure command. Load-then-mutate +
        // SaveChanges (the codebase's convention) so the anonymized rows participate in the ambient erasure
        // transaction.
        var invitations = await _dbContext.WorkspaceInvitations
            .Where(invitation => invitation.InvitedEmail == invitedEmail)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (invitations.Count == 0)
        {
            return 0;
        }

        foreach (var invitation in invitations)
        {
            invitation.AnonymizeInvitedEmail(updatedAt);
        }

        _dbContext.WorkspaceInvitations.UpdateRange(invitations);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return invitations.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListByInvitedEmailInOrganizationAsync(
        Guid organizationId,
        string invitedEmail,
        CancellationToken cancellationToken)
    {
        // An empty id or blank email can never address a stored invitation, so the read fails fast instead of
        // scanning the whole table.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (string.IsNullOrWhiteSpace(invitedEmail))
        {
            throw new ArgumentException("Invited email must not be blank.", nameof(invitedEmail));
        }

        // TENANT-SCOPED export read (CORE-PRIV-004): the predicate LEADS with the tenant column and matches the
        // invited email by EXACT (ordinal) equality — EF translates it to parameterized SQL against the trimmed,
        // case-preserved stored value — so an invitation addressed to the subject in another tenant is never
        // returned, and a near-match address is never returned (threat T5/T1). The read is tracking-free (the
        // export only reads back) and ordered oldest-first by the time-ordered surrogate id (UUIDv7),
        // provider-independent because SQLite cannot ORDER BY a DateTimeOffset. The token hash rides along on the
        // aggregate but the endpoint projects to a DTO that never emits it (threats T6/T7).
        return await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .Where(invitation => invitation.OrganizationId == organizationId
                && invitation.InvitedEmail == invitedEmail)
            .OrderBy(invitation => invitation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceInvitation>> ListTerminalForRetentionAsync(
        DateTimeOffset createdBefore,
        DateTimeOffset asOf,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // SYSTEM retention sweep (CORE-PRIV-003): deliberately spans all tenants/workspaces because the sweep is a
        // system job, not a tenant actor. The order is the time-ordered surrogate id (UUIDv7 derived from
        // CreatedAt, chronological and provider-independent — SQLite cannot ORDER BY or compare a DateTimeOffset),
        // oldest first, so the OLDEST invitations sort first. The bounded batch (Take(maxCount)) surfaces the
        // most-aged invitations; the terminal-state check (which compares ExpiresAt against asOf for a still-
        // pending invitation) AND the creation-age threshold are applied AFTER materialization. A pending
        // invitation whose token can still be redeemed (not expired at asOf) is NEVER returned — only
        // closed/expired/revoked invitations are purged. The read is tracking-free (it never mutates); the
        // returned aggregates carry the token hash, but the sweep purges by id and never reads it out (threats
        // T6/T7).
        var oldestInvitations = await _dbContext.WorkspaceInvitations
            .AsNoTracking()
            .OrderBy(invitation => invitation.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return oldestInvitations
            .Where(invitation => invitation.CreatedAt < createdBefore && IsTerminal(invitation, asOf))
            .ToList();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(WorkspaceInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        // Remove the terminal invitation row — the plaintext invited email goes with it (CORE-PRIV-003). The
        // lifecycle audit facts reference the invitation as a recorded fact (not a foreign key) and survive. A
        // concurrent purge of the same row makes this SaveChanges affect zero rows and throw
        // DbUpdateConcurrencyException, which the sweep treats as already-handled (concurrency-safe).
        _dbContext.WorkspaceInvitations.Remove(invitation);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the invitation is TERMINAL (closed/expired/revoked) at <paramref name="asOf"/>: accepted, revoked,
    /// or still pending but already past its expiry (so it can never be redeemed). A still-redeemable pending
    /// invitation is NOT terminal. The status is a non-linear lifecycle, so this is exact membership, never an
    /// ordering comparison.
    /// </summary>
    private static bool IsTerminal(WorkspaceInvitation invitation, DateTimeOffset asOf)
        => invitation.Status != WorkspaceInvitationStatus.Pending
            || invitation.ExpiresAt <= asOf;
}
