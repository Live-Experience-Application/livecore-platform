using System.Net;
using System.Text.Json;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the global Problem Details exception handler (CORE-RES-001). They drive the real
/// application over real HTTP (<see cref="WorkspaceApiFactory"/>) with ONE production dependency substituted by
/// a faulty one that throws an unexpected exception on a normal request path, so an unhandled exception genuinely
/// reaches the host. The story's required test: "an induced unhandled error returns a Problem Details body (not a
/// raw 500); the error response leaks no internal detail."
///
/// The substituted seam is the <see cref="IOrganizationRepository"/> the authenticated <c>GET /api/v1/me</c>
/// route reads its memberships from — replaced with a repository that throws. Only that one seam is faulty; the
/// rest of the pipeline (auth, routing, the request middleware) runs exactly as in production, so the failure is
/// a representative unexpected server fault, not a contrived endpoint. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class GlobalExceptionHandlerEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    // A distinctive internal detail the induced exception carries. It (and the exception type/stack) must NEVER
    // appear in the response body returned to the caller (threat T7).
    private const string _inducedInternalDetail = "internal failure at /db/secret-table for tenant 42";

    [Fact]
    public async Task An_induced_unhandled_error_returns_a_problem_details_500_not_a_raw_500()
    {
        await using var factory = new FaultyDependencyApiFactory(_inducedInternalDetail);
        using var client = factory.CreateClientFor("user-a", _issuer, _orgA);

        using var response = await client.GetAsync("/api/v1/me");

        // Not a bare framework 500: a 500 whose body is an RFC 7807 Problem Details carrying the documented
        // internal_error code (CORE-DX-001).
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            ProblemCodes.InternalError,
            document.RootElement.GetProperty(ProblemCodes.Member).GetString());
    }

    [Fact]
    public async Task The_induced_error_response_leaks_no_internal_detail()
    {
        await using var factory = new FaultyDependencyApiFactory(_inducedInternalDetail);
        using var client = factory.CreateClientFor("user-a", _issuer, _orgA);

        using var response = await client.GetAsync("/api/v1/me");
        var body = await response.Content.ReadAsStringAsync();

        // The body must not echo the exception message, its type name, a stack frame or the faulting symbol
        // (threat T7) — only a generic title/detail and the stable code.
        Assert.DoesNotContain(_inducedInternalDetail, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("ListMembershipsByMemberAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/db/secret-table", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            "An unexpected error occurred while processing the request.",
            document.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that substitutes a faulty <see cref="IOrganizationRepository"/> for
    /// the production one, so a normal authenticated request reaches an unhandled exception. Only that seam is
    /// swapped; every other production behavior runs unchanged.
    /// </summary>
    private sealed class FaultyDependencyApiFactory : WorkspaceApiFactory
    {
        private readonly string _failureDetail;

        public FaultyDependencyApiFactory(string failureDetail) => _failureDetail = failureDetail;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrganizationRepository>();
                services.AddScoped<IOrganizationRepository>(_ => new FaultyOrganizationRepository(_failureDetail));
            });
        }
    }

    /// <summary>
    /// An <see cref="IOrganizationRepository"/> whose every method throws an unexpected exception — the stand-in
    /// for an unexpected dependency fault (a driver/connection error) on a normal request path. The exception
    /// message carries a distinctive internal detail to prove the response never echoes it (threat T7).
    /// </summary>
    private sealed class FaultyOrganizationRepository : IOrganizationRepository
    {
        private readonly string _failureDetail;

        public FaultyOrganizationRepository(string failureDetail) => _failureDetail = failureDetail;

        private InvalidOperationException Fault() => new(_failureDetail);

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => throw Fault();

        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) => throw Fault();

        public Task<IReadOnlyList<Organization>> ListByMemberAsync(Guid userProfileId, CancellationToken cancellationToken)
            => throw Fault();

        public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw Fault();

        public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
            => throw Fault();

        public Task<OrganizationAddResult> AddWithOwnerAsync(
            Organization organization,
            OrganizationMember owner,
            CancellationToken cancellationToken)
            => throw Fault();

        public Task DeleteAsync(Organization organization, CancellationToken cancellationToken) => throw Fault();
    }
}
