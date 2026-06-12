using LiveCore.Api.Entities;

namespace LiveCore.Api.Templates;

/// <summary>
/// The template-loaded-entity-types loader of the Templates module (CORE-ENT-004, the headline
/// behavior). Given a resolved <see cref="Template"/> and a target (organization, workspace), it
/// materializes workspace <see cref="EntityType"/> rows FROM the template's <c>entityTypes</c>
/// definitions: it iterates the parsed definitions GENERICALLY (a plain foreach over the array,
/// never a switch on any type name), calls <see cref="EntityType.Create"/> for each and persists
/// through <see cref="IEntityTypeRepository.AddAsync"/>, collecting a structured
/// <see cref="TemplateLoadResult"/> (created count + per-type outcome).
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): the entity-type keys, names and schemas
/// are DATA read from the template definition; this loader never inspects their vocabulary and
/// never branches on a key/name. A two-type template and a three-type template load identically.
/// The Templates module depends on the Entities module through its explicit contracts
/// (<see cref="EntityType.Create"/>, <see cref="IEntityTypeRepository"/>), the allowed cross-module
/// access (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md: modules talk through contracts,
/// not each other's tables); it only CONSUMES the Entities contracts and never modifies the
/// <c>entity_types</c> table.
///
/// TENANT SECURITY (threat T5 in docs/07_SECURITY_THREAT_MODEL.md — the critical control): the
/// loader validates the template's SCOPE against the target organization BEFORE creating anything.
/// An ORGANIZATION-scoped template may be loaded ONLY into a workspace owned by the SAME
/// organization; a GLOBAL template may be loaded into ANY organization's workspace. An attempt to
/// load an org-A template into an org-B workspace is DENIED
/// (<see cref="TemplateLoadDenialReason.ScopeMismatch"/>) and nothing is created. The caller (a
/// future endpoint) resolves the target workspace through the tenant context and passes the target
/// organization + workspace explicitly; the loader re-checks the scope gate itself rather than
/// trusting the caller (defence in depth).
///
/// Idempotency / duplicate handling (a partial-load-with-report, by deliberate choice): if the
/// workspace already defines a type with a key the template names, the loader does NOT fail the
/// whole load — it SKIPS that one (reported as
/// <see cref="TemplateEntityTypeOutcome.SkippedDuplicate"/> when
/// <see cref="IEntityTypeRepository.AddAsync"/> returns <see cref="EntityTypeAddResult.DuplicateKey"/>)
/// and creates the rest. This is preferred over an all-or-nothing transactional load because a
/// template load is naturally idempotent (re-running it should converge the workspace to the
/// template's types, adding only what is missing) and because the per-workspace unique key already
/// guarantees no duplicate type is ever created; reporting the skip lets the caller see exactly what
/// the workspace already had. The definition has already been STRUCTURALLY validated
/// (<see cref="TemplateDefinitionValidator"/>), so each <see cref="EntityType.Create"/> succeeds and
/// the only expected per-type outcome other than created is the duplicate skip.
///
/// This loader persists entity types only. It emits no events, computes no visibility, runs no
/// entity-instance schema-conformance validation, exposes no HTTP route and performs no
/// template version-transition — all out of scope for this story.
/// </summary>
public sealed class TemplateEntityTypeLoader
{
    private readonly IEntityTypeRepository _entityTypes;
    private readonly TimeProvider _timeProvider;

    public TemplateEntityTypeLoader(IEntityTypeRepository entityTypes, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _entityTypes = entityTypes;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Loads the given template's entity-type definitions into the target workspace (owned by the
    /// target organization), returning a granted result with the per-type outcomes or a fail-closed
    /// denial. The scope gate (threat T5) is checked first: an org-scoped template targeted at a
    /// foreign organization's workspace is denied and nothing is created.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The target organization id or workspace id is empty. An empty id can never address a real
    /// tenant or workspace, so it is rejected rather than treated as a silent denial (consistent
    /// with the repository contracts).
    /// </exception>
    public async Task<TemplateLoadResult> LoadAsync(
        Template template,
        Guid targetOrganizationId,
        Guid targetWorkspaceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);

        // Empty ids can never address a real tenant or workspace. Reject them up front so the
        // outcome is a clean ArgumentException, matching the repository contracts.
        if (targetOrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Target organization id must not be empty.", nameof(targetOrganizationId));
        }

        if (targetWorkspaceId == Guid.Empty)
        {
            throw new ArgumentException("Target workspace id must not be empty.", nameof(targetWorkspaceId));
        }

        // TENANT SECURITY GATE (threat T5), enforced BEFORE any entity type is created: an
        // org-scoped template may load only into a workspace of the SAME organization; a global
        // template may load anywhere. An org-A template targeted at an org-B workspace is denied
        // here and nothing is persisted.
        if (!template.IsLoadableInto(targetOrganizationId))
        {
            return TemplateLoadResult.Denied(TemplateLoadDenialReason.ScopeMismatch);
        }

        // Parse + structurally re-validate the definition (it was validated when the template was
        // created, so this always succeeds for a stored template; the guard keeps the loader honest
        // and yields the generic definition model to iterate). A definition that somehow fails
        // structural validation has no entity types to create.
        if (!TemplateDefinitionValidator.TryParse(template.Definition, out var definition) || definition is null)
        {
            return TemplateLoadResult.Grant([]);
        }

        var now = _timeProvider.GetUtcNow();
        var outcomes = new List<TemplateEntityTypeLoadOutcome>(definition.EntityTypeDefinitions.Count);

        // Generic iteration: one foreach over the definitions, identical handling for every entry.
        // There is no per-type branching (the template boundary, docs/04).
        foreach (var entityTypeDefinition in definition.EntityTypeDefinitions)
        {
            // EntityType.Create canonicalizes the key and validates every field; the definition was
            // already structurally validated, so this does not throw for a stored template.
            var entityType = EntityType.Create(
                targetOrganizationId,
                targetWorkspaceId,
                entityTypeDefinition.Key,
                entityTypeDefinition.DisplayName,
                entityTypeDefinition.AttributeSchema,
                now);

            var addResult = await _entityTypes.AddAsync(entityType, cancellationToken).ConfigureAwait(false);

            // A duplicate key in the workspace is skipped and reported, not fatal: the rest of the
            // template still loads (partial-load-with-report).
            var outcome = addResult == EntityTypeAddResult.Added
                ? TemplateEntityTypeOutcome.Created
                : TemplateEntityTypeOutcome.SkippedDuplicate;

            outcomes.Add(new TemplateEntityTypeLoadOutcome(entityType.TypeKey, outcome));
        }

        return TemplateLoadResult.Grant(outcomes);
    }
}
