// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Reflection;
using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for the role-based export-MANIFEST projection (CORE-AUD-003) — the "export role-based
/// projection" control for threat T8 ("Export leak") in docs/07_SECURITY_THREAT_MODEL.md and the epic's
/// required "export projection tests".
///
/// They prove the two REQUIRED properties of the host-vs-audience separation:
/// <list type="bullet">
///   <item>The FULL view (<see cref="ExportManifestView"/>) includes the host-only fields — the producing
///   job, the tenant/workspace boundary ids, the format version, the produced timestamp, the entire per-kind
///   inventory and its total.</item>
///   <item>The SUMMARY view (<see cref="ExportManifestSummaryView"/>) EXCLUDES every host-only field — its
///   property set is exactly {Id, Scope} and carries no producing job, no tenant/workspace boundary id, no
///   version, no timestamp and, crucially, NO per-kind inventory (which reveals the shape of the workspace's
///   host content; docs/08_API_CONTRACTS.md DTO design rules; threats T7/T8).</item>
/// </list>
/// and that <see cref="ExportManifestProjection"/> maps EACH of the seven generic <see cref="MembershipRole"/>
/// values to the correct shape by EXACT set membership (the matrix is non-linear, so this is never a
/// &gt;/&lt; comparison; docs/06_AUTHORIZATION_MATRIX.md "View workspace metadata": yes for
/// Owner/Admin/Host/CoHost/Auditor, limited for Participant/Observer), and that an UNDEFINED role fails
/// closed to the stripped summary shape.
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class ExportManifestProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _requestedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _completedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<ExportResourceKind, int> _inventory =
        new Dictionary<ExportResourceKind, int>
        {
            [ExportResourceKind.Scene] = 3,
            [ExportResourceKind.ContentBlock] = 7,
        };

    /// <summary>The host-only fields the summary view must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(ExportManifestView.ExportJobId),
        nameof(ExportManifestView.OrganizationId),
        nameof(ExportManifestView.WorkspaceId),
        nameof(ExportManifestView.ManifestVersion),
        nameof(ExportManifestView.GeneratedAt),
        nameof(ExportManifestView.Entries),
        nameof(ExportManifestView.TotalItemCount),
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

    private static ExportManifest CreateManifest()
    {
        var job = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.Workspace, _createdAt);
        job.Start(_createdAt);
        job.Complete(_completedAt);
        return ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);
    }

    // --- Full view: includes the host-only fields ------------------------------

    [Fact]
    public void Full_view_includes_all_host_only_fields_and_the_inventory()
    {
        var manifest = CreateManifest();

        var full = ExportManifestView.From(manifest);

        Assert.Equal(manifest.Id, full.Id);
        Assert.Equal(manifest.ExportJobId, full.ExportJobId);
        Assert.Equal(manifest.OrganizationId, full.OrganizationId);
        Assert.Equal(manifest.WorkspaceId, full.WorkspaceId);
        Assert.Equal(manifest.Scope, full.Scope);
        Assert.Equal(manifest.ManifestVersion, full.ManifestVersion);
        Assert.Equal(manifest.GeneratedAt, full.GeneratedAt);
        Assert.Equal(manifest.TotalItemCount, full.TotalItemCount);

        // The per-kind inventory is present and projected in ascending-kind order.
        Assert.Equal(2, full.Entries.Count);
        Assert.Equal(
            new[] { ExportResourceKind.Scene, ExportResourceKind.ContentBlock }.OrderBy(kind => kind),
            full.Entries.Select(entry => entry.Kind));
        Assert.Equal(3, full.Entries.Single(entry => entry.Kind == ExportResourceKind.Scene).ItemCount);
        Assert.Equal(7, full.Entries.Single(entry => entry.Kind == ExportResourceKind.ContentBlock).ItemCount);
    }

    [Fact]
    public void Full_view_property_set_is_the_complete_shape()
    {
        var names = PublicPropertyNames(typeof(ExportManifestView));

        Assert.Equal(
            new[]
            {
                "Id", "ExportJobId", "OrganizationId", "WorkspaceId", "Scope",
                "ManifestVersion", "GeneratedAt", "Entries", "TotalItemCount",
            }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Summary view: excludes EVERY host-only field --------------------------

    [Fact]
    public void Summary_view_keeps_only_the_audience_safe_fields()
    {
        var manifest = CreateManifest();

        var summary = ExportManifestSummaryView.From(manifest);

        Assert.Equal(manifest.Id, summary.Id);
        Assert.Equal(manifest.Scope, summary.Scope);
    }

    [Fact]
    public void Summary_view_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the summary view is {Id, Scope}. This assertion FAILS if a
        // host-only field (most importantly the per-kind inventory) is ever added to the summary view
        // (docs/08: "Participant DTOs must not contain hidden content fields").
        var names = PublicPropertyNames(typeof(ExportManifestSummaryView));

        Assert.Equal(
            new[] { "Id", "Scope" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Summary_view_carries_no_inventory_job_or_boundary_field()
    {
        // Defence in depth against an accidental future leak: no property name hints at the inventory, the
        // producing job, a timestamp or the tenant/workspace boundary (threats T7/T8; docs/08).
        var names = PublicPropertyNames(typeof(ExportManifestSummaryView));

        foreach (var name in names)
        {
            Assert.DoesNotContain("entr", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("item", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("count", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("job", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("organization", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("generated", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each of the seven roles maps to the correct shape ----------

    [Theory]
    [MemberData(nameof(FullViewRoles))]
    public void Projector_maps_host_and_metadata_roles_to_the_full_view(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = yes for Owner/Admin/Host/CoHost AND Auditor; all five receive
        // the full view (with the inventory).
        Assert.True(ExportManifestProjection.ReceivesFullView(role));

        var projected = ExportManifestProjection.Project(CreateManifest(), role);

        Assert.IsType<ExportManifestView>(projected);
    }

    [Theory]
    [MemberData(nameof(SummaryViewRoles))]
    public void Projector_maps_audience_roles_to_the_summary_view(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = limited for Participant/Observer; both receive the stripped
        // summary view (no inventory).
        Assert.False(ExportManifestProjection.ReceivesFullView(role));

        var projected = ExportManifestProjection.Project(CreateManifest(), role);

        Assert.IsType<ExportManifestSummaryView>(projected);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The full set {Owner,Admin,Host,CoHost,Auditor} and the summary set {Participant,Observer} together
        // partition ALL seven defined roles with no overlap and no gap — proving the role->shape mapping is
        // total and exact (the matrix is non-linear, so this is set membership, not an ordering ladder).
        var allRoles = Enum.GetValues<MembershipRole>();

        var fullRoles = allRoles.Where(ExportManifestProjection.ReceivesFullView).ToArray();
        var summaryRoles = allRoles.Where(r => !ExportManifestProjection.ReceivesFullView(r)).ToArray();

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
        // role gets the stripped summary shape, never the inventory).
        const MembershipRole undefinedRole = (MembershipRole)999;

        Assert.False(ExportManifestProjection.ReceivesFullView(undefinedRole));

        var projected = ExportManifestProjection.Project(CreateManifest(), undefinedRole);

        Assert.IsType<ExportManifestSummaryView>(projected);
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
