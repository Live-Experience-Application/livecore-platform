// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
        Guid? targetParticipantId,
        bool visibilityChanged,
        bool blockedByLock)
    {
        Outcome = outcome;
        ResourceType = resourceType;
        ResourceId = resourceId;
        TargetParticipantId = targetParticipantId;
        VisibilityChanged = visibilityChanged;
        BlockedByLock = blockedByLock;
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

    /// <summary>
    /// Whether this call ACTUALLY changed the resource's visibility (a visible rule was created or a
    /// hidden rule was flipped to visible), as opposed to a no-op (an idempotent retry, or a fresh key
    /// for an already-visible resource). This is the same change signal the audit record uses
    /// (CORE-VIS-006), so a caller can emit a downstream effect — the durable realtime
    /// <c>ContentRevealed</c> event (CORE-RT-003) — exactly once per real change and never on a no-op.
    /// </summary>
    public bool VisibilityChanged { get; }

    /// <summary>
    /// Whether the reveal was REFUSED because the resource's rule in the target dimension is SEALED (locked)
    /// (CORE-VSEAL-001): a locked rule's visibility cannot be changed or revealed, so the command applied
    /// nothing, audited nothing and recorded no idempotency key. The endpoint maps this fail-closed outcome
    /// to <c>409</c>. When <see langword="true"/>, <see cref="VisibilityChanged"/> is always
    /// <see langword="false"/> (nothing changed). An unlocked rule never sets this, so a pre-seal reveal is
    /// unaffected.
    /// </summary>
    public bool BlockedByLock { get; }

    /// <summary>
    /// Builds a result for a reveal that was applied for the first time, recording whether it actually
    /// changed visibility.
    /// </summary>
    public static RevealResult Applied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        bool visibilityChanged)
        => new(RevealOutcome.Applied, resourceType, resourceId, targetParticipantId, visibilityChanged, blockedByLock: false);

    /// <summary>
    /// Builds a result for an idempotent retry (the reveal was already applied). A retry changes nothing,
    /// so <see cref="VisibilityChanged"/> is always <see langword="false"/>.
    /// </summary>
    public static RevealResult AlreadyApplied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(RevealOutcome.AlreadyApplied, resourceType, resourceId, targetParticipantId, visibilityChanged: false, blockedByLock: false);

    /// <summary>
    /// Builds a result for a reveal that was REFUSED because the target rule is SEALED (locked)
    /// (CORE-VSEAL-001) — nothing changed, nothing was audited and no idempotency key was recorded; the
    /// endpoint returns <c>409</c>. The outcome is reported as <see cref="RevealOutcome.Applied"/> only as a
    /// placeholder (the endpoint short-circuits on <see cref="BlockedByLock"/> before reading the outcome).
    /// </summary>
    public static RevealResult Blocked(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(RevealOutcome.Applied, resourceType, resourceId, targetParticipantId, visibilityChanged: false, blockedByLock: true);
}
