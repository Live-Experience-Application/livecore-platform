// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.UnitTests.Http;

/// <summary>
/// Unit coverage for the shared bounded-pagination primitives (CORE-DX-003, <see cref="Page"/> and
/// <see cref="PageResponse{T}"/>): the provider-independent limit/offset parse-and-clamp rules and the page
/// envelope factory, exercised deterministically without a database (the full HTTP round-trip is covered by the
/// list-pagination integration tests). The rules mirror the audit read byte-for-byte.
/// </summary>
public sealed class PaginationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveLimit_defaults_when_absent(string? raw)
    {
        Assert.True(Page.TryResolveLimit(raw, out var limit, out var error));
        Assert.Equal(Page.DefaultLimit, limit);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryResolveLimit_accepts_a_positive_value_within_the_bound()
    {
        Assert.True(Page.TryResolveLimit("25", out var limit, out _));
        Assert.Equal(25, limit);
    }

    [Fact]
    public void TryResolveLimit_clamps_an_oversized_value_to_the_maximum()
    {
        Assert.True(Page.TryResolveLimit((Page.MaxLimit + 1000).ToString(), out var limit, out _));
        Assert.Equal(Page.MaxLimit, limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void TryResolveLimit_rejects_a_non_positive_or_non_integer_value(string raw)
    {
        Assert.False(Page.TryResolveLimit(raw, out _, out var error));
        Assert.Contains("limit", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryResolveOffset_defaults_to_zero_when_absent(string? raw)
    {
        Assert.True(Page.TryResolveOffset(raw, out var offset, out _));
        Assert.Equal(0, offset);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    public void TryResolveOffset_accepts_a_non_negative_integer(string raw, int expected)
    {
        Assert.True(Page.TryResolveOffset(raw, out var offset, out _));
        Assert.Equal(expected, offset);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("notanumber")]
    public void TryResolveOffset_rejects_a_negative_or_non_integer_value(string raw)
    {
        Assert.False(Page.TryResolveOffset(raw, out _, out var error));
        Assert.Contains("offset", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PageResponse_From_carries_the_bounds_and_items()
    {
        var items = new[] { "a", "b" };

        var page = PageResponse.From(items, offset: 10, limit: 2, hasMore: true);

        Assert.Equal(10, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.True(page.HasMore);
        Assert.Equal(items, page.Items);
    }
}
