using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the current-principal endpoint (CORE-API-002,
/// <c>GET /api/v1/me</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, with a test authentication scheme and an
/// EF Core SQLite database, so the documented request flow (authentication ->
/// principal mapping -> endpoint -> projection) is exercised end-to-end.
///
/// The story's acceptance criteria and the threat model drive the cases:
/// <list type="bullet">
///   <item>401 for an anonymous caller (the "anonymous callers get 401"
///   criterion);</item>
///   <item>an authenticated user reads their profile + memberships + roles (the
///   "authenticated caller can read their principal context" criterion);</item>
///   <item>403 for a service-account principal (the unauthorized-role denial —
///   only a human user holds a profile and memberships);</item>
///   <item>a membership whose organization the token does not claim is NOT
///   exposed (tenant isolation; T5 — "do not expose other tenants");</item>
///   <item>the response carries no token/secret, only safe identifiers and the
///   caller's own metadata (T7).</item>
/// </list>
/// </summary>
public sealed class MeEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";
    private const string _orgC = "globex-inc";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- 401: anonymous -----------------------------------------------------

    [Fact]
    public async Task Me_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 403: a service account (unauthorized role) -------------------------

    [Fact]
    public async Task Me_for_a_service_account_is_403()
    {
        // /me is a user concept: a service account (issuer-asserted client_id
        // marker) holds no user profile or membership, so it is denied fail-closed,
        // exactly as the sibling /me/quota-status and /me/ad-eligibility routes do.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("svc-a", _issuer, _orgA);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- 200: the authenticated read ----------------------------------------

    [Fact]
    public async Task Me_returns_the_caller_profile_and_memberships_with_roles()
    {
        // The caller is an Owner of A and a Participant of B (persisted), and the
        // token claims both. /me returns the caller's own profile (keyed by the
        // OIDC identity pair) and exactly those two memberships, each with the
        // caller's own role.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-ab";
        Guid profileId = Guid.Empty;
        Guid orgAId = Guid.Empty;
        Guid orgBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            profileId = user.Id;
            var a = await db.AddOrganizationAsync(_orgA);
            var b = await db.AddOrganizationAsync(_orgB);
            orgAId = a.Id;
            orgBId = b.Id;
            await db.AddOrganizationMemberAsync(a.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(b.Id, user.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ReadMeAsync(response);

        // The caller's own profile (their identity returned to themselves).
        Assert.Equal(profileId, me.User.Id);
        Assert.Equal(_issuer, me.User.Issuer);
        Assert.Equal(subject, me.User.Subject);

        // Exactly the two memberships, each carrying the caller's own role.
        Assert.Equal(2, me.Memberships.Length);
        var byId = me.Memberships.ToDictionary(m => m.OrganizationId);
        Assert.Equal("Owner", byId[orgAId].Role);
        Assert.Equal(_orgA, byId[orgAId].OrganizationSlug);
        Assert.Equal("Participant", byId[orgBId].Role);
        Assert.Equal(_orgB, byId[orgBId].OrganizationSlug);
    }

    [Fact]
    public async Task Me_provisions_the_profile_on_first_sight_with_no_memberships()
    {
        // A brand-new user with no seeded profile and no membership: /me provisions
        // the profile reference (idempotent first sight) and returns it with an
        // EMPTY membership list. A token claim alone is never fabricated into a
        // membership.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "first-timer";

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ReadMeAsync(response);
        Assert.Equal(subject, me.User.Subject);
        Assert.Equal(_issuer, me.User.Issuer);
        Assert.NotEqual(Guid.Empty, me.User.Id);
        Assert.Empty(me.Memberships);

        // The profile was actually persisted (provisioned on first sight).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        Assert.True(await db.UserProfiles.AnyAsync(u => u.Issuer == _issuer && u.SubjectId == subject));
    }

    // ---- T5: do not expose other tenants ------------------------------------

    [Fact]
    public async Task Me_returns_only_memberships_the_token_claims()
    {
        // The caller is a member of A and B (persisted), but the token claims only
        // A. The principal context must expose ONLY the A membership: B is a
        // foreign tenant from the token's point of view and is never exposed
        // (threat T5 — "do not expose other tenants").
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-ab-token-a";
        Guid orgAId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var a = await db.AddOrganizationAsync(_orgA);
            var b = await db.AddOrganizationAsync(_orgB);
            orgAId = a.Id;
            await db.AddOrganizationMemberAsync(a.Id, user.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(b.Id, user.Id, MembershipRole.Admin);
        });

        // Token asserts A only.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ReadMeAsync(response);
        var only = Assert.Single(me.Memberships);
        Assert.Equal(orgAId, only.OrganizationId);
        Assert.Equal(_orgA, only.OrganizationSlug);
        Assert.Equal("Owner", only.Role);
    }

    [Fact]
    public async Task Me_is_empty_membership_for_a_claim_without_a_persisted_membership()
    {
        // The mirror of the above: the caller is a member of B only, but the token
        // claims A and C (neither of which the caller belongs to). There is no
        // organization the caller both belongs to and the token claims, so the
        // membership list is empty — a claim never leaks a membership the caller
        // does not hold, and a membership the token does not claim never leaks.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "member-b-only";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var b = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(b.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgC);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await ReadMeAsync(response);
        Assert.Empty(me.Memberships);
    }

    // ---- T7: no token/secret in the response --------------------------------

    [Fact]
    public async Task Me_response_exposes_only_safe_fields_and_no_token_or_secret()
    {
        // The response shape must carry only safe identifiers and the caller's own
        // metadata — never a token, secret or raw organization-claim field (threat
        // T7; the story note "No secrets/tokens"). This inspects the JSON property
        // NAMES structurally (not a fuzzy match on values, which a subject like
        // "no-secret" would trip), asserting an exact safe key set and that no key
        // names a credential.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "principal-user";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var a = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(a.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // The exact safe top-level and nested key sets — nothing more is exposed.
        Assert.Equal(
            new[] { "memberships", "user" },
            root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(
            new[] { "displayName", "email", "id", "issuer", "subject" },
            root.GetProperty("user").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(
            new[] { "organizationId", "organizationName", "organizationSlug", "role" },
            root.GetProperty("memberships")[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());

        // No property anywhere names a credential/token (case-insensitive).
        var forbidden = new[] { "token", "secret", "password", "bearer", "claim", "credential" };
        foreach (var name in AllPropertyNames(root))
        {
            Assert.DoesNotContain(
                forbidden,
                fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in AllPropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in AllPropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static async Task<MeDto> ReadMeAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<MeDto>(_json);
        Assert.NotNull(dto);
        Assert.NotNull(dto.User);
        Assert.NotNull(dto.Memberships);
        return dto;
    }

    private sealed record MeDto(MeUserDto User, MeMembershipDto[] Memberships);

    private sealed record MeUserDto(Guid Id, string Issuer, string Subject, string? DisplayName, string? Email);

    private sealed record MeMembershipDto(
        Guid OrganizationId,
        string OrganizationSlug,
        string OrganizationName,
        string Role);
}
