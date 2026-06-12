namespace LiveCore.Api.Visibility;

/// <summary>
/// Whether a reveal command actually changed state or recognized a retry (CORE-VIS-004). Both
/// outcomes leave the resource VISIBLE to the audience — the command is idempotent — so both are
/// success; the distinction lets a caller and the tests assert that a repeated reveal with the same
/// idempotency key did NOT apply a second effect.
/// </summary>
public enum RevealOutcome
{
    /// <summary>
    /// The reveal was applied for the first time for its idempotency key: the resource was made
    /// visible to the audience (a visibility rule now makes it visible).
    /// </summary>
    Applied = 1,

    /// <summary>
    /// The idempotency key had already been processed, so the reveal was NOT re-applied. The resource
    /// is already visible; no duplicate effect was produced (docs/08_API_CONTRACTS.md: a repeated
    /// reveal with the same idempotency key must not create duplicate effects).
    /// </summary>
    AlreadyApplied = 2,
}

/// <summary>
/// The result of a reveal command (CORE-VIS-004): which resource was revealed and whether the call
/// applied the reveal or recognized an idempotent retry. After either outcome the resource is visible
/// to the audience.
/// </summary>
public sealed class RevealResult
{
    private RevealResult(
        RevealOutcome outcome,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
    {
        Outcome = outcome;
        ResourceType = resourceType;
        ResourceId = resourceId;
        TargetParticipantId = targetParticipantId;
    }

    /// <summary>Whether the reveal was newly applied or recognized as an idempotent retry.</summary>
    public RevealOutcome Outcome { get; }

    /// <summary>The kind of resource that was revealed.</summary>
    public VisibilityResourceType ResourceType { get; }

    /// <summary>The surrogate id of the resource that was revealed.</summary>
    public Guid ResourceId { get; }

    /// <summary>
    /// The participant the resource was revealed to (a selected-participant reveal, CORE-VIS-005), or
    /// <see langword="null"/> when it was revealed to the whole audience.
    /// </summary>
    public Guid? TargetParticipantId { get; }

    /// <summary>Builds a result for a reveal that was applied for the first time.</summary>
    public static RevealResult Applied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(RevealOutcome.Applied, resourceType, resourceId, targetParticipantId);

    /// <summary>Builds a result for an idempotent retry (the reveal was already applied).</summary>
    public static RevealResult AlreadyApplied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(RevealOutcome.AlreadyApplied, resourceType, resourceId, targetParticipantId);
}
