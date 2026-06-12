using System.Text.Json;

namespace LiveCore.Api.Content;

/// <summary>
/// Per-type content VALIDATION and explicit SIZE LIMITS for a <see cref="ContentBlock"/>
/// body (CORE-SCENE-005: "Implement content validation and size limits"). This is the
/// rich, type-aware validation that CORE-SCENE-002 (the aggregate) and CORE-SCENE-003
/// (the create endpoint) deliberately deferred, keeping only a single non-blank +
/// disallowed-control-char + one catch-all length guard until now.
///
/// It is a small, reusable, product-neutral helper that the <see cref="ContentBlock"/>
/// aggregate calls from <see cref="ContentBlock.Create"/> and <see cref="ContentBlock.Revise"/>
/// AND that the create endpoint reuses for an early, type-aware 400 BEFORE persistence.
/// Centralising the rules here keeps the two enforcement layers in lock-step (the
/// endpoint can never accept a body the aggregate would reject, and vice versa) and lets
/// the rules be unit-tested in isolation, exactly as the story's "validation tests"
/// require.
///
/// Scope boundary (docs/05_MODULE_CONTRACTS.md): the Content module owns "content blocks"
/// and a "content type registry" but "may not decide visibility alone; must use Visibility
/// module". So this validator decides ONLY whether a body is well-formed and within its
/// type's size bound — it is NEVER a visibility decision and never inspects who may see the
/// block (that is the central Visibility module's concern, threats T2/T3). It also does not
/// validate JSON *schema* (template-defined attribute validation is the Templates epic,
/// docs/05) — only JSON well-formedness; and it does not resolve a Media reference against a
/// real <c>Asset</c> (asset linkage to content blocks is the later CORE-AST-005 story,
/// docs/05 Assets module) — it bounds the reference string only.
///
/// The size limits below are CONTENT-shape limits (how large a single host-prepared body
/// may be), NOT asset storage byte quotas or entitlement quotas (those are the Assets and
/// Entitlements epics): a Media body here is a short reference STRING, never the media
/// bytes. The fixed Text/Media/Data enum is kept as-is; a data-driven content type registry
/// is a separate later slice (docs/05).
/// </summary>
public static class ContentValidator
{
    /// <summary>
    /// Maximum length of a <see cref="ContentBlockType.Text"/> body. A textual content
    /// block holds free-form host-prepared text (docs/03_DOMAIN_LANGUAGE.md: a content
    /// block is a "Text/media/data unit"); 16384 characters is a generous bound for a
    /// single segment of prepared narration/prose while still rejecting hostile or broken
    /// oversized input. It is a content-shape limit, not an asset byte quota.
    /// </summary>
    public const int MaxTextLength = 16384;

    /// <summary>
    /// Maximum length of a <see cref="ContentBlockType.Data"/> body. A data content block
    /// holds a structured JSON document of flexible, template-defined attributes
    /// (docs/03_DOMAIN_LANGUAGE.md "data unit"; docs/10_DATABASE_SCHEMA.md permits JSONB
    /// for flexible attributes, never for authorization fields). Structured documents are
    /// the largest legitimate body, so this is the most generous per-type bound and it
    /// therefore sets the overall column bound (<see cref="MaxBodyLength"/>); 65536
    /// characters preserves the column size the table was created with, so no schema
    /// migration is needed for this story.
    /// </summary>
    public const int MaxDataLength = 65536;

    /// <summary>
    /// Maximum length of a <see cref="ContentBlockType.Media"/> body. A media content
    /// block holds a generic media REFERENCE string (for example a later <c>Asset</c> id
    /// or a media URI), not the media bytes (docs/05_MODULE_CONTRACTS.md: asset metadata,
    /// storage and signed URLs are the Assets module's concern; binding a content block to
    /// a real asset is the later CORE-AST-005 story). A reference is short, so 2048
    /// characters is a comfortable bound that still admits long URIs while keeping the body
    /// a reference, not an embedded payload.
    /// </summary>
    public const int MaxMediaReferenceLength = 2048;

    /// <summary>
    /// Overall maximum body length across all types — the maximum of the per-type limits,
    /// which is <see cref="MaxDataLength"/>. The <c>content_blocks.body</c> column is bounded
    /// by this value (it equals the column size the table was created with, so no migration
    /// is required); the per-type limits above are the real, narrower bounds the validator
    /// enforces for each type.
    /// </summary>
    public const int MaxBodyLength = MaxDataLength;

    /// <summary>
    /// Whether the given body is valid for the given content type, AFTER the body has been
    /// trimmed by the caller. Returns <see langword="false"/> (never throws) for any
    /// invalid body so both the aggregate and the endpoint can branch on the result. An
    /// undefined type is rejected as a defensive guard; defined types apply their
    /// per-type rule:
    /// <list type="bullet">
    ///   <item><see cref="ContentBlockType.Text"/>: non-blank, no disallowed control
    ///   characters, within <see cref="MaxTextLength"/>.</item>
    ///   <item><see cref="ContentBlockType.Media"/>: a non-blank reference string, no
    ///   disallowed control characters, within <see cref="MaxMediaReferenceLength"/>.</item>
    ///   <item><see cref="ContentBlockType.Data"/>: non-blank, within
    ///   <see cref="MaxDataLength"/>, and WELL-FORMED JSON (parseable by
    ///   <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>); JSON schema is NOT
    ///   validated (that is template validation, a different epic).</item>
    /// </list>
    /// </summary>
    public static bool IsValidBody(ContentBlockType type, string? body)
    {
        if (!ContentBlock.IsValidType(type))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return type switch
        {
            // Text: bounded plain text. Keep the non-blank + disallowed-control-char rule
            // (tab/CR/LF are legitimate whitespace in prose) and apply the explicit Text
            // length bound.
            ContentBlockType.Text =>
                body.Length <= MaxTextLength && !HasDisallowedControlCharacter(body),

            // Media: a bounded, non-blank reference string. The same control-char rule keeps
            // the reference clean; the real asset linkage/validation is deferred (CORE-AST-005).
            ContentBlockType.Media =>
                body.Length <= MaxMediaReferenceLength && !HasDisallowedControlCharacter(body),

            // Data: bounded, well-formed JSON. The length is checked first (cheap) so an
            // oversized body is rejected without parsing; well-formedness is then verified by
            // attempting a parse. Only well-formedness is checked, never the JSON schema.
            ContentBlockType.Data =>
                body.Length <= MaxDataLength && IsWellFormedJson(body),

            _ => false,
        };
    }

    /// <summary>
    /// The explicit size limit (maximum body length) for the given content type. Used by
    /// callers and tests to exercise the per-type boundary without duplicating the
    /// constants. An undefined type falls back to the overall <see cref="MaxBodyLength"/>.
    /// </summary>
    public static int MaxLengthFor(ContentBlockType type) => type switch
    {
        ContentBlockType.Text => MaxTextLength,
        ContentBlockType.Media => MaxMediaReferenceLength,
        ContentBlockType.Data => MaxDataLength,
        _ => MaxBodyLength,
    };

    /// <summary>
    /// Whether the given value parses as well-formed JSON (object, array or any JSON
    /// scalar). Uses the built-in <see cref="JsonDocument"/> (System.Text.Json, no new
    /// dependency); a <see cref="JsonException"/> means the body is not well-formed JSON
    /// and is rejected. The parsed document is disposed immediately — only well-formedness
    /// matters here, not the contents.
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

    /// <summary>
    /// Whether the value contains a control character other than the common whitespace
    /// (tab, carriage return, line feed) that legitimately appears in textual and
    /// structured content.
    /// </summary>
    private static bool HasDisallowedControlCharacter(string value)
        => value.Any(IsDisallowedControlCharacter);

    private static bool IsDisallowedControlCharacter(char value)
        => char.IsControl(value) && value is not '\t' and not '\r' and not '\n';
}
