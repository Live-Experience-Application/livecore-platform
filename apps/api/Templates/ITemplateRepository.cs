namespace LiveCore.Api.Templates;

/// <summary>
/// Persistence contract for the <see cref="Template"/> aggregate (CORE-ENT-004). The Templates
/// module owns the <c>templates</c> table; other modules access templates only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table
/// ownership violations; docs/05_MODULE_CONTRACTS.md: the Templates module owns the template
/// loader/validation/versioning).
///
/// Scope-aware lookups (csv/database_tables.csv row 19, "global/organization"): the registry holds
/// both GLOBAL templates (<c>organization_id IS NULL</c>, available to every tenant) and
/// ORGANIZATION-scoped templates (owned by one tenant). Every lookup is explicit about scope. The
/// by-id lookups deliberately come in two flavours — a GLOBAL lookup and an ORGANIZATION lookup —
/// so a caller can never accidentally read an org template through the global path or vice versa,
/// and so an org-A template is never returned through org-B's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization). There is no
/// "find by id alone" that ignores scope.
///
/// This contract takes explicit ids and keys; resolving the "current" organization from a request
/// is the tenant context resolver and a future endpoint (csv/api_routes.csv defines no template
/// route, so there is no HTTP surface in this story).
/// </summary>
public interface ITemplateRepository
{
    /// <summary>
    /// Finds the GLOBAL template with exactly the given id (<c>organization_id IS NULL</c>), or
    /// <see langword="null"/> when no such global template exists. An organization-scoped template
    /// with the same id is never returned here (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">The template id is empty.</exception>
    Task<Template?> FindGlobalByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the ORGANIZATION-scoped template with exactly the given id WITHIN the given
    /// organization, or <see langword="null"/> when no such template exists there. A global
    /// template, or a template owned by another organization, is never returned even when the
    /// surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or template id is empty.</exception>
    Task<Template?> FindByOrganizationAndIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the GLOBAL template addressed by exactly the given key and version, or
    /// <see langword="null"/> when none exists. The key is canonicalized (and malformed input
    /// rejected) before the lookup. An organization-scoped template with the same key+version is
    /// never returned here (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The template key is not a valid key after canonicalization, or the version is not positive.
    /// </exception>
    Task<Template?> FindGlobalByKeyAsync(
        string templateKey,
        int version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the ORGANIZATION-scoped template addressed by exactly the given key and version WITHIN
    /// the given organization, or <see langword="null"/> when none exists there. The key is
    /// canonicalized (and malformed input rejected) before the lookup. A global template, or one
    /// owned by another organization, is never returned here (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id is empty, the template key is not valid after canonicalization, or the
    /// version is not positive.
    /// </exception>
    Task<Template?> FindByOrganizationAndKeyAsync(
        Guid organizationId,
        string templateKey,
        int version,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every GLOBAL template (<c>organization_id IS NULL</c>) in a deterministic order
    /// (template key then version ascending). Organization-scoped templates are never included.
    /// </summary>
    Task<IReadOnlyList<Template>> ListGlobalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists every ORGANIZATION-scoped template owned by the given organization in a deterministic
    /// order (template key then version ascending). The list is tenant-scoped: another tenant's
    /// templates and the global pool are never included (threat T5/T1). An empty list is returned
    /// for an organization that owns no templates.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    Task<IReadOnlyList<Template>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new template. Returns <see cref="TemplateAddResult.Added"/> on success or
    /// <see cref="TemplateAddResult.Duplicate"/> when the same scope already holds a template with
    /// the same key and version (the per-scope unique index rejected the insert). The existing row
    /// is left unchanged. A foreign-key violation (an org-scoped template referencing a
    /// non-existent organization) surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<TemplateAddResult> AddAsync(Template template, CancellationToken cancellationToken);
}
