namespace LiveCore.Api.Visibility;

/// <summary>
/// Request body for the reveal command (CORE-VIS-004,
/// <c>POST /api/v1/sessions/{sessionId}/reveal</c>, csv/api_routes.csv "Idempotent reveal", roles
/// Host/CoHost/Owner/Admin). The session is taken from the route path (it pins the workspace); the
/// target organization is supplied as <see cref="OrganizationSlug"/> and resolved by the tenant
/// context resolver (token organization claim AND persisted membership — defence in depth, threat
/// T5), mirroring the session start/end commands.
///
/// The body names the resource to reveal generically by its kind and id
/// (<see cref="ResourceType"/> + <see cref="ResourceId"/>), exactly as a visibility rule addresses
/// its resource; it carries no vertical vocabulary (docs/04_PRODUCT_BOUNDARIES.md). The client's
/// retry-safety token is the <c>Idempotency-Key</c> request HEADER (docs/08_API_CONTRACTS.md), not a
/// body field.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the session's workspace, used to resolve the tenant
/// context (the route carries no organization in its path).
/// </param>
/// <param name="ResourceType">
/// The kind of resource to reveal — the name of a <see cref="VisibilityResourceType"/>
/// (Scene/ContentBlock/Entity). Parsed by name; a numeric or unknown value is a 400.
/// </param>
/// <param name="ResourceId">The surrogate id of the resource to reveal.</param>
/// <param name="ParticipantId">
/// Optional target of a SELECTED-participant reveal (CORE-VIS-005): when set, the resource is
/// revealed ONLY to that participant; when omitted/<see langword="null"/>, it is revealed to the
/// whole audience. A present-but-empty value is a 400; a set value must be a participant of the
/// session's workspace (otherwise hidden as 404).
/// </param>
public sealed record RevealRequest(
    string? OrganizationSlug,
    string? ResourceType,
    Guid ResourceId,
    Guid? ParticipantId = null);

/// <summary>
/// Response body of the reveal command (CORE-VIS-004). It echoes the revealed resource (kind + id),
/// confirms it is now visible to the audience, and reports whether this call APPLIED the reveal or
/// recognized an idempotent retry (<see cref="RevealOutcome"/>, serialized by name). It is a generic,
/// product-neutral confirmation — no content, no payload and no internal authorization rationale
/// (docs/08_API_CONTRACTS.md; threat T7).
/// </summary>
/// <param name="ResourceType">The kind of resource that was revealed (the enum name).</param>
/// <param name="ResourceId">The surrogate id of the resource that was revealed.</param>
/// <param name="Visible">Always <see langword="true"/> after a successful reveal.</param>
/// <param name="Outcome">
/// Whether the reveal was newly applied or recognized as an idempotent retry (the
/// <see cref="RevealOutcome"/> name).
/// </param>
/// <param name="ParticipantId">
/// The participant the resource was revealed to (a selected-participant reveal), or
/// <see langword="null"/> when it was revealed to the whole audience.
/// </param>
public sealed record RevealResponse(
    string ResourceType,
    Guid ResourceId,
    bool Visible,
    string Outcome,
    Guid? ParticipantId)
{
    /// <summary>Projects a <see cref="RevealResult"/> into its response DTO.</summary>
    public static RevealResponse From(RevealResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RevealResponse(
            result.ResourceType.ToString(),
            result.ResourceId,
            Visible: true,
            result.Outcome.ToString(),
            result.TargetParticipantId);
    }
}
