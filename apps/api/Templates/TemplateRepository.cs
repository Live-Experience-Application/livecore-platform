using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Templates;

/// <summary>
/// EF Core implementation of <see cref="ITemplateRepository"/> (CORE-ENT-004), backed by the
/// <c>templates</c> table mapped in <see cref="TemplateConfiguration"/>. Lookups are scope-aware:
/// the global flavours match <c>organization_id IS NULL</c>, the organization flavours match an
/// exact <c>organization_id</c>, so a global template is never read through an org path and an
/// org-A template is never read through org-B's id (threat T5/T1).
/// </summary>
internal sealed class TemplateRepository : ITemplateRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public TemplateRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Template?> FindGlobalByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Template id must not be empty.", nameof(id));
        }

        // organization_id IS NULL pins the GLOBAL pool: an org-scoped row with the same id never
        // matches (threat T5/T1).
        return await _dbContext.Templates
            .FirstOrDefaultAsync(
                template => template.OrganizationId == null && template.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Template?> FindByOrganizationAndIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Template id must not be empty.", nameof(id));
        }

        // The predicate leads with the tenant column and matches the id, so a global template or a
        // template owned by another organization is never returned even when the id matches
        // (threat T5/T1).
        return await _dbContext.Templates
            .FirstOrDefaultAsync(
                template => template.OrganizationId == organizationId && template.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Template?> FindGlobalByKeyAsync(
        string templateKey,
        int version,
        CancellationToken cancellationToken)
    {
        var canonicalKey = Template.CanonicalizeTemplateKey(templateKey);
        if (version < Template.MinVersion)
        {
            throw new ArgumentException("Template version must be a positive integer.", nameof(version));
        }

        return await _dbContext.Templates
            .FirstOrDefaultAsync(
                template => template.OrganizationId == null
                    && template.TemplateKey == canonicalKey
                    && template.Version == version,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Template?> FindByOrganizationAndKeyAsync(
        Guid organizationId,
        string templateKey,
        int version,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        var canonicalKey = Template.CanonicalizeTemplateKey(templateKey);
        if (version < Template.MinVersion)
        {
            throw new ArgumentException("Template version must be a positive integer.", nameof(version));
        }

        return await _dbContext.Templates
            .FirstOrDefaultAsync(
                template => template.OrganizationId == organizationId
                    && template.TemplateKey == canonicalKey
                    && template.Version == version,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Template>> ListGlobalAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Templates
            .Where(template => template.OrganizationId == null)
            .OrderBy(template => template.TemplateKey)
            .ThenBy(template => template.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Template>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        return await _dbContext.Templates
            .Where(template => template.OrganizationId == organizationId)
            .OrderBy(template => template.TemplateKey)
            .ThenBy(template => template.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemplateAddResult> AddAsync(Template template, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);

        _dbContext.Templates.Add(template);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return TemplateAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later
            // SaveChanges on the same scope.
            _dbContext.Entry(template).State = EntityState.Detached;

            // Provider-neutral duplicate detection within the SAME scope. SQLite treats NULLs as
            // distinct in a plain unique index, so each scope is enforced by its own FILTERED
            // unique index and the duplicate probe must mirror the same scope predicate: for a
            // global template (OrganizationId is null) probe the global pool, for an org template
            // probe that one organization. Any other failure (for example a foreign-key violation
            // for an org-scoped template referencing a non-existent organization) is rethrown.
            var duplicateExists = template.OrganizationId is { } organizationId
                ? await _dbContext.Templates
                    .AsNoTracking()
                    .AnyAsync(
                        existing => existing.OrganizationId == organizationId
                            && existing.TemplateKey == template.TemplateKey
                            && existing.Version == template.Version,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _dbContext.Templates
                    .AsNoTracking()
                    .AnyAsync(
                        existing => existing.OrganizationId == null
                            && existing.TemplateKey == template.TemplateKey
                            && existing.Version == template.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (duplicateExists)
            {
                return TemplateAddResult.Duplicate;
            }

            throw;
        }
    }
}
