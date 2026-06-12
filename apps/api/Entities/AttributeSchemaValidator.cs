using System.Text.Json;

namespace LiveCore.Api.Entities;

/// <summary>
/// Well-formedness and SIZE validation for an <see cref="EntityType"/>'s template-defined
/// attribute schema (CORE-ENT-001). This is the entity-type counterpart of the Content
/// module's <c>ContentValidator</c>: a small, reusable, product-neutral helper that the
/// <see cref="EntityType"/> aggregate calls from <see cref="EntityType.Create"/> and
/// <see cref="EntityType.RedefineAttributeSchema"/>, kept here so the rule can be
/// unit-tested in isolation (the story's "Template validation tests").
///
/// Scope boundary (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md): the attribute
/// schema is the flexible, template-defined attribute DEFINITIONS of a type, supplied at
/// runtime as DATA. This validator decides ONLY whether that data is WELL-FORMED JSON within
/// a size bound — it never inspects the schema's vocabulary, never branches on any type name
/// and never validates the schema against a template's expected structure. Full JSON-schema
/// validation against a loaded template (the schema-validation engine) is the separate
/// Templates module / CORE-ENT-004 ("template-loaded entity types", docs/05_MODULE_CONTRACTS.md:
/// the Templates module owns the template loader, validation and versioning). Keeping this
/// helper free of type-specific logic is what keeps the Entities module generic and
/// template-driven (docs/04: Core may store template content as data, but Core source must
/// not contain logic like <c>if entityType == ... then ...</c>).
///
/// The size limit is a CONTENT-shape limit (how large a single attribute-schema document may
/// be), reusing the System.Text.Json <see cref="JsonDocument"/> parse pattern from
/// <c>ContentValidator</c> with no new dependency.
/// </summary>
public static class AttributeSchemaValidator
{
    /// <summary>
    /// Maximum length of an entity type's attribute schema document, re-exported from
    /// <see cref="EntityType.MaxAttributeSchemaLength"/> so both the aggregate and any caller
    /// reference one constant. A generous bound for a flexible attribute-definition document
    /// that still rejects hostile or broken oversized input.
    /// </summary>
    public const int MaxSchemaLength = EntityType.MaxAttributeSchemaLength;

    /// <summary>
    /// Whether the given attribute schema is valid, AFTER it has been trimmed by the caller.
    /// Returns <see langword="false"/> (never throws) for any invalid schema so the aggregate
    /// can branch on the result: a blank/null schema, an oversized schema (checked first, so
    /// an oversized body is rejected without parsing), or a schema that is not WELL-FORMED
    /// JSON is rejected. Only well-formedness is checked, never the JSON schema against a
    /// template (that is template validation, CORE-ENT-004).
    /// </summary>
    public static bool IsValidSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return false;
        }

        return schema.Length <= MaxSchemaLength && IsWellFormedJson(schema);
    }

    /// <summary>
    /// Whether the given value parses as well-formed JSON (object, array or any JSON scalar).
    /// Uses the built-in <see cref="JsonDocument"/> (System.Text.Json, no new dependency); a
    /// <see cref="JsonException"/> means the value is not well-formed JSON and is rejected.
    /// The parsed document is disposed immediately — only well-formedness matters here, not
    /// the contents (the template boundary, docs/04).
    /// </summary>
    private static bool IsWellFormedJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
