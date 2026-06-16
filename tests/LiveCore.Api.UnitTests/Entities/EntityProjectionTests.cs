using System.Reflection;
using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for the host-vs-participant ENTITY DTO separation and its role-based projector (CORE-ENT-006,
/// the "Projection by role" obligation on the entity list and by-id read routes).
///
/// They prove the two REQUIRED properties of the separation and its ONE deliberate distinction from the scene
/// projection:
/// <list type="bullet">
///   <item>The HOST projection (<see cref="EntityResponse"/>) includes the host-only fields — the
///   tenant/workspace boundary ids, the type linkage, the attribute-values CONTENT and the host preparation
///   timestamps.</item>
///   <item>The PARTICIPANT projection (<see cref="ParticipantEntityResponse"/>) EXCLUDES every host-only
///   field — its property set is exactly {Id, Name} and carries no attribute-values content, no
///   tenant/workspace/type id, no preparation timestamp and no authorization rationale
///   (docs/08_API_CONTRACTS.md; threats T2/T7).</item>
///   <item>An entity IS content, so the projector maps roles by the "View host-only content" row of docs/06
///   (Owner/Admin/Host/CoHost), NOT the scene-style "View workspace metadata" row — so Auditor receives the
///   STRIPPED shape here (the deliberate distinction from <c>SceneProjection</c>), and the role classification
///   delegates to the central <c>VisibilityRoles.ViewsHostOnlyContent</c> single source.</item>
/// </list>
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class EntityProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _entityTypeId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The host-only fields the participant DTO must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(EntityResponse.OrganizationId),
        nameof(EntityResponse.WorkspaceId),
        nameof(EntityResponse.EntityTypeId),
        nameof(EntityResponse.AttributeValues),
        nameof(EntityResponse.CreatedAt),
        nameof(EntityResponse.UpdatedAt),
    ];

    /// <summary>
    /// The roles that receive the FULL host shape (docs/06 "View host-only content" = yes). An entity is
    /// content, so — unlike the scene metadata projection — Auditor is NOT here.
    /// </summary>
    public static TheoryData<MembershipRole> HostShapeRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    /// <summary>
    /// The roles that receive the STRIPPED participant shape: the audience roles (Participant/Observer) AND
    /// the audit role (Auditor, "View host-only content" = audit-only, not yes).
    /// </summary>
    public static TheoryData<MembershipRole> StrippedShapeRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    private static Entity CreateEntity(string name = "Lantern")
        => Entity.Create(_organizationId, _workspaceId, _entityTypeId, name, "{\"trait\":\"x\"}", _createdAt);

    // --- Host DTO: includes the host-only fields (the content) ------------------

    [Fact]
    public void Host_projection_includes_all_host_only_fields_including_the_content()
    {
        var entity = CreateEntity();

        var host = EntityResponse.From(entity);

        Assert.Equal(entity.Id, host.Id);
        Assert.Equal(entity.OrganizationId, host.OrganizationId);
        Assert.Equal(entity.WorkspaceId, host.WorkspaceId);
        Assert.Equal(entity.EntityTypeId, host.EntityTypeId);
        Assert.Equal(entity.Name, host.Name);
        Assert.Equal(entity.AttributeValues, host.AttributeValues);
        Assert.Equal(entity.CreatedAt, host.CreatedAt);
        Assert.Equal(entity.UpdatedAt, host.UpdatedAt);
    }

    [Fact]
    public void Host_dto_property_set_is_the_full_host_shape()
    {
        var names = PublicPropertyNames(typeof(EntityResponse));

        Assert.Equal(
            new[] { "Id", "OrganizationId", "WorkspaceId", "EntityTypeId", "Name", "AttributeValues", "CreatedAt", "UpdatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Participant DTO: excludes EVERY host-only field (and the content) ------

    [Fact]
    public void Participant_projection_keeps_only_the_audience_safe_fields()
    {
        var entity = CreateEntity("Beacon");

        var participant = ParticipantEntityResponse.From(entity);

        Assert.Equal(entity.Id, participant.Id);
        Assert.Equal(entity.Name, participant.Name);
    }

    [Fact]
    public void Participant_dto_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the participant DTO is {Id, Name}. This FAILS if a host-only
        // field — most importantly the attribute-values CONTENT — is ever added to the participant DTO
        // (docs/08: "Participant DTOs must not contain hidden content fields"; threats T2/T7).
        var names = PublicPropertyNames(typeof(ParticipantEntityResponse));

        Assert.Equal(
            new[] { "Id", "Name" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Participant_dto_carries_no_rationale_or_content_field()
    {
        var names = PublicPropertyNames(typeof(ParticipantEntityResponse));

        foreach (var name in names)
        {
            Assert.DoesNotContain("reason", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rationale", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attribute", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("visib", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each role maps to the correct shape -------------------------

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public void Projector_maps_host_content_roles_to_the_host_shape(MembershipRole role)
    {
        Assert.True(EntityProjection.ReceivesHostShape(role));

        var projected = EntityProjection.Project(new[] { CreateEntity() }, role);

        var hostArray = Assert.IsType<EntityResponse[]>(projected);
        Assert.Single(hostArray);

        var one = EntityProjection.ProjectOne(CreateEntity(), role);
        Assert.IsType<EntityResponse>(one);
    }

    [Theory]
    [MemberData(nameof(StrippedShapeRoles))]
    public void Projector_maps_audience_and_audit_roles_to_the_participant_shape(MembershipRole role)
    {
        // The audience roles AND the audit role get the stripped shape — the deliberate distinction from the
        // scene projection, since an entity is content (Auditor is audit-only, not a host-content role).
        Assert.False(EntityProjection.ReceivesHostShape(role));

        var projected = EntityProjection.Project(new[] { CreateEntity() }, role);

        var participantArray = Assert.IsType<ParticipantEntityResponse[]>(projected);
        Assert.Single(participantArray);

        var one = EntityProjection.ProjectOne(CreateEntity(), role);
        Assert.IsType<ParticipantEntityResponse>(one);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The host set {Owner,Admin,Host,CoHost} and the stripped set {Participant,Observer,Auditor} together
        // partition ALL seven defined roles with no overlap and no gap.
        var allRoles = Enum.GetValues<MembershipRole>();

        var hostRoles = allRoles.Where(EntityProjection.ReceivesHostShape).ToArray();
        var strippedRoles = allRoles.Where(r => !EntityProjection.ReceivesHostShape(r)).ToArray();

        Assert.Equal(7, allRoles.Length);
        Assert.Equal(
            new[] { MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host, MembershipRole.CoHost }
                .OrderBy(r => r),
            hostRoles.OrderBy(r => r));
        Assert.Equal(
            new[] { MembershipRole.Participant, MembershipRole.Observer, MembershipRole.Auditor }
                .OrderBy(r => r),
            strippedRoles.OrderBy(r => r));
    }

    [Fact]
    public void Projector_fails_closed_to_the_participant_shape_for_an_undefined_role()
    {
        const MembershipRole undefinedRole = (MembershipRole)999;

        Assert.False(EntityProjection.ReceivesHostShape(undefinedRole));

        Assert.IsType<ParticipantEntityResponse[]>(EntityProjection.Project(new[] { CreateEntity() }, undefinedRole));
        Assert.IsType<ParticipantEntityResponse>(EntityProjection.ProjectOne(CreateEntity(), undefinedRole));
    }

    [Fact]
    public void Projector_preserves_the_full_entity_set_for_both_shapes()
    {
        // The SET of entities is unchanged by the projection — only the per-entity SHAPE differs by role.
        var entities = new[] { CreateEntity("A"), CreateEntity("B"), CreateEntity("C") };

        var host = Assert.IsType<EntityResponse[]>(EntityProjection.Project(entities, MembershipRole.Host));
        var participant = Assert.IsType<ParticipantEntityResponse[]>(
            EntityProjection.Project(entities, MembershipRole.Participant));

        Assert.Equal(3, host.Length);
        Assert.Equal(3, participant.Length);
        Assert.Equal(new[] { "A", "B", "C" }, host.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, participant.Select(e => e.Name).ToArray());
    }

    private static string[] PublicPropertyNames(Type type)
    {
        // Exclude the compiler-generated record EqualityContract property.
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}
