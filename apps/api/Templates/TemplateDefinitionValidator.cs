using System.Text.Json;
using LiveCore.Api.Entities;

namespace LiveCore.Api.Templates;

/// <summary>
/// Well-formedness, SIZE and minimal STRUCTURAL validation for a <see cref="Template"/>'s
/// definition document (CORE-ENT-004, the story's "Template validation tests"). This is the
/// Templates module's counterpart of the Entities module's <see cref="AttributeSchemaValidator"/>:
/// a small, reusable, product-neutral helper the <see cref="Template"/> aggregate calls from its
/// constructor and the loader calls to obtain a parsed <see cref="TemplateDefinition"/>.
///
/// Scope boundary (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md): the definition is the
/// template content, supplied at runtime as DATA. This validator decides ONLY whether that data is
/// (a) well-formed JSON within a size bound and (b) STRUCTURALLY shaped like a template — a
/// top-level <c>templateKey</c> string and a non-empty <c>entityTypes</c> array whose entries each
/// carry a valid key, display name and attribute schema. It NEVER inspects the VOCABULARY of any
/// key or name, never branches on a specific value, and never validates an entity INSTANCE against
/// a schema (entity-instance schema-conformance is a separate concern, not this story). It reuses
/// the <see cref="EntityType"/> field validators (<see cref="EntityType.IsValidTypeKey"/>,
/// <see cref="EntityType.IsValidDisplayName"/>, <see cref="EntityType.IsValidAttributeSchema"/>)
/// so the structural rule stays identical to what the loader's <c>EntityType.Create</c> will
/// enforce, and reuses the built-in <see cref="JsonDocument"/> parse pattern (System.Text.Json, no
/// new dependency).
/// </summary>
public static class TemplateDefinitionValidator
{
    /// <summary>The required top-level property naming the definition's own template key.</summary>
    private const string _templateKeyProperty = "templateKey";

    /// <summary>The required top-level property holding the array of entity-type definitions.</summary>
    private const string _entityTypesProperty = "entityTypes";

    /// <summary>The required property of each entity-type entry holding its canonical type key.</summary>
    private const string _keyProperty = "key";

    /// <summary>The required property of each entity-type entry holding its display name.</summary>
    private const string _displayNameProperty = "displayName";

    /// <summary>The required property of each entity-type entry holding its attribute schema.</summary>
    private const string _attributeSchemaProperty = "attributeSchema";

    /// <summary>
    /// Whether the given definition is valid: well-formed JSON within the size bound, with the
    /// expected top-level structure and a non-empty <c>entityTypes</c> array of valid entries.
    /// Returns <see langword="false"/> (never throws) for any invalid definition so the aggregate
    /// can branch on the result. The caller passes an already-trimmed value (the aggregate trims).
    /// </summary>
    public static bool IsValidDefinition(string? definition) => TryParse(definition, out _);

    /// <summary>
    /// Attempts to parse and structurally validate the definition, producing the generic
    /// <see cref="TemplateDefinition"/> the loader iterates. Returns <see langword="true"/> with a
    /// non-null <paramref name="parsed"/> only when the definition is well-formed, within the size
    /// bound and structurally valid; otherwise returns <see langword="false"/> with
    /// <paramref name="parsed"/> set to <see langword="null"/>. Never throws.
    /// </summary>
    public static bool TryParse(string? definition, out TemplateDefinition? parsed)
    {
        parsed = null;

        // Blank or oversized definitions are rejected first, so an oversized body is rejected
        // without parsing (mirrors AttributeSchemaValidator).
        if (string.IsNullOrWhiteSpace(definition) || definition.Length > Template.MaxDefinitionLength)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(definition);
        }
        catch (JsonException)
        {
            // Not well-formed JSON.
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            // The definition must be a JSON OBJECT (the docs example shape); a scalar or array root
            // is structurally invalid.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Top-level templateKey: a string with the canonical template-key SHAPE. This is a
            // generic shape check (Template.IsValidTemplateKey), never a vocabulary check.
            if (!root.TryGetProperty(_templateKeyProperty, out var templateKeyElement)
                || templateKeyElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var templateKey = templateKeyElement.GetString();
            if (!Template.IsValidTemplateKey(templateKey))
            {
                return false;
            }

            // Top-level entityTypes: a non-empty array. A missing array, a non-array value, or an
            // empty array is rejected (a template that defines no entity types loads nothing).
            if (!root.TryGetProperty(_entityTypesProperty, out var entityTypesElement)
                || entityTypesElement.ValueKind != JsonValueKind.Array
                || entityTypesElement.GetArrayLength() == 0)
            {
                return false;
            }

            var entityTypeDefinitions = new List<EntityTypeDefinition>(entityTypesElement.GetArrayLength());
            foreach (var entry in entityTypesElement.EnumerateArray())
            {
                if (!TryParseEntityTypeEntry(entry, out var entityTypeDefinition))
                {
                    return false;
                }

                entityTypeDefinitions.Add(entityTypeDefinition!);
            }

            parsed = new TemplateDefinition(templateKey!, entityTypeDefinitions);
            return true;
        }
    }

    /// <summary>
    /// Validates one entity-type entry of the <c>entityTypes</c> array against the
    /// <see cref="EntityType"/> field invariants and produces a generic
    /// <see cref="EntityTypeDefinition"/>. Each entry must be a JSON object carrying a valid
    /// <c>key</c>, <c>displayName</c> and <c>attributeSchema</c>. The key is validated as a
    /// canonical type-key SHAPE (no canonicalization here — the loader canonicalizes via
    /// <see cref="EntityType.Create"/>); the attribute schema is captured as its RAW JSON text so
    /// the loader stores it verbatim. No value is ever inspected for vocabulary (the template
    /// boundary, docs/04).
    /// </summary>
    private static bool TryParseEntityTypeEntry(JsonElement entry, out EntityTypeDefinition? entityTypeDefinition)
    {
        entityTypeDefinition = null;

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // key: a string with the canonical entity-type key SHAPE (reuses EntityType.IsValidTypeKey;
        // the loader canonicalizes casing/whitespace through EntityType.Create).
        if (!entry.TryGetProperty(_keyProperty, out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var key = keyElement.GetString();
        if (!EntityType.IsValidTypeKey(key?.Trim().ToLowerInvariant()))
        {
            return false;
        }

        // displayName: a string within the EntityType display-name invariant.
        if (!entry.TryGetProperty(_displayNameProperty, out var displayNameElement)
            || displayNameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var displayName = displayNameElement.GetString();
        if (!EntityType.IsValidDisplayName(displayName))
        {
            return false;
        }

        // attributeSchema: a JSON value (object/array/scalar). It is captured as its RAW JSON text
        // and re-validated through EntityType.IsValidAttributeSchema (well-formedness + size), so
        // the schema the template carries is exactly the schema the entity type will store. A
        // missing attributeSchema is invalid (the entity type always has a schema).
        if (!entry.TryGetProperty(_attributeSchemaProperty, out var attributeSchemaElement))
        {
            return false;
        }

        var attributeSchema = attributeSchemaElement.GetRawText();
        if (!EntityType.IsValidAttributeSchema(attributeSchema))
        {
            return false;
        }

        entityTypeDefinition = new EntityTypeDefinition(key!, displayName!, attributeSchema);
        return true;
    }
}
