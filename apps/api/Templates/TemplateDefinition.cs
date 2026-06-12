using LiveCore.Api.Entities;

namespace LiveCore.Api.Templates;

/// <summary>
/// The parsed, structurally-validated shape of a <see cref="Template"/>'s definition document
/// (CORE-ENT-004). This is the small, GENERIC parse model the loader iterates: a
/// <see cref="TemplateKey"/> and an ordered list of <see cref="EntityTypeDefinitions"/>, each
/// naming an entity type to materialize (a key, a display name and an attribute schema). It is
/// produced only by <see cref="TemplateDefinitionValidator.TryParse"/>, which has already proven
/// the JSON is well-formed and structurally valid, so a parsed instance is always loadable.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): the values here (the template key, the
/// entity-type keys/names/schemas) are DATA supplied at runtime; they MAY carry vertical words.
/// This model and the loader treat them entirely generically — there is no field, branch or
/// comment that knows any specific key or name. The shape mirrors the docs example
/// <c>{"templateKey":"...","entityTypes":[...]}</c>, extended only so each entity-type entry can
/// carry the display name and attribute schema the loader needs to call <c>EntityType.Create</c>.
/// </summary>
public sealed class TemplateDefinition
{
    internal TemplateDefinition(string templateKey, IReadOnlyList<EntityTypeDefinition> entityTypeDefinitions)
    {
        TemplateKey = templateKey;
        EntityTypeDefinitions = entityTypeDefinitions;
    }

    /// <summary>
    /// The canonical template key declared INSIDE the definition document (the docs example's
    /// top-level <c>templateKey</c>). It is validated to the same dotted-slug shape as
    /// <see cref="Template.TemplateKey"/>. This is the definition's own self-description; it is
    /// stored DATA, never branched on (the template boundary, docs/04).
    /// </summary>
    public string TemplateKey { get; }

    /// <summary>
    /// The ordered entity-type definitions the loader materializes, one per entry of the
    /// definition's <c>entityTypes</c> array. Always non-empty (an empty array is rejected by the
    /// validator). The loader iterates this list GENERICALLY (a plain foreach, never a switch on
    /// any key).
    /// </summary>
    public IReadOnlyList<EntityTypeDefinition> EntityTypeDefinitions { get; }
}

/// <summary>
/// One entity-type entry of a <see cref="TemplateDefinition"/> (CORE-ENT-004): the generic,
/// validated inputs the loader passes straight to <see cref="EntityType.Create"/> — a canonical
/// type <see cref="Key"/>, a <see cref="DisplayName"/> and an <see cref="AttributeSchema"/>. Each
/// field has already been validated against the corresponding <see cref="EntityType"/> invariant
/// by <see cref="TemplateDefinitionValidator"/>, so the loader's <c>EntityType.Create</c> call
/// never throws for these inputs. The values are DATA and are never inspected for vocabulary (the
/// template boundary, docs/04).
/// </summary>
public sealed class EntityTypeDefinition
{
    internal EntityTypeDefinition(string key, string displayName, string attributeSchema)
    {
        Key = key;
        DisplayName = displayName;
        AttributeSchema = attributeSchema;
    }

    /// <summary>The canonical type key (validated to the <see cref="EntityType.IsValidTypeKey"/> shape).</summary>
    public string Key { get; }

    /// <summary>The entity type's display name (validated to <see cref="EntityType.IsValidDisplayName"/>).</summary>
    public string DisplayName { get; }

    /// <summary>The entity type's attribute schema JSON (validated to <see cref="EntityType.IsValidAttributeSchema"/>).</summary>
    public string AttributeSchema { get; }
}
