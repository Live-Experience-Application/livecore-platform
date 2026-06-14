using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the view-audit-log read endpoint (CORE-SEC-002, the "Security Hardening" epic,
/// <c>GET /api/v1/audit-logs</c>, csv/api_routes.csv roles Owner,Admin,Auditor). They drive the real
/// application over real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core
/// SQLite, foreign keys ON), so the documented request flow (authentication → tenant context resolver →
/// endpoint → authorization → tenant-scoped paged read → safe projection) is exercised end-to-end.
///
/// Coverage, per the story's required tests and the mandatory NEGATIVE authorization cases
/// (docs/06_AUTHORIZATION_MATRIX.md "View audit log"; threats T1/T5/T7):
/// <list type="bullet">
///   <item>POSITIVE: an Auditor (and Owner/Admin) reads their tenant's audit log; the result is tenant-scoped
///   (never another tenant's records) and paged (limit/offset/hasMore, with the server clamping the limit).</item>
///   <item>AUTHORIZATION (fail-closed): 401 unauthenticated; a non-privileged but known member
///   {Host, CoHost, Participant, Observer} is 403 (Host's matrix-optional grant fails closed); a service
///   account, a non-member, a foreign tenant and a token-claim mismatch are ALL hidden as 404; a missing
///   organizationSlug is 400; a malformed limit/offset is 400 — but only AFTER authorization, so an
///   unauthorized caller never receives request-shape feedback.</item>
///   <item>PII/SECRET-FREE projection (threat T7): the response carries only identifiers, the action by its
///   stable name and generic state names — never a display name, email or token.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class AuditLogEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset _t0 = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<MembershipRole> AuthorizedRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Auditor,
    ];

    // Known tenant members that are NOT granted "View audit log". Host is the matrix's deployment-optional
    // grant and is DENIED by Core's fail-closed default, so it belongs here too.
    public static TheoryData<MembershipRole> UnauthorizedKnownRoles =>
    [
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Read_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: authorized roles read the tenant's audit log ------------------

    [Theory]
    [MemberData(nameof(AuthorizedRoles))]
    public async Task Read_returns_the_tenants_audit_log_for_an_authorized_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        Guid expectedWorkspaceId = Guid.CreateVersion7();
        Guid expectedActorId = Guid.CreateVersion7();
        Guid firstEntryId = Guid.Empty;
        Guid secondEntryId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);

            var first = await db.AddAuditLogEntryAsync(
                org.Id, AuditAction.SessionStarted, expectedWorkspaceId, expectedActorId, _t0);
            var second = await db.AddAuditLogEntryAsync(
                org.Id, AuditAction.SessionEnded, expectedWorkspaceId, expectedActorId, _t0.AddSeconds(1));
            firstEntryId = first.Id;
            secondEntryId = second.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(body);
        Assert.False(body.HasMore);
        Assert.Equal(0, body.Offset);
        Assert.Equal(AuditLogPageResponse.DefaultLimit, body.Limit);

        // Chronological (append) order, both entries, tenant-scoped.
        Assert.Collection(
            body.Entries,
            e =>
            {
                Assert.Equal(firstEntryId, e.Id);
                Assert.Equal(nameof(AuditAction.SessionStarted), e.Action);
                Assert.Equal(expectedWorkspaceId, e.WorkspaceId);
                Assert.Equal(expectedActorId, e.ActorUserProfileId);
            },
            e =>
            {
                Assert.Equal(secondEntryId, e.Id);
                Assert.Equal(nameof(AuditAction.SessionEnded), e.Action);
            });
    }

    [Fact]
    public async Task Read_of_an_empty_audit_log_is_200_with_no_entries()
    {
        // A tenant the caller IS entitled to, but with no audit records yet, returns an empty page (not a 404
        // and not a leak): foreign-tenant is hidden, but an authorized empty tenant is simply empty.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(body);
        Assert.Empty(body.Entries);
        Assert.False(body.HasMore);
    }

    // ---- 403: known member without the View-audit-log grant ------------------

    [Theory]
    [MemberData(nameof(UnauthorizedKnownRoles))]
    public async Task Read_is_403_for_a_known_member_without_the_grant(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            await db.AddAuditLogEntryAsync(org.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Read_for_a_service_account_is_404()
    {
        // A service account holds no organization membership, so the tenant resolver denies it (NotAUser),
        // hidden as 404 (never distinguishable from a missing tenant).
        await using var factory = new WorkspaceApiFactory();
        await factory.SeedAsync(async db =>
        {
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddAuditLogEntryAsync(org.Id);
        });

        using var client = factory.CreateClientFor("svc-a", _issuer, _orgA);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ClientIdHeader, "automation-client");
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 404 hidden: foreign tenant / non-member / claim mismatch (T1/T5) ----

    [Fact]
    public async Task Read_is_404_when_the_caller_is_not_a_member_of_the_target_tenant()
    {
        // The token claims org A, but the caller has NO membership in org A. Resolution denies (no
        // membership), hidden as 404 — the caller cannot even see the tenant, let alone its audit log.
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            await db.AddAuditLogEntryAsync(org.Id);
        });

        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
        });

        // The token claims a tenant that does not exist; resolution denies, hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, "ghost-tenant");
        var response = await client.GetAsync("/api/v1/audit-logs?organizationSlug=ghost-tenant");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_token_claim_does_not_match_the_requested_tenant()
    {
        // T5: the caller is an Auditor of org A and names org A in the query, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
            await db.AddAuditLogEntryAsync(org.Id);
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(subject, _issuer, _orgB);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_never_returns_another_tenants_records()
    {
        // T5 (tenant isolation): the caller is an Auditor of BOTH tenants (distinct memberships). Reading org A
        // returns only org A's entries; reading org B returns only org B's — one tenant's audit records are
        // never returned through the other's slug.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-both";
        Guid entryInAId = Guid.Empty;
        Guid entryInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, user.Id, MembershipRole.Auditor);
            await db.AddOrganizationMemberAsync(orgB.Id, user.Id, MembershipRole.Auditor);
            entryInAId = (await db.AddAuditLogEntryAsync(orgA.Id, AuditAction.SessionStarted)).Id;
            entryInBId = (await db.AddAuditLogEntryAsync(orgB.Id, AuditAction.SessionEnded)).Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA, _orgB);

        var aResponse = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        var aBody = await aResponse.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(aBody);
        var aEntry = Assert.Single(aBody.Entries);
        Assert.Equal(entryInAId, aEntry.Id);
        Assert.DoesNotContain(aBody.Entries, e => e.Id == entryInBId);

        var bResponse = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgB}");
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);
        var bBody = await bResponse.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(bBody);
        var bEntry = Assert.Single(bBody.Entries);
        Assert.Equal(entryInBId, bEntry.Id);
        Assert.DoesNotContain(bBody.Entries, e => e.Id == entryInAId);
    }

    // ---- 400: request shape (only after authorization) ----------------------

    [Fact]
    public async Task Read_without_the_organization_slug_is_400()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync("/api/v1/audit-logs");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-3")]
    [InlineData("limit=abc")]
    [InlineData("offset=-1")]
    [InlineData("offset=notanumber")]
    public async Task Read_with_a_malformed_paging_parameter_is_400(string pagingQuery)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}&{pagingQuery}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_limit_is_not_leaked_to_an_unauthorized_caller()
    {
        // Request-shape validation runs AFTER authorization: a known member without the grant gets 403, and a
        // foreign tenant gets 404, even though the limit is malformed (the 400 would only ever reach an
        // authorized caller).
        await using var factory = new WorkspaceApiFactory();
        const string nonPrivileged = "participant-a";
        const string stranger = "stranger";
        await factory.SeedAsync(async db =>
        {
            var member = await db.AddUserAsync(_issuer, nonPrivileged);
            await db.AddUserAsync(_issuer, stranger);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, member.Id, MembershipRole.Participant);
        });

        using (var memberClient = factory.CreateClientFor(nonPrivileged, _issuer, _orgA))
        {
            var memberResponse = await memberClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}&limit=0");
            Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
        }

        using (var strangerClient = factory.CreateClientFor(stranger, _issuer, _orgA))
        {
            var strangerResponse = await strangerClient.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}&limit=0");
            Assert.Equal(HttpStatusCode.NotFound, strangerResponse.StatusCode);
        }
    }

    // ---- paging: limit/offset/hasMore + clamping ----------------------------

    [Fact]
    public async Task Read_pages_through_the_log_with_limit_and_offset()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        var entryIds = new List<Guid>();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
            for (var i = 0; i < 5; i++)
            {
                var entry = await db.AddAuditLogEntryAsync(
                    org.Id, AuditAction.SessionStarted, createdAt: _t0.AddSeconds(i));
                entryIds.Add(entry.Id);
            }
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // First page of two: entries 0,1, more remain.
        var firstPage = await client.GetFromJsonAsync<AuditPageDto>(
            $"/api/v1/audit-logs?organizationSlug={_orgA}&limit=2&offset=0", _json);
        Assert.NotNull(firstPage);
        Assert.Equal(2, firstPage.Limit);
        Assert.Equal(0, firstPage.Offset);
        Assert.True(firstPage.HasMore);
        Assert.Collection(
            firstPage.Entries,
            e => Assert.Equal(entryIds[0], e.Id),
            e => Assert.Equal(entryIds[1], e.Id));

        // Middle page of two: entries 2,3, more remain.
        var secondPage = await client.GetFromJsonAsync<AuditPageDto>(
            $"/api/v1/audit-logs?organizationSlug={_orgA}&limit=2&offset=2", _json);
        Assert.NotNull(secondPage);
        Assert.True(secondPage.HasMore);
        Assert.Collection(
            secondPage.Entries,
            e => Assert.Equal(entryIds[2], e.Id),
            e => Assert.Equal(entryIds[3], e.Id));

        // Last page: the single remaining entry 4, no more.
        var lastPage = await client.GetFromJsonAsync<AuditPageDto>(
            $"/api/v1/audit-logs?organizationSlug={_orgA}&limit=2&offset=4", _json);
        Assert.NotNull(lastPage);
        Assert.False(lastPage.HasMore);
        var only = Assert.Single(lastPage.Entries);
        Assert.Equal(entryIds[4], only.Id);
    }

    [Fact]
    public async Task Read_clamps_an_oversized_limit_to_the_maximum()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
            await db.AddAuditLogEntryAsync(org.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/audit-logs?organizationSlug={_orgA}&limit={AuditLogPageResponse.MaxLimit + 1000}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditPageDto>(_json);
        Assert.NotNull(body);
        // The effective limit is clamped to the server maximum, never the requested oversized value.
        Assert.Equal(AuditLogPageResponse.MaxLimit, body.Limit);
    }

    // ---- T7: PII / secret-free projection -----------------------------------

    [Fact]
    public async Task The_projection_is_pii_and_secret_free()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "auditor-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Auditor);
            await db.AddAuditLogEntryAsync(org.Id, AuditAction.SessionStarted);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/audit-logs?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        // The action surfaces by its stable NAME (not a number), and only the safe, generic fields appear.
        Assert.Contains(nameof(AuditAction.SessionStarted), raw, StringComparison.Ordinal);

        // None of the PII/secret-bearing fields the audit log never stores may appear in the projection.
        foreach (var forbidden in new[] { "email", "displayName", "token", "secret", "password", _subjectClaim })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The OIDC subject value seeded for the auditor; used to prove the response carries no identity payload.
    private const string _subjectClaim = "auditor-a";

    private sealed record AuditPageDto(
        Guid OrganizationId,
        int Offset,
        int Limit,
        bool HasMore,
        IReadOnlyList<AuditEntryDto> Entries);

    private sealed record AuditEntryDto(
        Guid Id,
        Guid OrganizationId,
        Guid? WorkspaceId,
        string Action,
        Guid? ActorUserProfileId,
        string? ResourceType,
        Guid? ResourceId,
        Guid? TargetParticipantId,
        string? PreviousState,
        string? NewState,
        DateTimeOffset CreatedAt);
}
