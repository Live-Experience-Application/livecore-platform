namespace LiveCore.Api.Content;

/// <summary>
/// Generic kind discriminator of a <see cref="ContentBlock"/> (CORE-SCENE-002). A
/// content block is the Core-owned, product-neutral "Text/media/data unit shown or
/// hidden by visibility rules" (docs/03_DOMAIN_LANGUAGE.md), so the three kinds below
/// are exactly the generic Text / media / data triad named there. They carry no
/// vertical product meaning (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md;
/// csv/forbidden_core_terms.csv): a vertical maps these to its own content kinds in
/// its UI; Core stores only the generic discriminator.
///
/// This is deliberately a minimal, fixed kind discriminator, not the full "content
/// type registry" the Content module owns (docs/05_MODULE_CONTRACTS.md). A
/// data-driven, template-defined content type registry is a later slice; modelling
/// it now would be speculative scope. The discriminator stays a closed generic enum
/// so the aggregate has a stable, validated shape.
///
/// The kind is persisted by its stable name (not its numeric value), exactly like
/// <c>SessionStatus</c> and <c>ParticipantStatus</c>, so the integers below are only
/// in-memory storage discriminators and carry no ordering meaning; they must not be
/// compared with &gt;/&lt;.
/// </summary>
public enum ContentBlockType
{
    /// <summary>
    /// A textual content block: its body holds free-form text
    /// (docs/03_DOMAIN_LANGUAGE.md: a content block is a "Text/media/data unit").
    /// </summary>
    Text = 1,

    /// <summary>
    /// A media content block: its body holds a reference to a media object (for
    /// example a later <c>Asset</c> id or media reference). Core stores only the
    /// generic reference; asset authorization and signed URLs are the Assets module's
    /// concern (docs/05_MODULE_CONTRACTS.md).
    /// </summary>
    Media = 2,

    /// <summary>
    /// A data content block: its body holds a structured data payload (for example a
    /// JSON document of template-defined attributes, docs/10_DATABASE_SCHEMA.md
    /// permits JSONB for flexible attributes but never for authorization fields).
    /// </summary>
    Data = 3,
}
