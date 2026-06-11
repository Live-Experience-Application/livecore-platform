using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// EF Core implementation of <see cref="IUserProfileRepository"/>
/// (CORE-ID-002), backed by the <c>users</c> table mapped in
/// <see cref="UserProfileConfiguration"/>.
/// </summary>
internal sealed class UserProfileRepository : IUserProfileRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public UserProfileRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<UserProfile?> FindByOidcIdentityAsync(
        string issuer,
        string subjectId,
        CancellationToken cancellationToken)
    {
        // Invalid values can never address a stored profile (profiles only
        // exist in valid states), so the lookup fails fast instead of
        // silently returning nothing for malformed input.
        if (!OidcPrincipal.IsValidIssuer(issuer))
        {
            throw new ArgumentException("Issuer violates the issuer invariants.", nameof(issuer));
        }

        if (!OidcPrincipal.IsValidSubjectId(subjectId))
        {
            throw new ArgumentException("Subject violates the subject invariants.", nameof(subjectId));
        }

        // Both predicates translate to parameterized SQL equality, which is
        // exact and case-sensitive under the default binary-style collations
        // of PostgreSQL: a near-match never addresses a foreign profile
        // (threat T5). The unique index guarantees at most one row.
        return await _dbContext.UserProfiles
            .FirstOrDefaultAsync(
                profile => profile.Issuer == issuer && profile.SubjectId == subjectId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _dbContext.UserProfiles.Add(profile);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return UserProfileAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be
            // retried by a later SaveChanges on the same scope.
            _dbContext.Entry(profile).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this
            // identity pair exists now, the unique index rejected the
            // insert as a duplicate (typically a lost create race). Any
            // other failure is rethrown unchanged.
            var duplicateExists = await _dbContext.UserProfiles
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.Issuer == profile.Issuer && existing.SubjectId == profile.SubjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return UserProfileAddResult.DuplicateIdentity;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_dbContext.Entry(profile).State == EntityState.Detached)
        {
            _dbContext.UserProfiles.Update(profile);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
