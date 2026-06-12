namespace LiveCore.Api.Visibility;

/// <summary>
/// Generic kind of Core resource that a <see cref="VisibilityRule"/> governs (CORE-VIS-001, the
/// first story of the "Visibility and Reveal Engine" epic). A visibility rule names the resource it
/// applies to by a (<c>resource_type</c>, <c>resource_id</c>) pair — the documented critical index
/// is <c>visibility_rules(workspace_id, resource_type, resource_id)</c>
/// (docs/10_DATABASE_SCHEMA.md) — so this enum is the closed set of generic, product-neutral Core
/// resources whose audience visibility can be controlled.
///
/// The three kinds are exactly the Core resources that are host-prepared and then shown or hidden to
/// the audience (docs/03_DOMAIN_LANGUAGE.md): a <see cref="Content.ContentBlock"/> is literally the
/// "Text/media/data unit shown or hidden by visibility rules", a <see cref="Scenes.Scene"/> is a
/// "Segment of a session", and an <see cref="Entities.Entity"/> is a "Generic domain object". They
/// carry NO vertical product meaning (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md;
/// csv/forbidden_core_terms.csv): a vertical maps these to its own vocabulary in its UI; Core stores
/// only the generic discriminator and never branches on a vertical type name (the template
/// boundary, docs/04).
///
/// The kind is persisted by its stable NAME (not its numeric value), exactly like
/// <c>ContentBlockType</c>, <c>SessionStatus</c> and <c>ParticipantStatus</c>, so the integers below
/// are only in-memory storage discriminators and carry no ordering meaning; they must not be
/// compared with &gt;/&lt;. A <c>resource_id</c> column references one of these resources by its
/// surrogate id, but it is intentionally NOT a database foreign key (a single column cannot
/// foreign-key into three different tables); the rule is the polymorphic owner and the
/// same-workspace coupling between a rule and its resource is enforced by the application flow that
/// creates rules (the later reveal/visibility-rule command stories), mirroring how
/// <c>ContentBlock.SceneId</c> and <c>Entity.EntityTypeId</c> are simple references whose
/// same-workspace coupling is enforced above the aggregate.
/// </summary>
public enum VisibilityResourceType
{
    /// <summary>
    /// A <see cref="Scenes.Scene"/> — a "Segment of a session" (docs/03_DOMAIN_LANGUAGE.md). The
    /// rule controls whether the scene is visible to the audience.
    /// </summary>
    Scene = 1,

    /// <summary>
    /// A <see cref="Content.ContentBlock"/> — the "Text/media/data unit shown or hidden by
    /// visibility rules" (docs/03_DOMAIN_LANGUAGE.md). The rule controls whether the content block
    /// is visible to the audience.
    /// </summary>
    ContentBlock = 2,

    /// <summary>
    /// An <see cref="Entities.Entity"/> — a "Generic domain object" (docs/03_DOMAIN_LANGUAGE.md).
    /// The rule controls whether the entity is visible to the audience.
    /// </summary>
    Entity = 3,
}
