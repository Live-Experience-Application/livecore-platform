// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="RequestLogContext"/> (CORE-OBS-002), the request-scoped holder of the documented
/// per-request log identifiers (docs/15_OBSERVABILITY.md). They pin the behavior the JSON console formatter
/// and the log-scope middleware rely on: only POPULATED keys are enumerated (so an unset "when applicable"
/// key is omitted rather than logged blank), enumeration reflects values set after the scope opened (the
/// mutability that lets the tenant resolver / event publisher enrich the same scope), surrogate ids render as
/// canonical strings, and only identifiers are ever exposed (threat T7).
/// </summary>
public sealed class RequestLogContextTests
{
    [Fact]
    public void A_new_context_exposes_no_keys()
    {
        var context = new RequestLogContext();

        Assert.Empty(context);
    }

    [Fact]
    public void It_exposes_only_the_populated_keys_with_their_documented_names()
    {
        var organizationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var context = new RequestLogContext();
        context.SetRequestId("0HMABC:00000001");
        context.SetOrganizationId(organizationId);
        context.SetWorkspaceId(workspaceId);
        context.SetUserId("subject-123");

        var values = context.ToDictionary(pair => pair.Key, pair => pair.Value);

        // The four set keys are present (and the two unset ones absent below), which fully accounts for every
        // documented key — so no extra key can have leaked in.
        Assert.Equal("0HMABC:00000001", values[RequestLogContext.RequestIdKey]);
        Assert.Equal(organizationId.ToString("D"), values[RequestLogContext.OrganizationIdKey]);
        Assert.Equal(workspaceId.ToString("D"), values[RequestLogContext.WorkspaceIdKey]);
        Assert.Equal("subject-123", values[RequestLogContext.UserIdKey]);

        // The "when applicable" keys that were never set are absent (not logged blank).
        Assert.DoesNotContain(RequestLogContext.SessionIdKey, values.Keys);
        Assert.DoesNotContain(RequestLogContext.EventIdKey, values.Keys);
    }

    [Fact]
    public void A_key_set_after_enumeration_appears_on_the_next_enumeration()
    {
        // The middleware opens ONE scope with this object and downstream owners enrich it; the JSON formatter
        // enumerates the scope on every log line, so a late-set key must appear afterwards.
        var context = new RequestLogContext();
        context.SetRequestId("req-1");
        Assert.DoesNotContain(RequestLogContext.EventIdKey, context.Select(pair => pair.Key));

        var eventId = Guid.NewGuid();
        context.SetEventId(eventId);

        var values = context.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(eventId.ToString("D"), values[RequestLogContext.EventIdKey]);
    }

    [Fact]
    public void Blank_or_empty_values_are_ignored_so_logging_never_throws()
    {
        var context = new RequestLogContext();
        context.SetRequestId("   ");
        context.SetRequestId(null);
        context.SetUserId(null);
        context.SetOrganizationId(Guid.Empty);
        context.SetWorkspaceId(Guid.Empty);
        context.SetSessionId(Guid.Empty);
        context.SetEventId(Guid.Empty);

        Assert.Empty(context);
    }

    [Fact]
    public void ToString_renders_only_identifier_key_value_pairs()
    {
        var organizationId = Guid.NewGuid();
        var context = new RequestLogContext();
        context.SetRequestId("req-7");
        context.SetOrganizationId(organizationId);

        var rendered = context.ToString();

        Assert.Contains($"{RequestLogContext.RequestIdKey}=req-7", rendered, StringComparison.Ordinal);
        Assert.Contains(
            $"{RequestLogContext.OrganizationIdKey}={organizationId:D}",
            rendered,
            StringComparison.Ordinal);
    }
}
