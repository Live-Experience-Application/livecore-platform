using LiveCore.Api.Exports;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for the export DOWNLOAD authorization policy (CORE-EXP-001, "Add the export read and download
/// endpoint"). They are the access-side authorization tests that make the export READ path "generic and
/// authorized" (the epic acceptance criterion), complementing the existing export PROJECTION tests
/// (<see cref="ExportManifestProjectionTests"/>, <see cref="ExportJobProjectionTests"/>) and following the shape
/// of <see cref="LiveCore.Api.UnitTests.Audit.AuditQueryPolicyTests"/>.
///
/// They pin the "Export workspace" row of docs/06_AUTHORIZATION_MATRIX.md and fail closed:
/// <list type="bullet">
///   <item>POSITIVE: Owner, Admin and Host may download a completed workspace export (the authorized set — the
///   required tests name "Host/Owner/Admin" as the downloaders).</item>
///   <item>NEGATIVE (unauthorized-role denial, fail-closed): CoHost (matrix "no"), the audience roles
///   Participant/Observer (matrix "no"), the deployment-<c>optional</c> Auditor (a metadata role, not an
///   export-artifact downloader), and any UNDEFINED role are all denied (threats T1/T5/T8).</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so every check is exact set membership, never a &gt;/&lt;
/// comparison. No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ExportAccessPolicyTests
{
    /// <summary>The roles that may DOWNLOAD an export (docs/06 "Export workspace" authorized set).</summary>
    public static TheoryData<MembershipRole> AuthorizedRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
    ];

    /// <summary>The roles denied the export download (CoHost = no; Participant/Observer = no; Auditor = optional, denied).</summary>
    public static TheoryData<MembershipRole> DeniedRoles =>
    [
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    [Theory]
    [MemberData(nameof(AuthorizedRoles))]
    public void Authorized_roles_can_download_an_export(MembershipRole role)
    {
        Assert.True(ExportAccessPolicy.CanDownloadExport(role));
    }

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void Denied_roles_cannot_download_an_export(MembershipRole role)
    {
        Assert.False(ExportAccessPolicy.CanDownloadExport(role));
    }

    [Fact]
    public void Auditor_is_denied_even_though_the_matrix_marks_it_optional()
    {
        // docs/06 marks Auditor "Export workspace" = optional. The downloadable export artifact is host/admin
        // content, not an audit-metadata grant, so Core fails closed on "optional": the Auditor reads the audit
        // log and the metadata-shaped manifest projection, but never downloads the export artifact.
        Assert.False(ExportAccessPolicy.CanDownloadExport(MembershipRole.Auditor));
    }

    [Fact]
    public void Undefined_role_cannot_download_an_export()
    {
        // Deny-by-default: an unrecognized enum value is never granted the export download (threats T1/T5/T8).
        Assert.False(ExportAccessPolicy.CanDownloadExport((MembershipRole)999));
    }

    [Fact]
    public void Exactly_owner_admin_and_host_are_authorized_across_all_roles()
    {
        // The authorized set {Owner, Admin, Host} and its complement together partition ALL seven defined roles
        // with no overlap and no gap — proving the grant is total and exact (the matrix is non-linear, so this is
        // set membership, not an ordering ladder). A silent change to the role set fails here.
        var allRoles = Enum.GetValues<MembershipRole>();

        var authorized = allRoles.Where(ExportAccessPolicy.CanDownloadExport).ToArray();
        var denied = allRoles.Where(r => !ExportAccessPolicy.CanDownloadExport(r)).ToArray();

        Assert.Equal(7, allRoles.Length);
        Assert.Equal(
            new[] { MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host }.OrderBy(r => r),
            authorized.OrderBy(r => r));
        Assert.Equal(
            new[]
            {
                MembershipRole.CoHost, MembershipRole.Participant,
                MembershipRole.Observer, MembershipRole.Auditor,
            }.OrderBy(r => r),
            denied.OrderBy(r => r));
        Assert.Empty(authorized.Intersect(denied));
    }
}
