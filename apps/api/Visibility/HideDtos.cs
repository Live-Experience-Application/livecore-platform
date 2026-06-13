namespace LiveCore.Api.Visibility;

/// <summary>
/// Request body for the hide command (CORE-REV-001, the "Reveal Lifecycle" hide / un-reveal,
/// <c>POST /api/v1/sessions/{sessionId}/hide</c>, roles Host/CoHost/Owner/Admin). It mirrors
/// <see cref="RevealRequest"/> exactly — the session is taken from the route path (it pins the
/// workspace); the target organization is supplied as <see cref="OrganizationSlug"/> and resolved by the
/// tenant context resolver (token organization claim AND persisted membership — defence in depth, threat
/// T5).
///
/// The body names the resource to hide generically by its kind and id (<see cref="ResourceType"/> +
/// <see cref="ResourceId"/>), exactly as a visibility rule addresses its resource; it carries no vertical
/// vocabulary (docs/04_PRODUCT_BOUNDARIES.md). The client's retry-safety token is the
/// <c>Idempotency-Key</c> request HEADER (docs/08_API_CONTRACTS.md), not a body field.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the session's workspace, used to resolve the tenant
/// context (the route carries no organization in its path).
/// </param>
/// <param name="ResourceType">
/// The kind of resource to hide — the name of a <see cref="VisibilityResourceType"/>
/// (Scene/ContentBlock/Entity). Parsed by name; a numeric or unknown value is a 400.
/// </param>
/// <param name="ResourceId">The surrogate id of the resource to hide.</param>
/// <param name="ParticipantId">
/// Optional target of a SELECTED-participant hide (mirroring the selected reveal, CORE-VIS-005): when
/// set, the resource is hidden ONLY for that participant; when omitted/<see langword="null"/>, it is
/// hidden from the whole audience. A present-but-empty value is a 400; a set value must be a participant
/// of the session's workspace (otherwise hidden as 404).
/// </param>
public sealed record HideRequest(
    string? OrganizationSlug,
    string? ResourceType,
    Guid ResourceId,
    Guid? ParticipantId = null);

/// <summary>
/// Response body of the hide command (CORE-REV-001). It echoes the hidden resource (kind + id), confirms
/// it is now hidden from the audience, and reports whether this call APPLIED the hide or recognized an
/// idempotent retry (<see cref="HideOutcome"/>, serialized by name). It is a generic, product-neutral
/// confirmation — no content, no payload and no internal authorization rationale
/// (docs/08_API_CONTRACTS.md; threat T7). It mirrors <see cref="RevealResponse"/> with the opposite
/// visibility sense.
/// </summary>
/// <param name="ResourceType">The kind of resource that was hidden (the enum name).</param>
/// <param name="ResourceId">The surrogate id of the resource that was hidden.</param>
/// <param name="Visible">Always <see langword="false"/> after a successful hide.</param>
/// <param name="Outcome">
/// Whether the hide was newly applied or recognized as an idempotent retry (the <see cref="HideOutcome"/>
/// name).
/// </param>
/// <param name="ParticipantId">
/// The participant the resource was hidden for (a selected-participant hide), or <see langword="null"/>
/// when it was hidden from the whole audience.
/// </param>
public sealed record HideResponse(
    string ResourceType,
    Guid ResourceId,
    bool Visible,
    string Outcome,
    Guid? ParticipantId)
{
    /// <summary>Projects a <see cref="HideResult"/> into its response DTO.</summary>
    public static HideResponse From(HideResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new HideResponse(
            result.ResourceType.ToString(),
            result.ResourceId,
            Visible: false,
            result.Outcome.ToString(),
            result.TargetParticipantId);
    }
}
