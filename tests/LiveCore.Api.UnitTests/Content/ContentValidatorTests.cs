using LiveCore.Api.Content;

namespace LiveCore.Api.UnitTests.Content;

/// <summary>
/// Validation tests for <see cref="ContentValidator"/> (CORE-SCENE-005: "Implement
/// content validation and size limits"). They exercise the per-type rules and explicit
/// size limits IN ISOLATION, exactly as the story's required "validation tests" ask: for
/// each content type a valid body is accepted and an invalid body is rejected (Data:
/// malformed JSON rejected / valid JSON accepted; Text: control-char/blank rejected;
/// Media: blank rejected), plus per-type at-limit/over-limit size boundaries using the
/// named constants.
///
/// The validator decides ONLY well-formedness + size; it never decides visibility and
/// never resolves a Media reference against a real asset (no vertical terms appear — only
/// generic ContentBlock vocabulary, AGENTS.md / csv/forbidden_core_terms.csv).
/// </summary>
public class ContentValidatorTests
{
    // --- Blank / undefined-type rejection (all types) --------------------------

    [Theory]
    [InlineData(ContentBlockType.Text, "")]
    [InlineData(ContentBlockType.Text, "   ")]
    [InlineData(ContentBlockType.Text, null)]
    [InlineData(ContentBlockType.Media, "")]
    [InlineData(ContentBlockType.Media, "   ")]
    [InlineData(ContentBlockType.Media, null)]
    [InlineData(ContentBlockType.Data, "")]
    [InlineData(ContentBlockType.Data, "   ")]
    [InlineData(ContentBlockType.Data, null)]
    public void Blank_body_is_rejected_for_every_type(ContentBlockType type, string? body)
        => Assert.False(ContentValidator.IsValidBody(type, body));

    [Fact]
    public void An_undefined_type_is_rejected()
        => Assert.False(ContentValidator.IsValidBody((ContentBlockType)999, "anything"));

    // --- Text ------------------------------------------------------------------

    [Fact]
    public void Text_accepts_bounded_plain_text_including_common_whitespace()
    {
        Assert.True(ContentValidator.IsValidBody(ContentBlockType.Text, "The opening narration."));
        // Tab/CR/LF are legitimate whitespace in prose.
        Assert.True(ContentValidator.IsValidBody(ContentBlockType.Text, "line one\nline two\tindented"));
    }

    [Fact]
    public void Text_rejects_a_disallowed_control_character()
        => Assert.False(ContentValidator.IsValidBody(ContentBlockType.Text, "bad\0body"));

    // --- Media -----------------------------------------------------------------

    [Theory]
    [InlineData("asset://ref-1")]
    [InlineData("https://media.example.test/object/123")]
    public void Media_accepts_a_bounded_reference_string(string reference)
        => Assert.True(ContentValidator.IsValidBody(ContentBlockType.Media, reference));

    [Fact]
    public void Media_rejects_a_disallowed_control_character()
        => Assert.False(ContentValidator.IsValidBody(ContentBlockType.Media, "asset://\0ref"));

    // --- Data (well-formed JSON, well-formedness only) -------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"key\":\"value\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"nested\":{\"a\":[true,false,null]}}")]
    [InlineData("42")] // a bare JSON scalar is still well-formed JSON
    public void Data_accepts_well_formed_json(string body)
        => Assert.True(ContentValidator.IsValidBody(ContentBlockType.Data, body));

    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{\"unterminated\":")]
    [InlineData("{ key: 'value' }")] // unquoted key + single quotes
    [InlineData("{\"a\":1,}")] // trailing comma is not valid JSON
    public void Data_rejects_malformed_json(string body)
        => Assert.False(ContentValidator.IsValidBody(ContentBlockType.Data, body));

    // --- Per-type size limits (at-limit accepted, over-limit rejected) ---------

    [Theory]
    [InlineData(ContentBlockType.Text)]
    [InlineData(ContentBlockType.Media)]
    public void A_body_at_exactly_the_per_type_limit_is_accepted(ContentBlockType type)
    {
        var atLimit = new string('a', ContentValidator.MaxLengthFor(type));

        Assert.True(ContentValidator.IsValidBody(type, atLimit));
    }

    [Theory]
    [InlineData(ContentBlockType.Text)]
    [InlineData(ContentBlockType.Media)]
    [InlineData(ContentBlockType.Data)]
    public void A_body_one_over_the_per_type_limit_is_rejected(ContentBlockType type)
    {
        // For Text/Media the over-limit content is plain characters; for Data it is a JSON
        // string literal padded past the Data limit, so the body is well-formed JSON yet
        // still rejected solely on SIZE.
        var overLimit = type == ContentBlockType.Data
            ? "\"" + new string('a', ContentValidator.MaxLengthFor(type)) + "\""
            : new string('a', ContentValidator.MaxLengthFor(type) + 1);

        Assert.False(ContentValidator.IsValidBody(type, overLimit));
    }

    [Fact]
    public void A_data_body_at_exactly_the_data_limit_is_accepted()
    {
        // Build a well-formed JSON string of exactly MaxDataLength characters: the two
        // quote characters plus (MaxDataLength - 2) padding characters.
        var atLimit = "\"" + new string('a', ContentValidator.MaxDataLength - 2) + "\"";
        Assert.Equal(ContentValidator.MaxDataLength, atLimit.Length);

        Assert.True(ContentValidator.IsValidBody(ContentBlockType.Data, atLimit));
    }

    // --- Constant relationships ------------------------------------------------

    [Fact]
    public void The_overall_body_bound_is_the_maximum_of_the_per_type_limits()
    {
        var maxPerType = Math.Max(
            ContentValidator.MaxTextLength,
            Math.Max(ContentValidator.MaxMediaReferenceLength, ContentValidator.MaxDataLength));

        Assert.Equal(ContentValidator.MaxBodyLength, maxPerType);
        // The aggregate re-exposes the same overall bound for the persistence column.
        Assert.Equal(ContentValidator.MaxBodyLength, ContentBlock.MaxBodyLength);
        // Data is the most generous per-type bound, so it sets the column bound.
        Assert.Equal(ContentValidator.MaxDataLength, ContentValidator.MaxBodyLength);
    }

    [Fact]
    public void MaxLengthFor_returns_the_per_type_constant()
    {
        Assert.Equal(ContentValidator.MaxTextLength, ContentValidator.MaxLengthFor(ContentBlockType.Text));
        Assert.Equal(ContentValidator.MaxMediaReferenceLength, ContentValidator.MaxLengthFor(ContentBlockType.Media));
        Assert.Equal(ContentValidator.MaxDataLength, ContentValidator.MaxLengthFor(ContentBlockType.Data));
    }

    [Fact]
    public void The_per_type_size_limits_are_the_documented_literal_values()
    {
        // Pin the explicit per-type size limits to their documented literal values: a
        // silent change to any constant (which would widen or narrow what the API accepts)
        // fails this test. These ARE the content-shape limits CORE-SCENE-005 establishes,
        // so the size limit itself — not just the boundary comparison — is regression-locked
        // (the boundary tests above derive their inputs from the constants and so cannot
        // catch a re-valued constant on their own).
        Assert.Equal(16384, ContentValidator.MaxTextLength);
        Assert.Equal(2048, ContentValidator.MaxMediaReferenceLength);
        Assert.Equal(65536, ContentValidator.MaxDataLength);
        Assert.Equal(65536, ContentValidator.MaxBodyLength);
    }
}
