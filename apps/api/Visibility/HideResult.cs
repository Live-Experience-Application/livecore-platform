// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Whether a hide command actually changed state or recognized a retry (CORE-REV-001, the "Reveal
/// Lifecycle" hide / un-reveal). Both outcomes leave the resource HIDDEN from the audience — the command
/// is idempotent — so both are success; the distinction lets a caller and the tests assert that a
/// repeated hide with the same idempotency key did NOT apply a second effect. It mirrors
/// <see cref="RevealOutcome"/> for the opposite direction.
/// </summary>
public enum HideOutcome
{
    /// <summary>
    /// The hide was applied for the first time for its idempotency key: the resource was made hidden
    /// from the audience (a visible rule was flipped to hidden, or the resource was already without a
    /// visible rule in that dimension).
    /// </summary>
    Applied = 1,

    /// <summary>
    /// The idempotency key had already been processed, so the hide was NOT re-applied. The resource is
    /// already hidden; no duplicate effect was produced (docs/08_API_CONTRACTS.md: a repeated command with
    /// the same idempotency key must not create duplicate effects).
    /// </summary>
    AlreadyApplied = 2,
}

/// <summary>
/// The result of a hide command (CORE-REV-001): which resource was hidden and whether the call applied
/// the hide or recognized an idempotent retry. After either outcome the resource is hidden from the
/// audience (or from the selected participant). It mirrors <see cref="RevealResult"/> for the opposite
/// direction.
/// </summary>
public sealed class HideResult
{
    private HideResult(
        HideOutcome outcome,
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

    /// <summary>Whether the hide was newly applied or recognized as an idempotent retry.</summary>
    public HideOutcome Outcome { get; }

    /// <summary>The kind of resource that was hidden.</summary>
    public VisibilityResourceType ResourceType { get; }

    /// <summary>The surrogate id of the resource that was hidden.</summary>
    public Guid ResourceId { get; }

    /// <summary>
    /// The participant the resource was hidden for (a selected-participant hide), or
    /// <see langword="null"/> when it was hidden from the whole audience.
    /// </summary>
    public Guid? TargetParticipantId { get; }

    /// <summary>
    /// Whether this call ACTUALLY changed the resource's visibility (a visible rule was flipped to
    /// hidden), as opposed to a no-op (an idempotent retry, or a fresh key for an already-hidden
    /// resource). This is the same change signal the audit record uses (CORE-VIS-006), so a caller can
    /// emit a downstream effect — the durable realtime <c>ContentHidden</c> event — exactly once per real
    /// change and never on a no-op.
    /// </summary>
    public bool VisibilityChanged { get; }

    /// <summary>
    /// Whether the hide was REFUSED because the resource's rule in the target dimension is SEALED (locked)
    /// (CORE-VSEAL-001): a locked rule's visibility cannot be changed, so the command applied nothing,
    /// audited nothing and recorded no idempotency key. The endpoint maps this fail-closed outcome to
    /// <c>409</c>. When <see langword="true"/>, <see cref="VisibilityChanged"/> is always
    /// <see langword="false"/>. An unlocked rule never sets this, so a pre-seal hide is unaffected.
    /// </summary>
    public bool BlockedByLock { get; }

    /// <summary>
    /// Builds a result for a hide that was applied for the first time, recording whether it actually
    /// changed visibility.
    /// </summary>
    public static HideResult Applied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        bool visibilityChanged)
        => new(HideOutcome.Applied, resourceType, resourceId, targetParticipantId, visibilityChanged, blockedByLock: false);

    /// <summary>
    /// Builds a result for an idempotent retry (the hide was already applied). A retry changes nothing,
    /// so <see cref="VisibilityChanged"/> is always <see langword="false"/>.
    /// </summary>
    public static HideResult AlreadyApplied(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(HideOutcome.AlreadyApplied, resourceType, resourceId, targetParticipantId, visibilityChanged: false, blockedByLock: false);

    /// <summary>
    /// Builds a result for a hide that was REFUSED because the target rule is SEALED (locked)
    /// (CORE-VSEAL-001) — nothing changed, nothing was audited and no idempotency key was recorded; the
    /// endpoint returns <c>409</c>. The outcome is reported as <see cref="HideOutcome.Applied"/> only as a
    /// placeholder (the endpoint short-circuits on <see cref="BlockedByLock"/> before reading the outcome).
    /// </summary>
    public static HideResult Blocked(
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId)
        => new(HideOutcome.Applied, resourceType, resourceId, targetParticipantId, visibilityChanged: false, blockedByLock: true);
}
