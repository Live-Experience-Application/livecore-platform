using System.Reflection;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Unit tests for the audit query AUTHORIZATION policy and its read view (CORE-AUD-005, "Implement audit
/// query permissions"). They are the read-side authorization tests that make the audit log "generic and
/// authorized" (the epic acceptance criterion), complementing the existing audit CREATION tests
/// (<see cref="AuditLogEntryTests"/>) and following the shape of the epic's "export projection tests"
/// (<see cref="LiveCore.Api.UnitTests.Exports.ExportJobProjectionTests"/>).
///
/// They pin the "View audit log" row of docs/06_AUTHORIZATION_MATRIX.md EXACTLY and fail closed:
/// <list type="bullet">
///   <item>POSITIVE: Owner, Admin and Auditor may read the audit log (Core's secure default authorized
///   set). Auditor in particular IS allowed here — the one place the matrix grants the audit role a
///   first-class <c>yes</c>.</item>
///   <item>NEGATIVE (unauthorized-role denial, fail-closed): Host (the matrix's deployment-<c>optional</c>
///   grant, denied by Core's secure default), CoHost, Participant, Observer and any UNDEFINED role are all
///   denied — they receive no audit facts (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).</item>
///   <item>The read VIEW carries only safe identifiers/enums/state-names and never any content (threat
///   T7).</item>
/// </list>
/// <see cref="MembershipRole"/> is non-linear, so every check is exact set membership, never a &gt;/&lt;
/// comparison. No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AuditQueryPolicyTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _actorId = Guid.CreateVersion7();
    private static readonly Guid _resourceId = Guid.CreateVersion7();
    private static readonly Guid _targetParticipantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The roles that may READ the audit log (docs/06 "View audit log" = yes; Core's secure default).</summary>
    public static TheoryData<MembershipRole> AuthorizedRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Auditor,
    ];

    /// <summary>The roles denied the audit read (Host = matrix "optional", denied fail-closed; the rest = no).</summary>
    public static TheoryData<MembershipRole> DeniedRoles =>
    [
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    private static AuditLogEntry CreateVisibilityEntry()
        => AuditLogEntry.ForVisibilityRuleChange(
            _organizationId,
            _workspaceId,
            _actorId,
            "ContentBlock",
            _resourceId,
            _targetParticipantId,
            "Hidden",
            "Visible",
            _createdAt);

    private static AuditLogEntry CreateSystemOrgLevelEntry()
        => AuditLogEntry.Create(
            _organizationId,
            workspaceId: null,
            AuditAction.MemberInvited,
            actorUserProfileId: null,
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            _createdAt);

    // --- Permission: positive (Owner/Admin/Auditor may read) -------------------

    [Theory]
    [MemberData(nameof(AuthorizedRoles))]
    public void Authorized_roles_can_view_the_audit_log(MembershipRole role)
    {
        Assert.True(AuditQueryPolicy.CanViewAuditLog(role));
    }

    [Fact]
    public void Auditor_can_view_the_audit_log()
    {
        // The audit log is the ONE place docs/06 grants the audit role a first-class "yes" (it is only
        // "audit-only" on the content rows, where VisibilityRoles/AssetDownloadPolicy deny it). This is the
        // explicit allowance docs/06 calls for.
        Assert.True(AuditQueryPolicy.CanViewAuditLog(MembershipRole.Auditor));
    }

    // --- Permission: negative (everyone else is denied, fail-closed) -----------

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void Denied_roles_cannot_view_the_audit_log(MembershipRole role)
    {
        Assert.False(AuditQueryPolicy.CanViewAuditLog(role));
    }

    [Fact]
    public void Host_is_denied_by_default_even_though_the_matrix_marks_it_optional()
    {
        // docs/06 marks Host "View audit log" = optional (a deployment-configurable grant). Core fails closed
        // on "optional": the secure default denies Host. Enabling it is a deployment policy layered on top,
        // deliberately not enabled in Core.
        Assert.False(AuditQueryPolicy.CanViewAuditLog(MembershipRole.Host));
    }

    [Fact]
    public void Undefined_role_cannot_view_the_audit_log()
    {
        // Deny-by-default: an unrecognized enum value is never granted the audit read (threats T1/T5).
        Assert.False(AuditQueryPolicy.CanViewAuditLog((MembershipRole)999));
    }

    [Fact]
    public void Exactly_owner_admin_and_auditor_are_authorized_across_all_roles()
    {
        // The authorized set {Owner, Admin, Auditor} and its complement together partition ALL seven defined
        // roles with no overlap and no gap — proving the grant is total and exact (the matrix is non-linear,
        // so this is set membership, not an ordering ladder). A silent change to the role set fails here.
        var allRoles = Enum.GetValues<MembershipRole>();

        var authorized = allRoles.Where(AuditQueryPolicy.CanViewAuditLog).ToArray();
        var denied = allRoles.Where(r => !AuditQueryPolicy.CanViewAuditLog(r)).ToArray();

        Assert.Equal(7, allRoles.Length);
        Assert.Equal(
            new[] { MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Auditor }.OrderBy(r => r),
            authorized.OrderBy(r => r));
        Assert.Equal(
            new[]
            {
                MembershipRole.Host, MembershipRole.CoHost,
                MembershipRole.Participant, MembershipRole.Observer,
            }.OrderBy(r => r),
            denied.OrderBy(r => r));
        Assert.Empty(authorized.Intersect(denied));
    }

    // --- Projection: authorized roles get the views, denied roles get nothing --

    [Theory]
    [MemberData(nameof(AuthorizedRoles))]
    public void Project_returns_the_views_for_an_authorized_role(MembershipRole role)
    {
        var entries = new[] { CreateVisibilityEntry(), CreateSystemOrgLevelEntry() };

        var projected = AuditQueryPolicy.Project(entries, role);

        Assert.Equal(2, projected.Count);
        Assert.Equal(entries[0].Id, projected[0].Id);
        Assert.Equal(entries[1].Id, projected[1].Id);
    }

    [Theory]
    [MemberData(nameof(DeniedRoles))]
    public void Project_returns_empty_for_a_denied_role(MembershipRole role)
    {
        // Fail-closed defence in depth: a denied role receives no audit facts at all (not a reduced record).
        var entries = new[] { CreateVisibilityEntry(), CreateSystemOrgLevelEntry() };

        Assert.Empty(AuditQueryPolicy.Project(entries, role));
    }

    [Fact]
    public void Project_returns_empty_for_an_undefined_role()
    {
        var entries = new[] { CreateVisibilityEntry() };

        Assert.Empty(AuditQueryPolicy.Project(entries, (MembershipRole)999));
    }

    [Fact]
    public void Project_rejects_a_null_entry_sequence()
    {
        Assert.Throws<ArgumentNullException>(
            () => AuditQueryPolicy.Project(null!, MembershipRole.Owner));
    }

    // --- View: faithfully maps every recorded fact, carries no content ---------

    [Fact]
    public void View_maps_every_field_of_a_fully_populated_entry()
    {
        var entry = CreateVisibilityEntry();

        var view = AuditLogEntryView.From(entry);

        Assert.Equal(entry.Id, view.Id);
        Assert.Equal(entry.OrganizationId, view.OrganizationId);
        Assert.Equal(entry.WorkspaceId, view.WorkspaceId);
        Assert.Equal(entry.Action, view.Action);
        Assert.Equal(entry.ActorUserProfileId, view.ActorUserProfileId);
        Assert.Equal(entry.ResourceType, view.ResourceType);
        Assert.Equal(entry.ResourceId, view.ResourceId);
        Assert.Equal(entry.TargetParticipantId, view.TargetParticipantId);
        Assert.Equal(entry.PreviousState, view.PreviousState);
        Assert.Equal(entry.NewState, view.NewState);
        Assert.Equal(entry.CreatedAt, view.CreatedAt);
    }

    [Fact]
    public void View_preserves_the_nulls_of_a_system_organization_level_entry()
    {
        // A generic system, organization-level, non-transition action records nulls for the optional parts;
        // the view must surface them faithfully, never invent a workspace, actor, resource or state.
        var entry = CreateSystemOrgLevelEntry();

        var view = AuditLogEntryView.From(entry);

        Assert.Equal(AuditAction.MemberInvited, view.Action);
        Assert.Null(view.WorkspaceId);
        Assert.Null(view.ActorUserProfileId);
        Assert.Null(view.ResourceType);
        Assert.Null(view.ResourceId);
        Assert.Null(view.TargetParticipantId);
        Assert.Null(view.PreviousState);
        Assert.Null(view.NewState);
    }

    [Fact]
    public void View_property_set_is_the_complete_identifier_only_shape()
    {
        // The exact top-level property set: identifiers, the action enum, generic state NAMES and the
        // timestamp — no content field. This assertion FAILS if a free-form content/body field is ever added
        // (threat T7).
        var names = PublicPropertyNames(typeof(AuditLogEntryView));

        Assert.Equal(
            new[]
            {
                "Id", "OrganizationId", "WorkspaceId", "Action", "ActorUserProfileId",
                "ResourceType", "ResourceId", "TargetParticipantId", "PreviousState", "NewState", "CreatedAt",
            }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    [Fact]
    public void View_carries_no_content_or_body_field()
    {
        // Defence in depth against an accidental future leak: no property name hints at revealed content or a
        // free-form body (threat T7; the audit log stores only the FACT of an action, never its content).
        var names = PublicPropertyNames(typeof(AuditLogEntryView));

        foreach (var name in names)
        {
            Assert.DoesNotContain("content", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("summary", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void View_rejects_a_null_entry()
    {
        Assert.Throws<ArgumentNullException>(() => AuditLogEntryView.From(null!));
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
