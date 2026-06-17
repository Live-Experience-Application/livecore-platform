// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace LiveCore.Api.UnitTests.Http;

/// <summary>
/// Unit coverage for the HTTP entity-tag surface of the optimistic-concurrency token
/// (CORE-DX-002, <see cref="EntityTag"/>): weak-ETag formatting and the <c>If-Match</c>
/// precondition decision. These are the provider-independent rules — a stale token is
/// rejected while a current one (or the absence of one) is accepted — exercised here
/// deterministically without a database; the full HTTP/SDK round-trip is covered by the
/// integration and SDK tests.
/// </summary>
public sealed class EntityTagTests
{
    [Fact]
    public void Weak_formats_the_token_as_a_weak_entity_tag()
    {
        Assert.Equal("W/\"8147\"", EntityTag.Weak("8147"));
    }

    [Theory]
    [InlineData("W/\"8147\"", "8147")]
    [InlineData("w/\"8147\"", "8147")] // tolerate a lower-case weak indicator
    [InlineData("\"8147\"", "8147")] // a strong tag's quoted body
    [InlineData("8147", "8147")] // the bare version value
    public void ExtractOpaqueValue_reduces_any_tag_form_to_its_opaque_value(string tag, string expected)
    {
        Assert.Equal(expected, EntityTag.ExtractOpaqueValue(tag));
    }

    [Fact]
    public void An_absent_if_match_is_not_provided()
    {
        Assert.Equal(IfMatchEvaluation.NotProvided, Evaluate(ifMatch: null, currentToken: "8147"));
    }

    [Fact]
    public void A_header_with_no_usable_tag_is_not_provided()
    {
        // A present-but-empty / comma-only header carries no entity-tag, so it is
        // treated as unconditional rather than a spurious precondition failure.
        Assert.Equal(IfMatchEvaluation.NotProvided, Evaluate(ifMatch: " , , ", currentToken: "8147"));
    }

    [Fact]
    public void The_wildcard_matches_any_current_representation()
    {
        Assert.Equal(IfMatchEvaluation.Matched, Evaluate(ifMatch: "*", currentToken: "8147"));
    }

    [Theory]
    [InlineData("W/\"8147\"")] // the exact weak tag the server emitted
    [InlineData("\"8147\"")] // the strong-quoted form
    [InlineData("8147")] // the bare version value
    public void A_current_token_matches(string ifMatch)
    {
        Assert.Equal(IfMatchEvaluation.Matched, Evaluate(ifMatch, currentToken: "8147"));
    }

    [Fact]
    public void A_stale_token_is_rejected()
    {
        Assert.Equal(IfMatchEvaluation.Stale, Evaluate(ifMatch: "W/\"8146\"", currentToken: "8147"));
    }

    [Fact]
    public void A_list_of_tags_matches_when_any_is_current()
    {
        Assert.Equal(IfMatchEvaluation.Matched, Evaluate(ifMatch: "W/\"1\", W/\"8147\"", currentToken: "8147"));
    }

    [Fact]
    public void A_list_of_tags_is_stale_when_none_is_current()
    {
        Assert.Equal(IfMatchEvaluation.Stale, Evaluate(ifMatch: "W/\"1\", W/\"2\"", currentToken: "8147"));
    }

    [Fact]
    public void A_supplied_tag_against_a_provider_without_a_token_fails_closed()
    {
        // When the resource has no readable token (a provider that maps none), an
        // If-Match cannot be confirmed, so it is rejected rather than ignored — the
        // production providers always carry the token, so this is the fail-closed edge.
        Assert.Equal(IfMatchEvaluation.Stale, Evaluate(ifMatch: "W/\"8147\"", currentToken: null));
    }

    private static IfMatchEvaluation Evaluate(string? ifMatch, string? currentToken)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is not null)
        {
            context.Request.Headers[EntityTag.IfMatchHeader] = new StringValues(ifMatch);
        }

        return EntityTag.EvaluateIfMatch(context.Request, currentToken);
    }
}
