// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Recaps;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Unit tests for the <see cref="Recap"/> invariants and its <see cref="Recap.GenerateByHost"/> /
/// <see cref="Recap.GenerateBySystem"/> factories (CORE-AUD-004): a recap is the generic, product-neutral
/// "session summary or structured continuation output" (docs/03_DOMAIN_LANGUAGE.md, docs/05_MODULE_CONTRACTS.md).
/// It is session-, workspace- and tenant-scoped and belongs only to one organization, workspace and session;
/// it carries the host-only <see cref="Recap.Summary"/> body and may be produced by a host OR the system
/// (docs/09_EVENT_CATALOG.md <c>RecapGenerated</c> source "System/Host"). The object-level boundary checks
/// (BelongsToOrganization/Workspace/Session, Identifies) are the fail-closed isolation guards (threats T5/T1),
/// and <see cref="Recap.ToString"/> stays log-safe and EXCLUDES the body (threat T7). No vertical-specific
/// terms appear anywhere — generic Core vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class RecapTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _sessionId = Guid.CreateVersion7();
    private static readonly Guid _foreignSessionId = Guid.CreateVersion7();
    private static readonly Guid _generatedBy = Guid.CreateVersion7();

    private const string _summary = "Session opened, the audience reviewed three scenes and one entity was revealed.";

    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private static Recap HostRecap()
        => Recap.GenerateByHost(_organizationId, _workspaceId, _sessionId, _generatedBy, _summary, _generatedAt);

    // --- Factory: GenerateByHost -----------------------------------------------

    [Fact]
    public void GenerateByHost_builds_the_recap()
    {
        var recap = HostRecap();

        Assert.NotEqual(Guid.Empty, recap.Id);
        Assert.Equal(7, recap.Id.Version); // UUIDv7, time-ordered (docs/10_DATABASE_SCHEMA.md)
        Assert.Equal(_organizationId, recap.OrganizationId);
        Assert.Equal(_workspaceId, recap.WorkspaceId);
        Assert.Equal(_sessionId, recap.SessionId);
        Assert.Equal(_generatedBy, recap.GeneratedByUserProfileId);
        Assert.Equal(Recap.CurrentRecapVersion, recap.RecapVersion);
        Assert.Equal(_summary, recap.Summary);
        Assert.Equal(_generatedAt, recap.GeneratedAt);
    }

    [Fact]
    public void GenerateByHost_trims_the_summary()
    {
        var recap = Recap.GenerateByHost(
            _organizationId, _workspaceId, _sessionId, _generatedBy, $"   {_summary}   ", _generatedAt);

        Assert.Equal(_summary, recap.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GenerateByHost_rejects_a_blank_summary(string? summary)
        => Assert.Throws<ArgumentException>(
            () => Recap.GenerateByHost(_organizationId, _workspaceId, _sessionId, _generatedBy, summary!, _generatedAt));

    [Fact]
    public void GenerateByHost_rejects_an_oversize_summary()
    {
        var tooLong = new string('x', Recap.MaxSummaryLength + 1);

        Assert.Throws<ArgumentException>(
            () => Recap.GenerateByHost(_organizationId, _workspaceId, _sessionId, _generatedBy, tooLong, _generatedAt));
    }

    [Fact]
    public void GenerateByHost_allows_a_summary_at_the_maximum_length()
    {
        var atLimit = new string('x', Recap.MaxSummaryLength);

        var recap = Recap.GenerateByHost(
            _organizationId, _workspaceId, _sessionId, _generatedBy, atLimit, _generatedAt);

        Assert.Equal(Recap.MaxSummaryLength, recap.Summary.Length);
    }

    [Fact]
    public void GenerateByHost_allows_a_multi_line_summary()
    {
        // A recap body is content (a summary or structured continuation output), so newlines are valid —
        // unlike a single-line failure reason.
        const string multiLine = "Line one.\nLine two.\nLine three.";

        var recap = Recap.GenerateByHost(
            _organizationId, _workspaceId, _sessionId, _generatedBy, multiLine, _generatedAt);

        Assert.Equal(multiLine, recap.Summary);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void GenerateByHost_rejects_an_empty_required_id(bool org, bool workspace, bool session)
    {
        var organizationId = org ? _organizationId : Guid.Empty;
        var workspaceId = workspace ? _workspaceId : Guid.Empty;
        var sessionId = session ? _sessionId : Guid.Empty;

        Assert.Throws<ArgumentException>(
            () => Recap.GenerateByHost(organizationId, workspaceId, sessionId, _generatedBy, _summary, _generatedAt));
    }

    [Fact]
    public void GenerateByHost_rejects_an_empty_producer_id()
        => Assert.Throws<ArgumentException>(
            () => Recap.GenerateByHost(_organizationId, _workspaceId, _sessionId, Guid.Empty, _summary, _generatedAt));

    // --- Factory: GenerateBySystem ---------------------------------------------

    [Fact]
    public void GenerateBySystem_builds_a_recap_with_no_producer()
    {
        // A recap may be produced automatically by the platform: the producer is recorded as null
        // (docs/09_EVENT_CATALOG.md RecapGenerated source "System/Host").
        var recap = Recap.GenerateBySystem(_organizationId, _workspaceId, _sessionId, _summary, _generatedAt);

        Assert.Null(recap.GeneratedByUserProfileId);
        Assert.Equal(_organizationId, recap.OrganizationId);
        Assert.Equal(_workspaceId, recap.WorkspaceId);
        Assert.Equal(_sessionId, recap.SessionId);
        Assert.Equal(_summary, recap.Summary);
        Assert.Equal(Recap.CurrentRecapVersion, recap.RecapVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GenerateBySystem_rejects_a_blank_summary(string? summary)
        => Assert.Throws<ArgumentException>(
            () => Recap.GenerateBySystem(_organizationId, _workspaceId, _sessionId, summary!, _generatedAt));

    // --- UTC normalization ------------------------------------------------------

    [Fact]
    public void Generated_at_is_normalized_to_utc()
    {
        var localGeneratedAt = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var recap = Recap.GenerateByHost(
            _organizationId, _workspaceId, _sessionId, _generatedBy, _summary, localGeneratedAt);

        Assert.Equal(TimeSpan.Zero, recap.GeneratedAt.Offset);
        Assert.Equal(localGeneratedAt.ToUniversalTime(), recap.GeneratedAt);
    }

    // --- Object-level boundary checks (fail-closed isolation) -------------------

    [Fact]
    public void BelongsToOrganization_matches_only_the_owning_tenant()
    {
        var recap = HostRecap();

        Assert.True(recap.BelongsToOrganization(_organizationId));
        Assert.False(recap.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(recap.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void BelongsToWorkspace_matches_only_the_owning_workspace()
    {
        var recap = HostRecap();

        Assert.True(recap.BelongsToWorkspace(_workspaceId));
        Assert.False(recap.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(recap.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void BelongsToSession_matches_only_the_summarized_session()
    {
        var recap = HostRecap();

        Assert.True(recap.BelongsToSession(_sessionId));
        Assert.False(recap.BelongsToSession(_foreignSessionId));
        Assert.False(recap.BelongsToSession(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_tenant_workspace_and_id_to_all_match()
    {
        var recap = HostRecap();

        Assert.True(recap.Identifies(_organizationId, _workspaceId, recap.Id));
        // A foreign tenant, a foreign workspace, a wrong id or an empty id all fail closed.
        Assert.False(recap.Identifies(_foreignOrganizationId, _workspaceId, recap.Id));
        Assert.False(recap.Identifies(_organizationId, _foreignWorkspaceId, recap.Id));
        Assert.False(recap.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(recap.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Log safety (threat T7): the body is never in ToString ------------------

    [Fact]
    public void ToString_is_log_safe_and_excludes_the_summary_body()
    {
        const string secretBody = "PRIVATE-RECAP-BODY-THAT-MUST-NEVER-BE-LOGGED";
        var recap = Recap.GenerateByHost(
            _organizationId, _workspaceId, _sessionId, _generatedBy, secretBody, _generatedAt);

        var rendered = recap.ToString();

        // Identifiers, version and coarse length are present...
        Assert.Contains(recap.Id.ToString(), rendered);
        Assert.Contains($"org={_organizationId}", rendered);
        Assert.Contains($"ws={_workspaceId}", rendered);
        Assert.Contains($"session={_sessionId}", rendered);
        Assert.Contains($"generatedBy={_generatedBy}", rendered);
        Assert.Contains($"length={secretBody.Length}", rendered);
        // ...but the host-only body itself is NEVER rendered (threat T7).
        Assert.DoesNotContain(secretBody, rendered);
    }

    [Fact]
    public void ToString_renders_a_system_recap_producer_as_system()
    {
        var recap = Recap.GenerateBySystem(_organizationId, _workspaceId, _sessionId, _summary, _generatedAt);

        Assert.Contains("generatedBy=system", recap.ToString());
    }

    // --- Validation helper ------------------------------------------------------

    [Theory]
    [InlineData("a valid summary", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidSummary_enforces_non_blank(string? value, bool expected)
        => Assert.Equal(expected, Recap.IsValidSummary(value));
}
