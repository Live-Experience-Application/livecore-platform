// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Templates;

/// <summary>
/// The template create command of the Templates module (CORE-TMPL-001, the "Vertical Authoring and Read API
/// Completeness" epic). An Owner/Admin can create a new ORGANIZATION-scoped <see cref="Template"/> definition —
/// reusable vertical scaffolding stored entirely as DATA (its template key plus the entity types it defines),
/// through which a vertical maps its domain onto Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md).
/// This service creates the template with a SERVER-MINTED surrogate id, persists it and appends an append-only
/// <see cref="AuditAction.TemplateCreated"/> audit record — the create + audit committing together in ONE
/// database transaction so a creation is recorded as a fact or it does not happen (the story's "audited"
/// criterion). It is the registry-level counterpart of <c>EntityTypeCreationService</c>.
///
/// ORGANIZATION-SCOPED ONLY (the story's "an org-scoped lookup never returns a global template for mutation"
/// criterion). This service ALWAYS creates an ORGANIZATION-scoped template owned by the resolved tenant
/// (<see cref="Template.CreateForOrganization"/>); there is no path here that creates a GLOBAL template
/// (<c>organization_id IS NULL</c>) — global templates are seed/platform data, never authored through this
/// tenant route. So a tenant can never mint or mutate a global template through this surface (threat T5).
///
/// PER-SCOPE UNIQUE KEY. A template's natural key is <see cref="Template.TemplateKey"/> +
/// <see cref="Template.Version"/> WITHIN its scope (an organization cannot register the same key+version twice),
/// enforced by the filtered unique (<c>organization_id</c>, <c>template_key</c>, <c>version</c>) index. The
/// service resolves any existing key+version through the tenant-scoped
/// <see cref="ITemplateRepository.FindByOrganizationAndKeyAsync"/> FIRST: a key+version already registered in
/// the caller's own organization yields <see cref="TemplateCreationResult.Duplicate"/> — nothing is created and
/// no transaction is opened (mirroring how <c>EntityTypeCreationService</c> resolves the key first and returns
/// without opening a transaction). The same key+version is still available to OTHER organizations and to the
/// global pool (threat T5); the unique index remains the real race guard, so a key+version claimed concurrently
/// after the pre-check still resolves to <see cref="TemplateCreationResult.Duplicate"/> inside the transaction.
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant (the endpoint performed the authentication,
/// tenant resolution and role authorization before calling in; this service is the authorized command's
/// effect). The created template is bound to exactly that organization — the <see cref="Template"/> aggregate
/// fixes the scope immutably at construction — so it can never be authored into another tenant or as a global
/// template (threat T5).
///
/// CONTENT BOUNDARY (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). The template's
/// <see cref="Template.TemplateKey"/> and <see cref="Template.Definition"/> are template-/host-supplied DATA,
/// validated only for shape (a valid dotted-slug key, a positive version, well-formed bounded structurally-valid
/// JSON) by the aggregate and <see cref="TemplateDefinitionValidator"/> — never inspected for vocabulary, never
/// branched on, and never written to logs or the audit record (threat T7); the audit fact records only
/// identifiers and the generic <c>Template</c> kind name.
///
/// ATOMICITY. The template insert and the audit append run inside a single explicit
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so either
/// the template and its audit record commit together or a failure rolls both back leaving nothing behind. The
/// transaction is opened inside the EF execution strategy's <c>ExecuteAsync</c>, so it stays correct under the
/// CORE-CONC-003 retrying strategy.
/// </summary>
internal sealed class TemplateCreationService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly ITemplateRepository _templates;
    private readonly IAuditLogRepository _audit;

    public TemplateCreationService(
        TransactionalUnitOfWork unitOfWork,
        ITemplateRepository templates,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _templates = templates;
        _audit = audit;
    }

    /// <summary>
    /// Creates a new organization-scoped template in the given tenant with the given key, version and
    /// definition, and appends an append-only <see cref="AuditAction.TemplateCreated"/> record — atomically.
    /// Returns <see cref="TemplateCreationResult.Created"/> carrying the created template when the key+version
    /// was free in the organization, or <see cref="TemplateCreationResult.Duplicate"/> when the organization
    /// already holds a template with that key and version (nothing is created).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the template.</param>
    /// <param name="templateKey">The template's canonical natural key (template-/host-supplied DATA; canonicalized and validated by the aggregate).</param>
    /// <param name="version">The positive integer template version (part of the per-scope natural key).</param>
    /// <param name="definition">The template's content as a JSON document (well-formed, structurally-valid DATA; validated for shape only).</param>
    /// <param name="actorUserProfileId">
    /// The authenticated Owner/Admin who created the template — the audited actor. Supplied by the endpoint from
    /// the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the template's created/updated time and the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id or actor id is empty, or the key/version/definition violate a <see cref="Template"/>
    /// invariant (the endpoint validates these before calling, so a throw here is defensive).
    /// </exception>
    public async Task<TemplateCreationResult> CreateAsync(
        Guid organizationId,
        string templateKey,
        int version,
        string definition,
        Guid actorUserProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // PER-SCOPE UNIQUE KEY: resolve any existing template with this key+version WITHIN the resolved tenant
        // FIRST. FindByOrganizationAndKeyAsync canonicalizes the key and leads with organization_id, so the same
        // key+version in another organization or in the global pool is never matched (threat T5). An already
        // registered key+version creates nothing and opens no transaction — the create is rejected as Duplicate.
        // The unique index is still the real race guard for a key+version claimed concurrently after this
        // pre-check (handled below).
        var existing = await _templates
            .FindByOrganizationAndKeyAsync(organizationId, templateKey, version, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return TemplateCreationResult.Duplicate;
        }

        // ONE unit of work (CORE-CONC-002): the template insert and the audit append commit together or roll
        // back together, so a creation is applied whole or not at all. Both repositories write through the same
        // scoped DbContext the TransactionalUnitOfWork begins the transaction on, so each SaveChanges enrols in
        // this transaction. The transaction is opened inside the EF execution strategy's ExecuteAsync, so it
        // stays correct under the CORE-CONC-003 retrying strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The aggregate mints the server-side surrogate id (UUIDv7) and fixes the organization scope
                // immutably; the key and definition are stored as DATA (the template boundary, docs/04). The
                // endpoint validated them, so CreateForOrganization does not throw here. It is ALWAYS an
                // organization-scoped template — never a global one (the "org-scoped route never mutates a global
                // template" criterion; threat T5).
                var template = Template.CreateForOrganization(organizationId, templateKey, version, definition, now);

                // The filtered unique (organization_id, template_key, version) index is the authoritative race
                // guard: a key+version claimed concurrently between the pre-check and here is rejected as a
                // duplicate, so nothing is created or audited and the existing template is left unchanged.
                var addResult = await _templates
                    .AddAsync(template, transactionCancellationToken)
                    .ConfigureAwait(false);
                if (addResult == TemplateAddResult.Duplicate)
                {
                    return TemplateCreationResult.Duplicate;
                }

                // AUDIT (the story's "audited" criterion): a creation is security-relevant, so append an
                // append-only record capturing the actor (the admin who created the template), the created
                // template and the tenant. A template is organization-level (not workspace content), so the fact
                // records no workspace. The template's key and definition content are NEVER recorded — only
                // identifiers and the generic Template kind name (threat T7).
                var entry = AuditLogEntry.ForTemplateCreation(
                    organizationId,
                    actorUserProfileId,
                    nameof(Template),
                    template.Id,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return TemplateCreationResult.Created(template);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
