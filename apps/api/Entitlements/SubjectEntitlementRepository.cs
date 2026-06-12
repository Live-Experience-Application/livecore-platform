using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// EF Core implementation of <see cref="ISubjectEntitlementRepository"/> (CORE-ENTL-002), backed by the
/// <c>subject_entitlements</c> table mapped in <see cref="SubjectEntitlementConfiguration"/>. Every read leads
/// with the (subject_type, subject_id) pair, so one subject's premium state is never returned through another
/// subject's id.
/// </summary>
internal sealed class SubjectEntitlementRepository : ISubjectEntitlementRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public SubjectEntitlementRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<SubjectEntitlementAddResult> AddAsync(
        SubjectEntitlement assignment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        _dbContext.SubjectEntitlements.Add(assignment);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SubjectEntitlementAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the
            // same scope.
            _dbContext.Entry(assignment).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if the subject already holds this entitlement, the unique
            // (subject_type, subject_id, entitlement_definition_id) index rejected the insert as a duplicate
            // (typically a lost assignment race). Any other failure (for example a granted
            // entitlement-definition foreign key that does not exist) is rethrown unchanged.
            var duplicateExists = await _dbContext.SubjectEntitlements
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.SubjectType == assignment.SubjectType
                        && existing.SubjectId == assignment.SubjectId
                        && existing.EntitlementDefinitionId == assignment.EntitlementDefinitionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return SubjectEntitlementAddResult.DuplicateAssignment;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(SubjectEntitlement assignment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // The assignment is already tracked (it was loaded through this context); EF persists the mutated value
        // and lifecycle columns. Update is in place — a subject never carries two rows for the same entitlement.
        _dbContext.SubjectEntitlements.Update(assignment);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubjectEntitlement?> FindBySubjectAndEntitlementAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid entitlementDefinitionId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (entitlementDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(entitlementDefinitionId));
        }

        // The predicate leads with the subject pair, then the entitlement, so another subject's assignment is
        // never returned even when the entitlement id matches.
        return await _dbContext.SubjectEntitlements
            .FirstOrDefaultAsync(
                assignment => assignment.SubjectType == subjectType
                    && assignment.SubjectId == subjectId
                    && assignment.EntitlementDefinitionId == entitlementDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubjectEntitlement>> ListBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Scoped by the subject pair, ordered by the stable entitlement key for a deterministic result (a
        // value supported by every provider, unlike a DateTimeOffset ORDER BY on SQLite).
        return await _dbContext.SubjectEntitlements
            .Where(assignment => assignment.SubjectType == subjectType && assignment.SubjectId == subjectId)
            .OrderBy(assignment => assignment.EntitlementKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubjectEntitlement>> ListActiveBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Scoped by the subject pair AND filtered to active assignments: a revoked assignment is excluded, so
        // the resolver sees only the in-force premium state (the fail-closed default — a revoked grant is not
        // entitled; docs/21).
        return await _dbContext.SubjectEntitlements
            .Where(assignment => assignment.SubjectType == subjectType
                && assignment.SubjectId == subjectId
                && assignment.IsActive)
            .OrderBy(assignment => assignment.EntitlementKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
