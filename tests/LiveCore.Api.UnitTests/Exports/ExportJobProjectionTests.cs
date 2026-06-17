// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Reflection;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for the role-based export-job projection (CORE-AUD-002) — the "export role-based projection"
/// control for threat T8 ("Export leak") in docs/07_SECURITY_THREAT_MODEL.md and the epic's required
/// "export projection tests".
///
/// They prove the two REQUIRED properties of the host-vs-audience separation:
/// <list type="bullet">
///   <item>The FULL view (<see cref="ExportJobView"/>) includes the host-only fields (the tenant/workspace
///   boundary ids, the requester, the failure reason and the server timestamps).</item>
///   <item>The SUMMARY view (<see cref="ExportJobSummaryView"/>) EXCLUDES every host-only field — its
///   property set is exactly {Id, Scope, Status} and carries no tenant/workspace boundary id, no
///   requester, no failure reason and no timestamp (docs/08_API_CONTRACTS.md DTO design rules; threats
///   T7/T8).</item>
/// </list>
/// and that <see cref="ExportJobProjection"/> maps EACH of the seven generic <see cref="MembershipRole"/>
/// values to the correct shape by EXACT set membership (the matrix is non-linear, so this is never a
/// &gt;/&lt; comparison; docs/06_AUTHORIZATION_MATRIX.md "View workspace metadata": yes for
/// Owner/Admin/Host/CoHost/Auditor, limited for Participant/Observer), and that an UNDEFINED role fails
/// closed to the stripped summary shape.
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class ExportJobProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _requestedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The host-only fields the summary view must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(ExportJobView.OrganizationId),
        nameof(ExportJobView.WorkspaceId),
        nameof(ExportJobView.RequestedByUserProfileId),
        nameof(ExportJobView.FailureReason),
        nameof(ExportJobView.CreatedAt),
        nameof(ExportJobView.UpdatedAt),
    ];

    /// <summary>The roles that receive the FULL view (docs/06 "View workspace metadata" = yes).</summary>
    public static TheoryData<MembershipRole> FullViewRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Auditor,
    ];

    /// <summary>The audience roles that receive the STRIPPED summary view (docs/06 "View workspace metadata" = limited).</summary>
    public static TheoryData<MembershipRole> SummaryViewRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    private static ExportJob CreateFailedJob()
    {
        var job = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.Workspace, _createdAt);
        job.Fail("an internal diagnostic", _createdAt);
        return job;
    }

    // --- Full view: includes the host-only fields ------------------------------

    [Fact]
    public void Full_view_includes_all_host_only_fields()
    {
        var job = CreateFailedJob();

        var full = ExportJobView.From(job);

        Assert.Equal(job.Id, full.Id);
        Assert.Equal(job.OrganizationId, full.OrganizationId);
        Assert.Equal(job.WorkspaceId, full.WorkspaceId);
        Assert.Equal(job.RequestedByUserProfileId, full.RequestedByUserProfileId);
        Assert.Equal(job.Scope, full.Scope);
        Assert.Equal(job.Status, full.Status);
        Assert.Equal(job.FailureReason, full.FailureReason);
        Assert.Equal(job.CreatedAt, full.CreatedAt);
        Assert.Equal(job.UpdatedAt, full.UpdatedAt);
    }

    [Fact]
    public void Full_view_property_set_is_the_complete_shape()
    {
        var names = PublicPropertyNames(typeof(ExportJobView));

        Assert.Equal(
            new[]
            {
                "Id", "OrganizationId", "WorkspaceId", "RequestedByUserProfileId",
                "Scope", "Status", "FailureReason", "CreatedAt", "UpdatedAt",
            }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Summary view: excludes EVERY host-only field --------------------------

    [Fact]
    public void Summary_view_keeps_only_the_audience_safe_fields()
    {
        var job = CreateFailedJob();

        var summary = ExportJobSummaryView.From(job);

        Assert.Equal(job.Id, summary.Id);
        Assert.Equal(job.Scope, summary.Scope);
        Assert.Equal(job.Status, summary.Status);
    }

    [Fact]
    public void Summary_view_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the summary view is {Id, Scope, Status}. This assertion FAILS
        // if a host-only field is ever added to the summary view (docs/08: "Participant DTOs must not
        // contain hidden content fields").
        var names = PublicPropertyNames(typeof(ExportJobSummaryView));

        Assert.Equal(
            new[] { "Id", "Scope", "Status" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Summary_view_carries_no_requester_reason_or_rationale_field()
    {
        // Defence in depth against an accidental future leak: no property name hints at the requester, a
        // failure reason or authorization rationale (threats T7/T8; docs/08).
        var names = PublicPropertyNames(typeof(ExportJobSummaryView));

        foreach (var name in names)
        {
            Assert.DoesNotContain("requested", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reason", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rationale", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("organization", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each of the seven roles maps to the correct shape ----------

    [Theory]
    [MemberData(nameof(FullViewRoles))]
    public void Projector_maps_host_and_metadata_roles_to_the_full_view(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = yes for Owner/Admin/Host/CoHost AND Auditor; all five receive
        // the full view.
        Assert.True(ExportJobProjection.ReceivesFullView(role));

        var projected = ExportJobProjection.Project(new[] { CreateFailedJob() }, role);

        var fullArray = Assert.IsType<ExportJobView[]>(projected);
        Assert.Single(fullArray);
    }

    [Theory]
    [MemberData(nameof(SummaryViewRoles))]
    public void Projector_maps_audience_roles_to_the_summary_view(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = limited for Participant/Observer; both receive the stripped
        // summary view.
        Assert.False(ExportJobProjection.ReceivesFullView(role));

        var projected = ExportJobProjection.Project(new[] { CreateFailedJob() }, role);

        var summaryArray = Assert.IsType<ExportJobSummaryView[]>(projected);
        Assert.Single(summaryArray);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The full set {Owner,Admin,Host,CoHost,Auditor} and the summary set {Participant,Observer} together
        // partition ALL seven defined roles with no overlap and no gap — proving the role->shape mapping is
        // total and exact (the matrix is non-linear, so this is set membership, not an ordering ladder).
        var allRoles = Enum.GetValues<MembershipRole>();

        var fullRoles = allRoles.Where(ExportJobProjection.ReceivesFullView).ToArray();
        var summaryRoles = allRoles.Where(r => !ExportJobProjection.ReceivesFullView(r)).ToArray();

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
        // An undefined enum value is never granted the broader full view: deny-by-default (an unrecognized
        // role gets the stripped summary shape).
        const MembershipRole undefinedRole = (MembershipRole)999;

        Assert.False(ExportJobProjection.ReceivesFullView(undefinedRole));

        var projected = ExportJobProjection.Project(new[] { CreateFailedJob() }, undefinedRole);

        Assert.IsType<ExportJobSummaryView[]>(projected);
    }

    [Fact]
    public void Projector_preserves_the_full_job_set_for_both_shapes()
    {
        // The SET of jobs is unchanged by the projection — only the per-job SHAPE differs by role.
        var jobs = new[] { CreateFailedJob(), CreateFailedJob(), CreateFailedJob() };

        var full = Assert.IsType<ExportJobView[]>(ExportJobProjection.Project(jobs, MembershipRole.Host));
        var summary = Assert.IsType<ExportJobSummaryView[]>(
            ExportJobProjection.Project(jobs, MembershipRole.Participant));

        Assert.Equal(3, full.Length);
        Assert.Equal(3, summary.Length);
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
