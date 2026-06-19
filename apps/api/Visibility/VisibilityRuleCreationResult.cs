// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Outcome of the visibility-rule create command (CORE-SVIS-005, the "Vertical Adopter Consumability
/// Completeness" epic), returned by <see cref="VisibilityRuleService.CreateAsync"/>.
///
/// A create is gated by the SAME-WORKSPACE invariant the database foreign key cannot enforce: the
/// referenced resource — and, for a selected-participant rule, the target participant — must live in the
/// rule's OWN (organization, workspace). So the outcomes are:
/// <list type="bullet">
///   <item><see cref="VisibilityRuleCreationStatus.Created"/> — the resource (and any participant) resolved
///   within the scope, a new rule was created and audited (carrying the created
///   <see cref="VisibilityRule"/>); the endpoint maps it to <c>201 Created</c>.</item>
///   <item><see cref="VisibilityRuleCreationStatus.ResourceNotInWorkspace"/> — the referenced resource does
///   not exist in the rule's workspace (an unknown id, or one belonging to another workspace/tenant —
///   indistinguishable, so nothing is created and no audit fact is written). The endpoint maps it to a
///   <c>400</c> (the body-supplied resource reference does not resolve in the caller's authorized workspace;
///   the same response for unknown and foreign resources leaks nothing, threats T1/T5).</item>
///   <item><see cref="VisibilityRuleCreationStatus.ParticipantNotInWorkspace"/> — a selected-participant
///   rule named a participant that is not in the rule's workspace (an unknown participant, or one of another
///   workspace/tenant). The endpoint hides it as a <c>404</c>, exactly as the reveal command hides a
///   cross-workspace participant target (a host must not be able to target, or probe for, a participant
///   outside the session's workspace; threat T5).</item>
///   <item><see cref="VisibilityRuleCreationStatus.Duplicate"/> — a rule already exists for the same
///   (session, resource, DIMENSION): the filtered unique index (CORE-SVIS-002) rejected the insert. The
///   endpoint maps it to a <c>409</c> (<c>duplicate_resource</c>); nothing else was changed and no audit
///   fact is written.</item>
/// </list>
/// </summary>
internal readonly record struct VisibilityRuleCreationResult
{
    private VisibilityRuleCreationResult(VisibilityRuleCreationStatus status, VisibilityRule? rule)
    {
        Status = status;
        Rule = rule;
    }

    /// <summary>The outcome kind.</summary>
    public VisibilityRuleCreationStatus Status { get; }

    /// <summary>
    /// The created rule when <see cref="Status"/> is <see cref="VisibilityRuleCreationStatus.Created"/>;
    /// otherwise <see langword="null"/> (no rule was created).
    /// </summary>
    public VisibilityRule? Rule { get; }

    /// <summary>The resource (and any participant) resolved within the workspace and a rule was created and audited.</summary>
    public static VisibilityRuleCreationResult Created(VisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new VisibilityRuleCreationResult(VisibilityRuleCreationStatus.Created, rule);
    }

    /// <summary>The referenced resource does not exist within the resolved tenant and workspace; nothing was created.</summary>
    public static VisibilityRuleCreationResult ResourceNotInWorkspace { get; } =
        new(VisibilityRuleCreationStatus.ResourceNotInWorkspace, null);

    /// <summary>The target participant is not in the resolved workspace; nothing was created.</summary>
    public static VisibilityRuleCreationResult ParticipantNotInWorkspace { get; } =
        new(VisibilityRuleCreationStatus.ParticipantNotInWorkspace, null);

    /// <summary>A rule already exists for the same (session, resource, dimension); nothing was created.</summary>
    public static VisibilityRuleCreationResult Duplicate { get; } =
        new(VisibilityRuleCreationStatus.Duplicate, null);
}

/// <summary>The kind of <see cref="VisibilityRuleCreationResult"/>.</summary>
internal enum VisibilityRuleCreationStatus
{
    /// <summary>A new visibility rule was created, persisted and appended to the append-only audit log.</summary>
    Created = 1,

    /// <summary>
    /// The referenced resource does not exist within the resolved tenant and workspace, so no rule was
    /// created (the same-workspace coupling the database foreign key cannot enforce).
    /// </summary>
    ResourceNotInWorkspace = 2,

    /// <summary>
    /// The selected-participant target is not in the resolved tenant and workspace, so no rule was created.
    /// </summary>
    ParticipantNotInWorkspace = 3,

    /// <summary>
    /// A rule already exists for the same (session, resource, dimension): the filtered unique index
    /// (CORE-SVIS-002) rejected the insert.
    /// </summary>
    Duplicate = 4,
}
