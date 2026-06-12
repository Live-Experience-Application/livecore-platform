namespace LiveCore.Api.Templates;

/// <summary>
/// Per-entity-type outcome of a template load (CORE-ENT-004). For each entity-type definition in
/// the template the loader records whether the type was newly CREATED in the target workspace or
/// SKIPPED because the workspace already defined that key.
/// </summary>
public enum TemplateEntityTypeOutcome
{
    /// <summary>The entity type was created in the target workspace.</summary>
    Created = 1,

    /// <summary>
    /// The entity type was skipped because the workspace already defined a type with this key (the
    /// per-workspace unique key rejected the insert). The existing type is left unchanged; the load
    /// continues with the remaining definitions (partial-load-with-report).
    /// </summary>
    SkippedDuplicate = 2,
}

/// <summary>
/// The outcome for a single entity-type definition of a loaded template (CORE-ENT-004): the
/// canonical type <see cref="Key"/> and whether it was created or skipped. The key is a canonical
/// slug identifier (safe to surface, like a workspace slug); no template content beyond the key is
/// carried, so the report stays log-safe (threat T7).
/// </summary>
/// <param name="Key">The canonical entity-type key from the template definition.</param>
/// <param name="Outcome">Whether the type was created or skipped as a duplicate.</param>
public readonly record struct TemplateEntityTypeLoadOutcome(string Key, TemplateEntityTypeOutcome Outcome);

/// <summary>
/// Why a template load was DENIED before any entity type was created (CORE-ENT-004). The loader
/// fails closed: a denial means nothing was persisted (threat T5/T1).
/// </summary>
public enum TemplateLoadDenialReason
{
    /// <summary>
    /// The template's scope does not permit loading into the target organization's workspace: an
    /// ORGANIZATION-scoped template was targeted at a workspace owned by a DIFFERENT organization
    /// (threat T5: an org-A template can never be loaded into an org-B workspace). A global
    /// template is never denied for this reason.
    /// </summary>
    ScopeMismatch = 1,
}

/// <summary>
/// The result of loading a <see cref="Template"/>'s entity-type definitions into a target workspace
/// (CORE-ENT-004, the headline behavior). It is either GRANTED — carrying the per-type outcomes and
/// the created count — or a fail-closed DENIAL carrying a typed reason and creating nothing.
///
/// Idempotency / partial-load: when granted, a definition whose key already exists in the workspace
/// is reported as <see cref="TemplateEntityTypeOutcome.SkippedDuplicate"/> rather than failing the
/// whole load, so re-loading a template (or loading one that overlaps an existing workspace) creates
/// only the missing types and reports the rest (a partial-load-with-report; see the loader's
/// reasoning for why this is preferred over all-or-nothing). The result never carries any template
/// definition content beyond the canonical type keys, so it is log-safe (threat T7).
/// </summary>
public sealed class TemplateLoadResult
{
    private TemplateLoadResult(
        bool granted,
        TemplateLoadDenialReason? denialReason,
        IReadOnlyList<TemplateEntityTypeLoadOutcome> outcomes)
    {
        Granted = granted;
        DenialReason = denialReason;
        Outcomes = outcomes;
    }

    /// <summary>Whether the load was permitted. When <see langword="false"/>, nothing was created.</summary>
    public bool Granted { get; }

    /// <summary>
    /// The reason the load was denied, or <see langword="null"/> when it was granted. A denial is
    /// fail-closed: no entity type was created (threat T5/T1).
    /// </summary>
    public TemplateLoadDenialReason? DenialReason { get; }

    /// <summary>
    /// The per-entity-type outcomes, in the template definition's order. Empty for a denial.
    /// </summary>
    public IReadOnlyList<TemplateEntityTypeLoadOutcome> Outcomes { get; }

    /// <summary>How many entity types were newly created in the target workspace.</summary>
    public int CreatedCount => Outcomes.Count(outcome => outcome.Outcome == TemplateEntityTypeOutcome.Created);

    /// <summary>How many entity-type definitions were skipped because the key already existed.</summary>
    public int SkippedCount => Outcomes.Count(outcome => outcome.Outcome == TemplateEntityTypeOutcome.SkippedDuplicate);

    /// <summary>Creates a GRANTED result carrying the per-type outcomes.</summary>
    public static TemplateLoadResult Grant(IReadOnlyList<TemplateEntityTypeLoadOutcome> outcomes)
        => new(granted: true, denialReason: null, outcomes);

    /// <summary>Creates a fail-closed DENIAL result that created nothing.</summary>
    public static TemplateLoadResult Denied(TemplateLoadDenialReason reason)
        => new(granted: false, denialReason: reason, outcomes: []);
}
