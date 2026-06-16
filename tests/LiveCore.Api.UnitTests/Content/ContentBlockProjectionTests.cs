using System.Reflection;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Content;

/// <summary>
/// Unit tests for the host-vs-participant CONTENT-BLOCK DTO separation and its role-based projector
/// (CORE-CB-001, the "Projection by role" obligation on the content-block list and by-id read routes).
///
/// They prove the two REQUIRED properties of the separation and its alignment with the entity projection
/// (a content block, like an entity, IS content):
/// <list type="bullet">
///   <item>The HOST projection (<see cref="ContentBlockResponse"/>) includes the host-only fields — the
///   tenant/workspace/scene boundary ids, the BODY content, the revision number and the host preparation
///   timestamps.</item>
///   <item>The PARTICIPANT projection (<see cref="ParticipantContentBlockResponse"/>) EXCLUDES every host-only
///   field — its property set is exactly {Id, Type} and carries no body content, no tenant/workspace/scene id,
///   no revision number, no preparation timestamp and no authorization rationale (docs/08_API_CONTRACTS.md;
///   threats T2/T7).</item>
///   <item>A content block IS content, so the projector maps roles by the "View host-only content" row of
///   docs/06 (Owner/Admin/Host/CoHost), NOT the scene-style "View workspace metadata" row — so Auditor
///   receives the STRIPPED shape here, and the role classification delegates to the central
///   <c>VisibilityRoles.ViewsHostOnlyContent</c> single source (the same as the entity projection).</item>
/// </list>
///
/// No vertical-specific terms appear anywhere — generic Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class ContentBlockProjectionTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _sceneId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The host-only fields the participant DTO must NEVER carry.</summary>
    private static readonly string[] _hostOnlyFieldNames =
    [
        nameof(ContentBlockResponse.OrganizationId),
        nameof(ContentBlockResponse.WorkspaceId),
        nameof(ContentBlockResponse.SceneId),
        nameof(ContentBlockResponse.Body),
        nameof(ContentBlockResponse.RevisionNumber),
        nameof(ContentBlockResponse.CreatedAt),
        nameof(ContentBlockResponse.UpdatedAt),
    ];

    /// <summary>
    /// The roles that receive the FULL host shape (docs/06 "View host-only content" = yes). A content block is
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

    private static ContentBlock CreateBlock(string body = "Welcome to the show")
        => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, ContentBlockType.Text, body, _createdAt);

    // --- Host DTO: includes the host-only fields (the body content) -------------

    [Fact]
    public void Host_projection_includes_all_host_only_fields_including_the_body()
    {
        var block = CreateBlock();

        var host = ContentBlockResponse.From(block);

        Assert.Equal(block.Id, host.Id);
        Assert.Equal(block.OrganizationId, host.OrganizationId);
        Assert.Equal(block.WorkspaceId, host.WorkspaceId);
        Assert.Equal(block.SceneId, host.SceneId);
        Assert.Equal(block.Type.ToString(), host.Type);
        Assert.Equal(block.Body, host.Body);
        Assert.Equal(block.RevisionNumber, host.RevisionNumber);
        Assert.Equal(block.CreatedAt, host.CreatedAt);
        Assert.Equal(block.UpdatedAt, host.UpdatedAt);
    }

    [Fact]
    public void Host_dto_property_set_is_the_full_host_shape()
    {
        var names = PublicPropertyNames(typeof(ContentBlockResponse));

        Assert.Equal(
            new[] { "Id", "OrganizationId", "WorkspaceId", "SceneId", "Type", "Body", "RevisionNumber", "CreatedAt", "UpdatedAt" }
                .OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    // --- Participant DTO: excludes EVERY host-only field (and the body) ---------

    [Fact]
    public void Participant_projection_keeps_only_the_audience_safe_fields()
    {
        var block = CreateBlock("hidden body");

        var participant = ParticipantContentBlockResponse.From(block);

        Assert.Equal(block.Id, participant.Id);
        Assert.Equal(block.Type.ToString(), participant.Type);
    }

    [Fact]
    public void Participant_dto_property_set_excludes_every_host_only_field()
    {
        // The exact top-level property set of the participant DTO is {Id, Type}. This FAILS if a host-only
        // field — most importantly the BODY content — is ever added to the participant DTO (docs/08:
        // "Participant DTOs must not contain hidden content fields"; threats T2/T7).
        var names = PublicPropertyNames(typeof(ParticipantContentBlockResponse));

        Assert.Equal(
            new[] { "Id", "Type" }.OrderBy(n => n, StringComparer.Ordinal),
            names);

        foreach (var hostOnly in _hostOnlyFieldNames)
        {
            Assert.DoesNotContain(hostOnly, names);
        }
    }

    [Fact]
    public void Participant_dto_carries_no_rationale_or_content_field()
    {
        var names = PublicPropertyNames(typeof(ParticipantContentBlockResponse));

        foreach (var name in names)
        {
            Assert.DoesNotContain("reason", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rationale", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("visib", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Projector: each role maps to the correct shape -------------------------

    [Theory]
    [MemberData(nameof(HostShapeRoles))]
    public void Projector_maps_host_content_roles_to_the_host_shape(MembershipRole role)
    {
        Assert.True(ContentBlockProjection.ReceivesHostShape(role));

        var projected = ContentBlockProjection.Project(new[] { CreateBlock() }, role);

        var hostArray = Assert.IsType<ContentBlockResponse[]>(projected);
        Assert.Single(hostArray);

        var one = ContentBlockProjection.ProjectOne(CreateBlock(), role);
        Assert.IsType<ContentBlockResponse>(one);
    }

    [Theory]
    [MemberData(nameof(StrippedShapeRoles))]
    public void Projector_maps_audience_and_audit_roles_to_the_participant_shape(MembershipRole role)
    {
        // The audience roles AND the audit role get the stripped shape, since a content block is content
        // (Auditor is audit-only, not a host-content role).
        Assert.False(ContentBlockProjection.ReceivesHostShape(role));

        var projected = ContentBlockProjection.Project(new[] { CreateBlock() }, role);

        var participantArray = Assert.IsType<ParticipantContentBlockResponse[]>(projected);
        Assert.Single(participantArray);

        var one = ContentBlockProjection.ProjectOne(CreateBlock(), role);
        Assert.IsType<ParticipantContentBlockResponse>(one);
    }

    [Fact]
    public void Projector_covers_every_defined_role_exactly_once_in_the_two_sets()
    {
        // The host set {Owner,Admin,Host,CoHost} and the stripped set {Participant,Observer,Auditor} together
        // partition ALL seven defined roles with no overlap and no gap.
        var allRoles = Enum.GetValues<MembershipRole>();

        var hostRoles = allRoles.Where(ContentBlockProjection.ReceivesHostShape).ToArray();
        var strippedRoles = allRoles.Where(r => !ContentBlockProjection.ReceivesHostShape(r)).ToArray();

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

        Assert.False(ContentBlockProjection.ReceivesHostShape(undefinedRole));

        Assert.IsType<ParticipantContentBlockResponse[]>(
            ContentBlockProjection.Project(new[] { CreateBlock() }, undefinedRole));
        Assert.IsType<ParticipantContentBlockResponse>(
            ContentBlockProjection.ProjectOne(CreateBlock(), undefinedRole));
    }

    [Fact]
    public void Projector_preserves_the_full_block_set_for_both_shapes()
    {
        // The SET of blocks is unchanged by the projection — only the per-block SHAPE differs by role.
        var blocks = new[] { CreateBlock("A"), CreateBlock("B"), CreateBlock("C") };

        var host = Assert.IsType<ContentBlockResponse[]>(
            ContentBlockProjection.Project(blocks, MembershipRole.Host));
        var participant = Assert.IsType<ParticipantContentBlockResponse[]>(
            ContentBlockProjection.Project(blocks, MembershipRole.Participant));

        Assert.Equal(3, host.Length);
        Assert.Equal(3, participant.Length);
        Assert.Equal(new[] { "A", "B", "C" }, host.Select(b => b.Body).ToArray());
        // Both shapes carry the same blocks (same ids); only the participant shape drops the body.
        Assert.Equal(host.Select(b => b.Id).ToArray(), participant.Select(b => b.Id).ToArray());
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
