using System.Reflection;
using LiveCore.Api.Organizations;
using LiveCore.Api.Recaps;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Unit tests for the role-based RECAP projection (CORE-AUD-004) — the host-vs-audience separation that keeps
/// the host-only recap body away from the audience (threat T2 visibility leak in
/// docs/07_SECURITY_THREAT_MODEL.md; docs/09_EVENT_CATALOG.md <c>RecapGenerated</c>: "Participant-visible only
/// after separate reveal"), the recap analogue of the epic's required "export projection tests".
///
/// They prove the two REQUIRED properties of the host-vs-audience separation:
/// <list type="bullet">
///   <item>The FULL view (<see cref="RecapView"/>) includes the host-only fields — the tenant/workspace/session
///   boundary ids, the producer, the version, the produced timestamp and, crucially, the recap
///   <see cref="RecapView.Summary"/> BODY.</item>
///   <item>The SUMMARY view (<see cref="RecapSummaryView"/>) EXCLUDES every host-only field — its property set
///   is exactly {Id, SessionId} and carries NO recap body, no producer, no tenant/workspace boundary id, no
///   version and no timestamp (docs/08_API_CONTRACTS.md DTO design rules; threats T2/T7).</item>
/// </list>
/// and that <see cref="RecapProjection"/> maps EACH of the seven generic <see cref="MembershipRole"/> values to
/// the correct shape by EXACT set membership (the matrix is non-linear, so this is never a &gt;/&lt;
/// comparison; docs/06_AUTHORIZATION_MATRIX.md host-content / "View workspace metadata" roles:
/// Owner/Admin/Host/CoHost/Auditor get the full view with the body, Participant/Observer get the stripped
/// summary), and that an UNDEFINED role fails closed to the stripped summary shape.
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class RecapProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _sessionId = Guid.CreateVersion7();
    private static readonly Guid _generatedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private const string _summary = "Host-only recap body: three scenes were prepared and one was revealed.";

    /// <summary>The host-only fields the summary view must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(RecapView.OrganizationId),
        nameof(RecapView.WorkspaceId),
        nameof(RecapView.GeneratedByUserProfileId),
        nameof(RecapView.RecapVersion),
        nameof(RecapView.Summary),
        nameof(RecapView.GeneratedAt),
    ];

    /// <summary>The roles that receive the FULL view (docs/06 host-content / "View workspace metadata" = yes).</summary>
    public static TheoryData<MembershipRole> FullViewRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Auditor,
    ];

    /// <summary>The audience roles that receive the STRIPPED summary view.</summary>
    public static TheoryData<MembershipRole> SummaryViewRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    private static Recap CreateRecap()
        => Recap.GenerateByHost(_organizationId, _workspaceId, _sessionId, _generatedBy, _summary, _generatedAt);

    // --- Full view: includes the host-only fields and the body -----------------

    [Fact]
    public void Full_view_includes_all_host_only_fields_and_the_body()
    {
        var recap = CreateRecap();

        var full = RecapView.From(recap);

        Assert.Equal(recap.Id, full.Id);
        Assert.Equal(recap.OrganizationId, full.OrganizationId);
        Assert.Equal(recap.WorkspaceId, full.WorkspaceId);
        Assert.Equal(recap.SessionId, full.SessionId);
        Assert.Equal(recap.GeneratedByUserProfileId, full.GeneratedByUserProfileId);
        Assert.Equal(recap.RecapVersion, full.RecapVersion);
        Assert.Equal(recap.GeneratedAt, full.GeneratedAt);
        // The host-only recap body is present in the full view only.
        Assert.Equal(_summary, full.Summary);
    }

    [Fact]
    public void Full_view_property_set_is_the_complete_shape()
    {
        var names = PublicPropertyNames(typeof(RecapView));

        Assert.Equal(
            new[]
            {
                "Id", "OrganizationId", "WorkspaceId", "SessionId", "GeneratedByUserProfileId",
                "RecapVersion", "Summary", "GeneratedAt",
            }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Summary view: excludes EVERY host-only field (especially the body) ----

    [Fact]
    public void Summary_view_keeps_only_the_audience_safe_fields()
    {
        var recap = CreateRecap();

        var summary = RecapSummaryView.From(recap);

        Assert.Equal(recap.Id, summary.Id);
        Assert.Equal(recap.SessionId, summary.SessionId);
    }

    [Fact]
    public void Summary_view_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the summary view is {Id, SessionId}. This assertion FAILS if a
        // host-only field — most importantly the recap Summary body — is ever added to the summary view
        // (docs/08: "Participant DTOs must not contain hidden content fields"; threat T2).
        var names = PublicPropertyNames(typeof(RecapSummaryView));

        Assert.Equal(
            new[] { "Id", "SessionId" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Summary_view_carries_no_body_producer_version_or_boundary_field()
    {
        // Defence in depth against an accidental future leak: no property name hints at the body, the producer,
        // a version, a timestamp or the tenant/workspace boundary (threats T2/T7; docs/08).
        var names = PublicPropertyNames(typeof(RecapSummaryView));

        foreach (var name in names)
        {
            Assert.DoesNotContain("summary", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("generated", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("version", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("organization", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each of the seven roles maps to the correct shape ----------

    [Theory]
    [MemberData(nameof(FullViewRoles))]
    public void Projector_maps_host_and_metadata_roles_to_the_full_view(MembershipRole role)
    {
        // docs/06 host-content roles Owner/Admin/Host/CoHost AND the audit-only Auditor receive the full view
        // (with the recap body).
        Assert.True(RecapProjection.ReceivesFullView(role));

        var projected = RecapProjection.Project(CreateRecap(), role);

        Assert.IsType<RecapView>(projected);
    }

    [Theory]
    [MemberData(nameof(SummaryViewRoles))]
    public void Projector_maps_audience_roles_to_the_summary_view(MembershipRole role)
    {
        // Participant/Observer receive the stripped summary view (no recap body) — the body is host-only until
        // a separate reveal (docs/09_EVENT_CATALOG.md; threat T2).
        Assert.False(RecapProjection.ReceivesFullView(role));

        var projected = RecapProjection.Project(CreateRecap(), role);

        Assert.IsType<RecapSummaryView>(projected);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The full set {Owner,Admin,Host,CoHost,Auditor} and the summary set {Participant,Observer} together
        // partition ALL seven defined roles with no overlap and no gap — proving the role->shape mapping is
        // total and exact (the matrix is non-linear, so this is set membership, not an ordering ladder).
        var allRoles = Enum.GetValues<MembershipRole>();

        var fullRoles = allRoles.Where(RecapProjection.ReceivesFullView).ToArray();
        var summaryRoles = allRoles.Where(r => !RecapProjection.ReceivesFullView(r)).ToArray();

        Assert.Equal(7, allRoles.Length);
        Assert.Equal(
            new[]
            {
                MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host,
                MembershipRole.CoHost, MembershipRole.Auditor,
            }.OrderBy(r => r),
            fullRoles.OrderBy(r => r));
        Assert.Equal(
            new[] { MembershipRole.Participant, MembershipRole.Observer }.OrderBy(r => r),
            summaryRoles.OrderBy(r => r));
    }

    [Fact]
    public void Projector_fails_closed_to_the_summary_view_for_an_undefined_role()
    {
        // An undefined enum value is never granted the broader full view: deny-by-default (an unrecognized role
        // gets the stripped summary shape, never the host-only body).
        const MembershipRole undefinedRole = (MembershipRole)999;

        Assert.False(RecapProjection.ReceivesFullView(undefinedRole));

        var projected = RecapProjection.Project(CreateRecap(), undefinedRole);

        Assert.IsType<RecapSummaryView>(projected);
    }

    private static string[] PublicPropertyNames(Type type)
        => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // Exclude the compiler-generated record EqualityContract property.
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
