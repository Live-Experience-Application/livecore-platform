using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Participants;

/// <summary>
/// EF Core implementation of <see cref="IParticipantRepository"/> (CORE-SES-001),
/// backed by the <c>participants</c> table mapped in
/// <see cref="ParticipantConfiguration"/>.
/// </summary>
internal sealed class ParticipantRepository : IParticipantRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public ParticipantRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Participant?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored participant (ids are generated
        // non-empty), so the lookup fails fast instead of returning an arbitrary
        // row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading
        // with the tenant column. The lookup is exactly tenant- and
        // workspace-scoped, so a participant under another organization or
        // workspace is never returned even when the surrogate id matches
        // (threat T5/T1).
        return await _dbContext.Participants
            .FirstOrDefaultAsync(
                participant => participant.OrganizationId == organizationId
                    && participant.WorkspaceId == workspaceId
                    && participant.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Participant?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored participant (ids are generated
        // non-empty), so the lookup fails fast instead of returning an arbitrary
        // row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(id));
        }

        // The predicate LEADS with the tenant column, so the lookup is exactly
        // tenant-scoped: a participant under another organization is never
        // returned even when the surrogate id matches (threat T5/T1). The
        // workspace is deliberately NOT part of the predicate because the
        // by-participant-id feed route does not know it up front; it is read off
        // the returned row (Participant.WorkspaceId) so the caller can authorize
        // against that workspace AFTER the tenant boundary has been enforced. The
        // returned participant also carries its user link and status for the
        // own-feed/preview decision and the removed-participant guard.
        return await _dbContext.Participants
            .FirstOrDefaultAsync(
                participant => participant.OrganizationId == organizationId
                    && participant.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Participant>> ListActiveByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's participants, so the
        // lookup fails fast instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column, then matches the workspace and
        // the Active status, so the list is exactly tenant- and workspace-scoped and
        // excludes soft-removed participants (threat T5/T1). The order is by the
        // time-ordered surrogate id (UUIDv7), which is chronological and
        // provider-independent — SQLite cannot ORDER BY a DateTimeOffset — matching
        // the other repositories' ordering convention.
        return await _dbContext.Participants
            .Where(participant => participant.OrganizationId == organizationId
                && participant.WorkspaceId == workspaceId
                && participant.Status == ParticipantStatus.Active)
            .OrderBy(participant => participant.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Participant?> FindByUserAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // All three predicates translate to parameterized SQL equality, leading
        // with the tenant column. The filtered unique (workspace_id, user_id)
        // index guarantees at most one user-linked participant per workspace; a
        // participant the user has in another workspace, or in the same workspace
        // id under a different organization, is never returned (threat T5/T1).
        return await _dbContext.Participants
            .FirstOrDefaultAsync(
                participant => participant.OrganizationId == organizationId
                    && participant.WorkspaceId == workspaceId
                    && participant.UserProfileId == userProfileId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ParticipantAddResult> AddAsync(
        Participant participant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participant);

        _dbContext.Participants.Add(participant);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ParticipantAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a
            // later SaveChanges on the same scope.
            _dbContext.Entry(participant).State = EntityState.Detached;

            // Provider-neutral duplicate detection: only user-linked participants
            // are subject to the unique (workspace_id, user_id) index, so a
            // duplicate is possible only when this participant has a user link and
            // a participant for the same (workspace, user) pair exists now
            // (typically a lost create race). Any other failure (for example a
            // foreign-key violation for a non-existent workspace, tenant or user)
            // is rethrown unchanged. Anonymous participants can never trip this.
            if (participant.UserProfileId is { } userProfileId)
            {
                var duplicateExists = await _dbContext.Participants
                    .AsNoTracking()
                    .AnyAsync(
                        existing => existing.WorkspaceId == participant.WorkspaceId
                            && existing.UserProfileId == userProfileId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (duplicateExists)
                {
                    return ParticipantAddResult.DuplicateUserParticipant;
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Participant participant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participant);

        // The participant was loaded and mutated within this scope's change
        // tracker (or is attached here); only the mutable display name, status
        // and update timestamp change. The organization, workspace, user link and
        // id are immutable on the aggregate, so an update can never move the row
        // to another tenant, workspace or subject (threat T5).
        _dbContext.Participants.Update(participant);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> AnonymizeBySubjectAsync(
        Guid userProfileId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored participant (ids are generated non-empty), so the erasure
        // fails fast instead of matching an arbitrary set of rows.
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // CROSS-TENANT BY DESIGN (GDPR Art.17): the predicate matches the user link ALONE, not a tenant or
        // workspace, so it anonymizes the subject's participant display data everywhere it was stored. This is
        // safe under threat T5 — it is keyed by the subject's own surrogate id, returns only a count and is
        // reachable only from the authorized erasure command. Load-then-mutate + SaveChanges (the codebase's
        // add/remove convention) so the anonymized rows participate in the ambient erasure transaction.
        var participants = await _dbContext.Participants
            .Where(participant => participant.UserProfileId == userProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (participants.Count == 0)
        {
            return 0;
        }

        foreach (var participant in participants)
        {
            participant.Anonymize(updatedAt);
        }

        _dbContext.Participants.UpdateRange(participants);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return participants.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Participant>> ListBySubjectInOrganizationAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored participant (ids are generated non-empty), so the read fails fast
        // instead of scanning the whole table.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        // TENANT-SCOPED export read (CORE-PRIV-004): the predicate LEADS with the tenant column and matches the
        // user link, so the subject's participant records in another tenant are never returned even when the
        // surrogate ids would otherwise be addressable (threat T5/T1). The read is tracking-free (it never
        // mutates — the export only reads the subject's data back) and ordered oldest-first by the time-ordered
        // surrogate id (UUIDv7), provider-independent because SQLite cannot ORDER BY a DateTimeOffset. Every
        // lifecycle status is included: an access/portability export reflects the subject's data as held, so a
        // soft-removed participant is still part of the subject's record.
        return await _dbContext.Participants
            .AsNoTracking()
            .Where(participant => participant.OrganizationId == organizationId
                && participant.UserProfileId == userProfileId)
            .OrderBy(participant => participant.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
