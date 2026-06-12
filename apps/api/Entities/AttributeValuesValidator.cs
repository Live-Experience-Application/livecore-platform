using System.Text.Json;

namespace LiveCore.Api.Entities;

/// <summary>
/// Well-formedness and SIZE validation for an <see cref="Entity"/>'s attribute VALUES document
/// (CORE-ENT-002). This is the entity-instance counterpart of <see cref="AttributeSchemaValidator"/>
/// (which validates an entity TYPE's attribute SCHEMA): a small, product-neutral helper that the
/// <see cref="Entity"/> aggregate calls from <see cref="Entity.Create"/> and
/// <see cref="Entity.RedefineAttributeValues"/>, kept here so the rule can be unit-tested in
/// isolation (the story's "Template validation tests"). It is deliberately a separate, analogous
/// validator rather than a shared helper: each aggregate owns and re-exports its own size bound,
/// and folding them into one shared abstraction would be refactoring beyond this story's scope.
///
/// Scope boundary (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md): the attribute values are
/// the instance's actual attribute DATA, supplied at runtime. This validator decides ONLY whether
/// that data is WELL-FORMED JSON within a size bound — it never inspects the values' vocabulary,
/// never branches on any type or name and never validates the values against the entity type's
/// declared attribute SCHEMA. Schema-conformance validation (checking the instance's values
/// against the type's attribute definitions) is the template engine / CORE-ENT-004, not here.
/// Keeping this helper free of type-specific logic is what keeps the Entities module generic and
/// template-driven (docs/04: Core may store template content as data, but Core source must not
/// contain logic like <c>if entityType == ... then ...</c>).
///
/// The size limit is a CONTENT-shape limit (how large a single attribute-values document may be),
/// reusing the System.Text.Json <see cref="JsonDocument"/> parse pattern with no new dependency.
/// </summary>
public static class AttributeValuesValidator
{
    /// <summary>
    /// Maximum length of an entity's attribute-values document, re-exported from
    /// <see cref="Entity.MaxAttributeValuesLength"/> so both the aggregate and any caller
    /// reference one constant. A generous bound for a flexible attribute-values document that
    /// still rejects hostile or broken oversized input.
    /// </summary>
    public const int MaxValuesLength = Entity.MaxAttributeValuesLength;

    /// <summary>
    /// Whether the given attribute-values document is valid, AFTER it has been trimmed by the
    /// caller. Returns <see langword="false"/> (never throws) for any invalid document so the
    /// aggregate can branch on the result: a blank/null document, an oversized document (checked
    /// first, so an oversized body is rejected without parsing), or a document that is not
    /// WELL-FORMED JSON is rejected. Only well-formedness is checked, never the values against the
    /// entity type's attribute schema (that is schema-conformance validation, CORE-ENT-004).
    /// </summary>
    public static bool IsValidValues(string? values)
    {
        if (string.IsNullOrWhiteSpace(values))
        {
            return false;
        }

        return values.Length <= MaxValuesLength && IsWellFormedJson(values);
    }

    /// <summary>
    /// Whether the given value parses as well-formed JSON (object, array or any JSON scalar). Uses
    /// the built-in <see cref="JsonDocument"/> (System.Text.Json, no new dependency); a
    /// <see cref="JsonException"/> means the value is not well-formed JSON and is rejected. The
    /// parsed document is disposed immediately — only well-formedness matters here, not the
    /// contents (the template boundary, docs/04).
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
