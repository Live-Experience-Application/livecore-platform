using System.Reflection;
using LiveCore.Api.Organizations;
using LiveCore.Api.Scenes;

namespace LiveCore.Api.UnitTests.Scenes;

/// <summary>
/// Unit tests for the host-vs-participant scene DTO separation and its role-based
/// projector (CORE-SCENE-004, the "Projection by role" obligation on
/// <c>GET /api/v1/workspaces/{workspaceId}/scenes</c>, csv/api_routes.csv line 15).
///
/// They prove the two REQUIRED properties of the separation:
/// <list type="bullet">
///   <item>The HOST projection (<see cref="SceneResponse"/>) includes the host-only
///   fields (the tenant/workspace boundary ids and the host preparation timestamps).</item>
///   <item>The PARTICIPANT projection (<see cref="ParticipantSceneResponse"/>) EXCLUDES
///   every host-only field — its property set is exactly {Id, Title, Order} and carries
///   no tenant/workspace boundary id, no preparation timestamp and no authorization
///   rationale (docs/08_API_CONTRACTS.md DTO design rules; threats T2/T7 in
///   docs/07_SECURITY_THREAT_MODEL.md).</item>
/// </list>
/// and that <see cref="SceneProjection"/> maps EACH of the seven generic
/// <see cref="MembershipRole"/> values to the correct shape by EXACT set membership
/// (the matrix is non-linear, so this is never a &gt;/&lt; comparison;
/// docs/06_AUTHORIZATION_MATRIX.md "View workspace metadata": yes for
/// Owner/Admin/Host/CoHost/Auditor, limited for Participant/Observer).
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class SceneProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The host-only fields the participant DTO must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(SceneResponse.OrganizationId),
        nameof(SceneResponse.WorkspaceId),
        nameof(SceneResponse.CreatedAt),
        nameof(SceneResponse.UpdatedAt),
    ];

    /// <summary>The roles that receive the FULL host/metadata shape (docs/06 "View workspace metadata" = yes).</summary>
    public static TheoryData<MembershipRole> HostShapeRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
        MembershipRole.Auditor,
    ];

    /// <summary>The audience roles that receive the STRIPPED participant shape (docs/06 "View workspace metadata" = limited).</summary>
    public static TheoryData<MembershipRole> ParticipantShapeRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
    ];

    private static Scene CreateScene(int order = 0)
        => Scene.Create(_organizationId, _workspaceId, "Opening Segment", order, _createdAt);

    // --- Host DTO: includes the host-only fields --------------------------------

    [Fact]
    public void Host_projection_includes_all_host_only_fields()
    {
        var scene = CreateScene(order: 3);

        var host = SceneResponse.From(scene);

        // The host shape carries the full host-prepared metadata, including the
        // tenant/workspace boundary ids and the host preparation timestamps.
        Assert.Equal(scene.Id, host.Id);
        Assert.Equal(scene.OrganizationId, host.OrganizationId);
        Assert.Equal(scene.WorkspaceId, host.WorkspaceId);
        Assert.Equal(scene.Title, host.Title);
        Assert.Equal(scene.Order, host.Order);
        Assert.Equal(scene.CreatedAt, host.CreatedAt);
        Assert.Equal(scene.UpdatedAt, host.UpdatedAt);
    }

    [Fact]
    public void Host_dto_property_set_is_the_full_host_shape()
    {
        var names = PublicPropertyNames(typeof(SceneResponse));

        Assert.Equal(
            new[] { "Id", "OrganizationId", "WorkspaceId", "Title", "Order", "CreatedAt", "UpdatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Participant DTO: excludes EVERY host-only field ------------------------

    [Fact]
    public void Participant_projection_keeps_only_the_audience_safe_fields()
    {
        var scene = CreateScene(order: 5);

        var participant = ParticipantSceneResponse.From(scene);

        // The stripped shape keeps only the minimal audience-relevant identity +
        // sequence.
        Assert.Equal(scene.Id, participant.Id);
        Assert.Equal(scene.Title, participant.Title);
        Assert.Equal(scene.Order, participant.Order);
    }

    [Fact]
    public void Participant_dto_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the participant DTO is {Id, Title, Order}.
        // This is the assertion that FAILS if a host-only field is ever added to the
        // participant DTO (docs/08: "Participant DTOs must not contain hidden content
        // fields"; "Never include internal authorization rationale").
        var names = PublicPropertyNames(typeof(ParticipantSceneResponse));

        Assert.Equal(
            new[] { "Id", "Title", "Order" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        // None of the host-only fields (tenant/workspace boundary ids, host
        // preparation timestamps) appears on the participant DTO.
        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Participant_dto_carries_no_rationale_or_visibility_field()
    {
        // Defence in depth against an accidental future leak: no property name hints at
        // authorization rationale or visibility state (threat T7; docs/08 "Never include
        // internal authorization rationale in participant responses").
        var names = PublicPropertyNames(typeof(ParticipantSceneResponse));

        foreach (var name in names)
        {
            Assert.DoesNotContain("reason", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rationale", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("visib", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hidden", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each of the seven roles maps to the correct shape -----------

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public void Projector_maps_host_and_metadata_roles_to_the_host_shape(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = yes for Owner/Admin/Host/CoHost AND
        // Auditor; all five receive the full host shape.
        Assert.True(SceneProjection.ReceivesHostShape(role));

        var projected = SceneProjection.Project(new[] { CreateScene() }, role);

        var hostArray = Assert.IsType<SceneResponse[]>(projected);
        Assert.Single(hostArray);
    }

    [Theory]
    [MemberData(nameof(ParticipantShapeRoles))]
    public void Projector_maps_audience_roles_to_the_participant_shape(MembershipRole role)
    {
        // docs/06 "View workspace metadata" = limited for Participant/Observer; both
        // receive the stripped participant shape.
        Assert.False(SceneProjection.ReceivesHostShape(role));

        var projected = SceneProjection.Project(new[] { CreateScene() }, role);

        var participantArray = Assert.IsType<ParticipantSceneResponse[]>(projected);
        Assert.Single(participantArray);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The host set {Owner,Admin,Host,CoHost,Auditor} and the participant set
        // {Participant,Observer} together partition ALL seven defined roles with no
        // overlap and no gap — proving the role->shape mapping is total and exact (the
        // matrix is non-linear, so this is set membership, not an ordering ladder).
        var allRoles = Enum.GetValues<MembershipRole>();

        var hostRoles = allRoles.Where(SceneProjection.ReceivesHostShape).ToArray();
        var participantRoles = allRoles.Where(r => !SceneProjection.ReceivesHostShape(r)).ToArray();

        Assert.Equal(7, allRoles.Length);
        Assert.Equal(
            new[]
            {
                MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host,
                MembershipRole.CoHost, MembershipRole.Auditor,
            }.OrderBy(r => r),
            hostRoles.OrderBy(r => r));
        Assert.Equal(
            new[] { MembershipRole.Participant, MembershipRole.Observer }.OrderBy(r => r),
            participantRoles.OrderBy(r => r));
    }

    [Fact]
    public void Projector_fails_closed_to_the_participant_shape_for_an_undefined_role()
    {
        // An undefined enum value is never granted the broader host metadata shape:
        // deny-by-default (an unrecognized role gets the stripped audience shape).
        const MembershipRole undefinedRole = (MembershipRole)999;

        Assert.False(SceneProjection.ReceivesHostShape(undefinedRole));

        var projected = SceneProjection.Project(new[] { CreateScene() }, undefinedRole);

        Assert.IsType<ParticipantSceneResponse[]>(projected);
    }

    [Fact]
    public void Projector_preserves_the_full_scene_set_and_order_for_both_shapes()
    {
        // The SET of scenes is unchanged by the projection — only the per-scene SHAPE
        // differs by role. A participant receives ALL of the workspace's scenes, in
        // order; deciding WHICH scenes are visible is the Visibility module's later
        // concern, not this projector.
        var scenes = new[] { CreateScene(0), CreateScene(1), CreateScene(2) };

        var host = Assert.IsType<SceneResponse[]>(SceneProjection.Project(scenes, MembershipRole.Host));
        var participant = Assert.IsType<ParticipantSceneResponse[]>(
            SceneProjection.Project(scenes, MembershipRole.Participant));

        Assert.Equal(3, host.Length);
        Assert.Equal(3, participant.Length);
        Assert.Equal(new[] { 0, 1, 2 }, host.Select(s => s.Order).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, participant.Select(s => s.Order).ToArray());
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
