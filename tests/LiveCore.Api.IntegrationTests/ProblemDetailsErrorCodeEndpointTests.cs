using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Entitlements;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the stable, machine-readable Problem Details error codes (CORE-DX-001).
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/> and assert
/// the <c>code</c> extension member every error body now carries (apps/api/CoreProblem.cs,
/// apps/api/ProblemCodes.cs), so a consumer can branch on the code rather than the human
/// <c>title</c>/<c>detail</c> prose.
///
/// The story's required tests:
/// <list type="bullet">
///   <item>the three structurally-distinct <c>409</c>s — quota-exceeded, workspace-archived and
///   concurrency-conflict — return three DISTINCT stable codes (so a consumer can tell them apart);</item>
///   <item>a sample of <c>4xx</c>/<c>5xx</c> responses each carry a code drawn from the documented
///   <see cref="ProblemCodes"/> catalog.</item>
/// </list>
/// The published <c>@livecore/contracts</c> <c>ProblemCodes</c> enum is asserted to match this server
/// catalog by the cross-language contract test <c>ProblemCodeCatalogContractTests</c>
/// (LiveCore.Api.UnitTests). All fixtures are generic Core vocabulary (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ProblemDetailsErrorCodeEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- The three distinct 409s carry three distinct codes -----------------

    [Fact]
    public async Task The_three_distinct_409s_carry_three_distinct_stable_codes()
    {
        var quotaCode = await QuotaExceededCodeAsync();
        var archivedCode = await ArchivedWorkspaceCodeAsync();
        var concurrencyCode = await ConcurrencyConflictCodeAsync();

        // Each is the specific code its condition documents (not a generic "conflict").
        Assert.Equal("quota_exceeded", quotaCode);
        Assert.Equal("workspace_archived", archivedCode);
        Assert.Equal("concurrency_conflict", concurrencyCode);

        // ...and the three are pairwise distinct, so a consumer branches correctly on the code alone.
        var codes = new[] { quotaCode, archivedCode, concurrencyCode };
        Assert.Equal(3, codes.Distinct().Count());

        // Every one is drawn from the documented catalog.
        Assert.All(codes, code => Assert.Contains(code, ProblemCodes.Set));
    }

    // ---- A sample of 4xx/5xx responses each carry a catalog code ------------

    [Fact]
    public async Task A_sample_of_4xx_and_5xx_error_responses_each_carry_a_code_from_the_catalog()
    {
        var samples = new List<(HttpStatusCode Status, string Code)>
        {
            await ValidationError400Async(),
            await Forbidden403Async(),
            await NotFound404Async(),
            await ArchivedConflict409Async(),
            await ServiceUnavailable503Async(),
        };

        // The sample spans the 4xx classes and a 5xx.
        Assert.Contains(samples, s => (int)s.Status is >= 400 and < 500);
        Assert.Contains(samples, s => (int)s.Status is >= 500 and < 600);

        // Every Problem Details body in the sample carries a non-empty code from the documented catalog.
        Assert.All(samples, sample =>
        {
            Assert.False(string.IsNullOrEmpty(sample.Code), $"{(int)sample.Status} carried no code");
            Assert.Contains(sample.Code, ProblemCodes.Set);
        });

        // The codes are the documented ones for each class (the prose may change; the code may not).
        Assert.Equal("validation_error", Code(samples, HttpStatusCode.BadRequest));
        Assert.Equal("permission_denied", Code(samples, HttpStatusCode.Forbidden));
        Assert.Equal("not_found", Code(samples, HttpStatusCode.NotFound));
        Assert.Equal("workspace_archived", Code(samples, HttpStatusCode.Conflict));
        Assert.Equal("service_unavailable", Code(samples, HttpStatusCode.ServiceUnavailable));
    }

    private static string Code(
        IEnumerable<(HttpStatusCode Status, string Code)> samples,
        HttpStatusCode status)
        => samples.Single(s => s.Status == status).Code;

    // ---- Scenario producers --------------------------------------------------

    private static async Task<string> QuotaExceededCodeAsync()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
            var entitlement = await db.AddQuotaEntitlementDefinitionAsync(QuotaEntitlementKeys.WorkspaceActiveMax);
            await db.AddQuotaDefinitionAsync(entitlement, EntitlementSubjectType.User);
            await db.AddSubjectQuotaEntitlementAsync(EntitlementSubjectType.User, user.Id, entitlement, limit: 1);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First create lands at the limit of 1 (allowed); the second would exceed it (409).
        var first = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "first", "First"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/v1/workspaces", new CreateWorkspaceRequest(_orgA, "second", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        return await ReadCodeAsync(second);
    }

    private static async Task<string> ArchivedWorkspaceCodeAsync()
    {
        var (status, code) = await ArchivedConflict409Async();
        Assert.Equal(HttpStatusCode.Conflict, status);
        return code;
    }

    private static async Task<(HttpStatusCode Status, string Code)> ArchivedConflict409Async()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show", archived: true);
            workspaceId = workspace.Id;
            // A workspace membership with a session-control role, so the request reaches the archived guard
            // (which sits AFTER role authorization) rather than being denied at the role check.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions",
            new { organizationSlug = _orgA, title = "Opening Night" });

        return (response.StatusCode, await ReadCodeAsync(response));
    }

    /// <summary>
    /// Runs <see cref="ConcurrencyConflictMiddleware"/> over a stale-write exception end-to-end and reads
    /// the <c>code</c> from the real <c>application/problem+json</c> body it writes. The middleware is the
    /// sole producer of the concurrency-conflict 409; inducing the underlying <c>xmin</c> race over real
    /// HTTP is a deterministic-only-on-PostgreSQL scenario proven by
    /// <see cref="SavepointRecoveryAndHttpConflictTests"/>, so this asserts the resulting code
    /// provider-independently.
    /// </summary>
    private static async Task<string> ConcurrencyConflictCodeAsync()
    {
        var middleware = new ConcurrencyConflictMiddleware(
            _ => throw new DbUpdateConcurrencyException("The row was modified concurrently."));

        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        return document.RootElement.GetProperty(ProblemCodes.Member).GetString()!;
    }

    private static async Task<(HttpStatusCode Status, string Code)> ValidationError400Async()
    {
        // A blank purchase token is rejected as 400 before any verification runs.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);
        var response = await client.PostAsJsonAsync(
            "/api/v1/purchases/google/tokens", new { purchaseToken = "", productReference = "product.premium" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (response.StatusCode, await ReadCodeAsync(response));
    }

    private static async Task<(HttpStatusCode Status, string Code)> Forbidden403Async()
    {
        // Archive is Owner-only; an authenticated Admin org member is forbidden (a handler-produced 403,
        // not the framework challenge).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "admin-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Admin);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/archive?organizationSlug={_orgA}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        return (response.StatusCode, await ReadCodeAsync(response));
    }

    private static async Task<(HttpStatusCode Status, string Code)> NotFound404Async()
    {
        // An owner requesting an unknown workspace id: hidden as 404 (a fail-closed denial).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        return (response.StatusCode, await ReadCodeAsync(response));
    }

    private static async Task<(HttpStatusCode Status, string Code)> ServiceUnavailable503Async()
    {
        // With no Google verifier configured (the production default), an authorized request is 503.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("buyer-a", _issuer);
        var response = await client.PostAsJsonAsync(
            "/api/v1/purchases/google/tokens",
            new { purchaseToken = "genuine-token", productReference = "product.premium" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        return (response.StatusCode, await ReadCodeAsync(response));
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            document.RootElement.TryGetProperty(ProblemCodes.Member, out var code),
            "the Problem Details body carries no 'code' member");
        var value = code.GetString();
        Assert.False(string.IsNullOrEmpty(value));
        return value!;
    }
}
