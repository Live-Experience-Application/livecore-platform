using System.Net;
using LiveCore.Api.Observability;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the per-request structured log context (CORE-OBS-002) — the story's required
/// test: "a request log line includes the context keys and no PII/secret". They drive the real application
/// over real HTTP through a <see cref="WorkspaceApiFactory"/> with a capturing logging provider added
/// alongside the production JSON console formatter, so the scopes asserted on are exactly the scopes a real
/// log sink would render (the documented request flow authentication -> log scope -> tenant resolver ->
/// endpoint is exercised end-to-end).
///
/// The assertions cover both halves of the acceptance criterion:
/// <list type="bullet">
///   <item>a real authenticated request's log line carries the documented context — <c>request_id</c>,
///   <c>organization_id</c> (from the resolved tenant), <c>workspace_id</c> (from the route) and
///   <c>user_id</c> (the principal's subject);</item>
///   <item>no PII or secret leaks — the caller's email (presented on the token) appears in NO captured log
///   line or scope value (threat T7), and the user identity logged is the opaque subject, never the email;</item>
///   <item>fail-safe: an unauthenticated request still gets a correlatable <c>request_id</c> but carries no
///   <c>user_id</c> and no <c>organization_id</c> — a denied request never logs a tenant or principal it did
///   not resolve.</item>
/// </list>
/// </summary>
public sealed class RequestLogContextEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _org = "northwind-labs";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _email = "observed.user@example.test";
    private const string _summaryMessagePrefix = "Handled request";

    [Fact]
    public async Task Authenticated_request_log_line_carries_the_documented_context_and_leaks_no_pii()
    {
        await using var factory = new LogCapturingApiFactory();

        var orgId = Guid.Empty;
        var workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, _subject);
            var org = await db.AddOrganizationAsync(_org);
            orgId = org.Id;
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(_subject, _issuer, _org);
        // Present a token that carries PII (the email claim) so we can assert it never reaches the logs.
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.EmailHeader, _email);

        using var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}?organizationSlug={_org}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = SingleRequestSummary(factory.LogProvider.Entries);

        // The documented per-request context keys are present with the resolved identifier values.
        Assert.False(string.IsNullOrWhiteSpace(ScopeValue(summary, RequestLogContext.RequestIdKey)));
        Assert.Equal(orgId.ToString("D"), ScopeValue(summary, RequestLogContext.OrganizationIdKey));
        Assert.Equal(workspaceId.ToString("D"), ScopeValue(summary, RequestLogContext.WorkspaceIdKey));
        Assert.Equal(_subject, ScopeValue(summary, RequestLogContext.UserIdKey));

        // The user identity logged is the opaque subject, never the email — and no PII reaches ANY log line
        // (message or scope), nor any access token (threat T7).
        Assert.DoesNotContain(_email, summary.Message, StringComparison.OrdinalIgnoreCase);
        foreach (var entry in factory.LogProvider.Entries)
        {
            Assert.DoesNotContain(_email, entry.Message, StringComparison.OrdinalIgnoreCase);
            foreach (var value in entry.ScopeValues.Values)
            {
                Assert.DoesNotContain(_email, value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Unauthenticated_request_still_logs_a_request_id_but_no_user_or_tenant()
    {
        await using var factory = new LogCapturingApiFactory();
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_org}");

        // The route requires authorization, so an anonymous caller is challenged with 401 — inside the log
        // scope, so the denial is still logged with a correlatable request_id.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var summary = SingleRequestSummary(factory.LogProvider.Entries);

        Assert.False(string.IsNullOrWhiteSpace(ScopeValue(summary, RequestLogContext.RequestIdKey)));
        // Fail-safe: no user_id (anonymous) and no organization_id (the tenant was never resolved) are logged.
        Assert.False(summary.ScopeValues.ContainsKey(RequestLogContext.UserIdKey));
        Assert.False(summary.ScopeValues.ContainsKey(RequestLogContext.OrganizationIdKey));
    }

    private static CapturedLogEntry SingleRequestSummary(IReadOnlyList<CapturedLogEntry> entries)
        => Assert.Single(
            entries,
            entry => entry.Message.StartsWith(_summaryMessagePrefix, StringComparison.Ordinal));

    private static string? ScopeValue(CapturedLogEntry entry, string key)
        => entry.ScopeValues.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that additionally registers a <see cref="CapturingLoggerProvider"/>
    /// alongside the production logging, so the test can observe the structured scopes a request produces. The
    /// base factory's persistence and test-authentication wiring is preserved (base is called first).
    /// </summary>
    private sealed class LogCapturingApiFactory : WorkspaceApiFactory
    {
        public CapturingLoggerProvider LogProvider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(LogProvider));
        }
    }
}
