// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// VisibilityRule aggregate of the Visibility module (CORE-VIS-001, the first story of the
/// "Visibility and Reveal Engine" epic).
///
/// A <see cref="VisibilityRule"/> is the Core-owned, product-neutral "Rule determining audience
/// visibility" (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md: the Visibility module owns
/// "visibility rules"; csv/database_tables.csv: table <c>visibility_rules</c>, module Visibility,
/// scope <c>workspace</c>, "Audience rules"). It binds ONE Core resource — named generically by a
/// (<see cref="ResourceType"/>, <see cref="ResourceId"/>) pair — to a base audience
/// <see cref="VisibilityState"/> (Hidden from the audience, or Visible to it). The documented
/// critical index is <c>visibility_rules(workspace_id, resource_type, resource_id)</c>
/// (docs/10_DATABASE_SCHEMA.md), which is exactly this resource-addressing shape.
///
/// THE CENTRAL SECURITY MODULE — WHAT THIS STORY IS AND IS NOT. The Visibility module is "the
/// central security module" and visibility logic must not be "duplicate[d] elsewhere"
/// (docs/05_MODULE_CONTRACTS.md), and "entity visibility is not computed ad hoc in many places"
/// (docs/02_ARCHITECTURE.md). This story is the rule MODEL + persistence only: the durable record of
/// a resource's base audience visibility. It deliberately does NOT decide a viewer's effective
/// access — that is <c>CanViewResource</c> (CORE-VIS-002) — does NOT compute a participant's visible
/// set (<c>GetVisibleResourcesForParticipant</c>, the deferred audience computation the CORE-SES-005
/// and CORE-ENT-005 skeletons left empty), does NOT preview-as-participant (CORE-VIS-003), and does
/// NOT implement the reveal COMMAND with its authorization, idempotency and append-only event
/// (CORE-VIS-004) nor selected-participant reveal (CORE-VIS-005). It exposes only the state-
/// transition PRIMITIVE <see cref="ChangeVisibility"/> that the later reveal command builds on,
/// exactly as <c>Session.Start</c>/<c>Session.End</c> (the CORE-SES-002 model story) are the
/// primitives the CORE-SES-004 start/end COMMAND builds on.
///
/// Tenant + workspace + SESSION boundary: a reveal is session-scoped (CORE-SVIS-001;
/// csv/database_tables.csv scopes <c>visibility_rules</c> to <c>session</c>;
/// docs/adr/0013-session-scoped-visibility-rules.md), so the rule carries <see cref="OrganizationId"/>
/// (the tenant; checked first), <see cref="WorkspaceId"/> and <see cref="SessionId"/> (checked last),
/// mirroring the session-scoped <c>session_events</c> stream (docs/10_DATABASE_SCHEMA.md: tenant-scoped
/// tables include <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>,
/// session-scoped tables include <c>session_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). A
/// rule belongs to exactly one organization, one workspace and one session for its whole lifetime.
/// Because a workspace may run several CONCURRENT sessions, the workspace boundary alone is NOT enough:
/// the surrogate <see cref="Id"/> alone never authorizes access, and every lookup is scoped by
/// <see cref="OrganizationId"/>, <see cref="WorkspaceId"/> and <see cref="SessionId"/> so a reveal in one
/// session can never be read — or fanned out — through another session's, another workspace's or another
/// tenant's id (the cross-session data leak; threat T5/T3; T1 broken object-level authorization).
///
/// Resource reference: <see cref="ResourceType"/> + <see cref="ResourceId"/> name the governed
/// resource. <see cref="ResourceId"/> is a plain surrogate id, NOT a database foreign key: a single
/// column cannot foreign-key into the three different resource tables
/// (<c>scenes</c>/<c>content_blocks</c>/<c>entities</c>), so the rule is the polymorphic owner.
/// Ensuring the referenced resource EXISTS and is in the rule's OWN workspace is the responsibility
/// of the application flow that creates rules (the later reveal / visibility-rule command), which
/// resolves the resource through a workspace-scoped lookup before creating the rule — the same
/// same-workspace-coupling precedent as <c>ContentBlock.SceneId</c> and <c>Entity.EntityTypeId</c>.
/// The authorization-relevant fields (<see cref="ResourceType"/>, <see cref="ResourceId"/>,
/// <see cref="Visibility"/>) are stored as REAL COLUMNS, never inside arbitrary JSON
/// (docs/10_DATABASE_SCHEMA.md: "Do not store visibility rules only inside arbitrary JSON";
/// JSONB is "not for core authorization fields").
///
/// Visibility invariant: <see cref="Visibility"/> is the base audience state — Hidden or Visible.
/// It can be changed (<see cref="ChangeVisibility"/>, the reveal primitive) but the tenant boundary,
/// the workspace, the id and the governed resource are immutable: changing visibility never moves
/// the rule to another organization, workspace or resource (threat T5). Nothing on this aggregate is
/// free-form content, so <see cref="ToString"/> is fully identifier/enum based (threat T7).
/// </summary>
public sealed class VisibilityRule
{
    private VisibilityRule(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility,
        Guid? targetParticipantId,
        bool locked,
        DateTimeOffset? scheduledRevealAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Visibility rule id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (!IsValidResourceType(resourceType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceType),
                resourceType,
                "Resource type is not a defined visibility resource type.");
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        if (!IsValidVisibility(visibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibility),
                visibility,
                "Visibility is not a defined visibility state.");
        }

        // A null target means audience-wide; a SET target must be a real participant id. An empty
        // target id can never address a participant, so it is rejected (it must not silently degrade
        // to an audience-wide rule, which would over-share — threat T5).
        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null for an audience-wide rule.",
                nameof(targetParticipantId));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A visibility rule cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SessionId = sessionId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Visibility = visibility;
        TargetParticipantId = targetParticipantId;
        Locked = locked;
        // A set scheduled-reveal time is normalized to UTC so the persisted timestamptz value is
        // offset-independent (docs/10_DATABASE_SCHEMA.md); a null schedule (the default) stays null.
        ScheduledRevealAt = scheduledRevealAt?.ToUniversalTime();
        // Timestamps are normalized to UTC so the persisted timestamptz values are
        // offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private VisibilityRule()
    {
    }

    /// <summary>
    /// Surrogate key of the visibility rule row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes access: lookups are always scoped
    /// by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the rule: the id of the organization that owns the workspace this rule
    /// belongs to (the <c>organization_id</c> foreign key to the <c>organizations</c> table).
    /// Immutable; a rule never crosses to another organization. The organization boundary is checked
    /// before the workspace boundary, so this column lets isolation be enforced at the row level
    /// (threat T5; docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the rule: the id of the workspace this rule belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; a rule never moves
    /// to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Session boundary of the rule: the id of the session this rule belongs to (the <c>session_id</c>
    /// foreign key to the <c>sessions</c> table). A reveal is SESSION-SCOPED (CORE-SVIS-001,
    /// docs/adr/0013-session-scoped-visibility-rules.md): a resource revealed in one session is visible
    /// ONLY within that session, so the rule that records the reveal belongs to exactly one session and
    /// every visibility decision and realtime recipient set is bounded by it. A workspace may run several
    /// concurrent sessions, so the <see cref="WorkspaceId"/> alone is NOT enough to isolate a reveal — a
    /// participant connected to a DIFFERENT concurrent session of the same workspace must never see it
    /// (the cross-session leak threat T5/T3 in docs/07_SECURITY_THREAT_MODEL.md). Immutable; a rule never
    /// moves to another session. The owning session already pins the workspace and tenant, but storing all
    /// three on the row lets isolation be enforced at the row level and checked in boundary order
    /// (organization, then workspace, then session).
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Generic kind of the governed resource — Scene, ContentBlock or Entity
    /// (docs/03_DOMAIN_LANGUAGE.md). Immutable; a rule governs one resource for its whole lifetime.
    /// Part of the documented critical index <c>visibility_rules(session_id, resource_type,
    /// resource_id)</c> (CORE-SVIS-001; docs/10_DATABASE_SCHEMA.md), now led by the session column so a
    /// resource's rules are looked up WITHIN a session.
    /// </summary>
    public VisibilityResourceType ResourceType { get; }

    /// <summary>
    /// Surrogate id of the governed resource (a <c>scenes</c>/<c>content_blocks</c>/<c>entities</c>
    /// row, per <see cref="ResourceType"/>). Immutable. Intentionally NOT a database foreign key —
    /// the reference is polymorphic across three tables; the same-workspace coupling is enforced by
    /// the application flow that creates rules (see the type summary).
    /// </summary>
    public Guid ResourceId { get; }

    /// <summary>
    /// Base visibility state of the governed resource — Hidden or Visible
    /// (<see cref="VisibilityState"/>). Mutable only through <see cref="ChangeVisibility"/>.
    /// </summary>
    public VisibilityState Visibility { get; private set; }

    /// <summary>
    /// The participant this rule is scoped to (CORE-VIS-005), or <see langword="null"/> for an
    /// AUDIENCE-WIDE rule that applies to the whole audience. When set (the <c>target_participant_id</c>
    /// foreign key to the <c>participants</c> table), the rule's visibility applies ONLY to that
    /// participant — a selected-participant reveal: a non-selected participant must not see it (threat
    /// T5; docs/06_AUTHORIZATION_MATRIX.md "Send private content"; docs/09_EVENT_CATALOG.md
    /// "selected recipients"). Immutable; a rule's target is fixed for its whole life.
    /// </summary>
    public Guid? TargetParticipantId { get; }

    /// <summary>
    /// Whether this rule is SEALED (locked): a server-asserted authoring lock that expresses a
    /// permanently-restricted resource (CORE-VSEAL-001, raised by a vertical adopter, ARC-GAP-002). A
    /// locked rule's audience visibility CANNOT be changed — a reveal, hide or visibility change that
    /// targets a locked rule is rejected fail-closed (the reveal/hide command resolves the locked rule and
    /// refuses; the endpoint maps it to <c>409</c>). The lock is an ORTHOGONAL authoring concern, NOT a
    /// third <see cref="VisibilityState"/> member: it gates whether the existing binary Hidden/Visible
    /// state may transition, and leaves the binary state — and every projection and recipient decision that
    /// branches on it — exactly as before. It is set and cleared only by the authoring roles
    /// (<see cref="Lock"/>/<see cref="Unlock"/>); an UNLOCKED rule (the default) behaves exactly as a
    /// pre-CORE-VSEAL-001 rule. The flag is projected on the rule and, where audience-safe, on the
    /// participant visible-feed item so a consumer can render a locked presentation state bound to a
    /// server fact (never host content; threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public bool Locked { get; private set; }

    /// <summary>
    /// The optional SCHEDULED-REVEAL time of this rule (UTC), or <see langword="null"/> when the rule has no
    /// schedule (CORE-VSEAL-002, the "Scheduled and Sealed Visibility" epic, raised by a vertical adopter,
    /// ARC-GAP-002). When set on a <see cref="VisibilityState.Hidden"/> rule, the resource is meant to stay
    /// hidden until this time and then be AUTOMATICALLY revealed by the worker's background sweep
    /// (<c>ScheduledRevealService</c>) — which drives the SAME central reveal command
    /// (<see cref="RevealService"/>) as a live host reveal, so the auto-reveal is gated through the Visibility
    /// engine and emits the normal session events to exactly the authorized audience (it never reveals to an
    /// unauthorized participant). The "WHEN" half of controlling visibility (docs/01_PRODUCT_VISION_AND_SCOPE.md).
    ///
    /// It is set ONLY at creation time (the create command's optional <c>scheduledRevealAt</c>); it is never an
    /// authorization input (the binary <see cref="Visibility"/> state and the rule's tenant/workspace/session
    /// boundaries decide access exactly as before) and is left UNCHANGED by the auto-reveal — the sweep relies on
    /// the binary state flip (Hidden -&gt; Visible) plus a deterministic reveal idempotency key for its
    /// at-most-once guarantee, so a rule with no schedule behaves exactly as a pre-CORE-VSEAL-002 rule. The
    /// timestamp is projected on the rule (and, where audience-safe, on the participant visible-feed item) so a
    /// consumer can render a scheduled presentation state — it is a server fact about WHEN a resource is/was
    /// scheduled to appear, never host content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public DateTimeOffset? ScheduledRevealAt { get; }

    /// <summary>When this rule was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this rule was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new visibility rule binding the given resource (named by type + id) in the given
    /// session (owned by the given workspace and organization) to the given base audience visibility
    /// state. The reveal is SESSION-SCOPED (CORE-SVIS-001): the rule is visible only within
    /// <paramref name="sessionId"/>. The caller (the reveal / visibility-rule command) supplies the
    /// tenant, workspace, session and resource ids; it is responsible for having resolved the resource
    /// through a workspace-scoped lookup so the resource is in the rule's own workspace, and the session
    /// through a tenant-scoped lookup so the session is in that workspace. The aggregate enforces only
    /// the structural invariants (non-empty ids, a defined resource type and visibility state).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id or resource id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The resource type or the visibility state is not defined.
    /// </exception>
    public static VisibilityRule Create(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility,
        DateTimeOffset createdAt,
        DateTimeOffset? scheduledRevealAt = null)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            sessionId,
            resourceType,
            resourceId,
            visibility,
            targetParticipantId: null,
            // A new rule is UNLOCKED: locking is an explicit, separately-authorized authoring action
            // (CORE-VSEAL-001), so a freshly created rule behaves exactly as a pre-seal rule.
            locked: false,
            // An optional scheduled-reveal time (CORE-VSEAL-002): null (the default) means no schedule, so the
            // rule behaves exactly as a pre-CORE-VSEAL-002 rule.
            scheduledRevealAt,
            createdAt,
            createdAt);

    /// <summary>
    /// Creates a new visibility rule scoped to a SINGLE participant (CORE-VIS-005) — a
    /// selected-participant reveal. Identical to <see cref="Create"/> except the rule's visibility
    /// applies ONLY to <paramref name="targetParticipantId"/>, not the whole audience. The caller is
    /// responsible for having resolved the participant through a workspace-scoped lookup so the
    /// participant is in the rule's own workspace (mirrors the resource same-workspace coupling).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id, resource id or target participant id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The resource type or the visibility state is not defined.
    /// </exception>
    public static VisibilityRule CreateForParticipant(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid targetParticipantId,
        VisibilityState visibility,
        DateTimeOffset createdAt,
        DateTimeOffset? scheduledRevealAt = null)
    {
        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException("Target participant id must not be empty.", nameof(targetParticipantId));
        }

        return new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            sessionId,
            resourceType,
            resourceId,
            visibility,
            targetParticipantId,
            // A new selected-participant rule is UNLOCKED, exactly like an audience-wide one (CORE-VSEAL-001).
            locked: false,
            // An optional scheduled-reveal time (CORE-VSEAL-002), exactly like an audience-wide rule.
            scheduledRevealAt,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this rule belongs to exactly the given organization. Empty ids match nothing. This is
    /// the tenant-boundary check, checked before the workspace boundary: a rule belongs only to the
    /// organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this rule belongs to exactly the given workspace. Empty ids match nothing. This is
    /// the workspace-boundary check: a rule belongs only to the workspace it names, never to any
    /// other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this rule belongs to exactly the given session. Empty ids match nothing. This is the
    /// session-boundary check (checked after the organization and workspace boundaries): a reveal is
    /// session-scoped, so a rule belongs only to the session it names and never makes a resource visible
    /// in any other concurrent session of the same workspace (the cross-session leak, threat T5/T3).
    /// </summary>
    public bool BelongsToSession(Guid sessionId)
        => sessionId != Guid.Empty && SessionId == sessionId;

    /// <summary>
    /// Whether this rule governs exactly the given resource (type AND id). An empty id matches
    /// nothing. The resource-addressing check used by the later policy/command to find the rule for
    /// a resource.
    /// </summary>
    public bool Governs(VisibilityResourceType resourceType, Guid resourceId)
        => resourceId != Guid.Empty
            && ResourceType == resourceType
            && ResourceId == resourceId;

    /// <summary>
    /// Whether this rule is the one identified by the given id WITHIN the given organization and
    /// workspace. All three must match; an empty id matches nothing. A rule with the same id under a
    /// different tenant or workspace (which cannot happen for a single row but guards the caller's
    /// intent) returns <see langword="false"/>: the surrogate id is only ever honored inside its
    /// tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Whether the governed resource is currently visible to the audience (its
    /// <see cref="Visibility"/> is <see cref="VisibilityState.Visible"/>). This is the bare state
    /// read on the rule; deciding whether a SPECIFIC viewer may see the resource (combining the role,
    /// the workspace boundary and these rules) is <c>CanViewResource</c> (CORE-VIS-002), not this
    /// aggregate.
    /// </summary>
    public bool IsVisibleToAudience() => Visibility == VisibilityState.Visible;

    /// <summary>
    /// Whether this rule applies to the WHOLE audience (its <see cref="TargetParticipantId"/> is
    /// <see langword="null"/>) rather than a single selected participant (CORE-VIS-005).
    /// </summary>
    public bool IsAudienceWide => TargetParticipantId is null;

    /// <summary>
    /// Whether this rule is scoped to exactly the given participant. An empty id matches nothing, and
    /// an audience-wide rule (no target) targets no specific participant, so it returns
    /// <see langword="false"/> here (use <see cref="IsAudienceWide"/> for the audience-wide case).
    /// </summary>
    public bool TargetsParticipant(Guid participantId)
        => participantId != Guid.Empty && TargetParticipantId == participantId;

    /// <summary>
    /// Whether the governed resource is visible to the given participant by THIS rule: the rule must
    /// be Visible AND either audience-wide (visible to everyone) or scoped to exactly this participant.
    /// A Visible rule scoped to a DIFFERENT participant is NOT visible to this one — the
    /// selected-participant guarantee (threat T5). Deciding a participant's overall visibility across
    /// all of a resource's rules is <c>CanParticipantViewResource</c> (CORE-VIS-005), not this single
    /// rule.
    /// </summary>
    public bool IsVisibleTo(Guid participantId)
        => IsVisibleToAudience() && (IsAudienceWide || TargetsParticipant(participantId));

    /// <summary>
    /// Changes the base audience visibility state of the governed resource — the state-transition
    /// PRIMITIVE the later reveal command (CORE-VIS-004) builds on (analogous to
    /// <c>Session.Start</c>/<c>Session.End</c>). The tenant boundary, the workspace, the id and the
    /// governed resource are immutable here, so changing visibility never moves the rule to another
    /// organization, workspace or resource (threat T5); it changes only the visibility state and the
    /// update timestamp. An undefined state is rejected with NO mutation, leaving the prior state
    /// intact. This is the bare model transition only — authorization, idempotency and the
    /// append-only reveal event are the CORE-VIS-004 command, not this method.
    ///
    /// SEALED/LOCKED GATE (CORE-VSEAL-001): a <see cref="Locked"/> rule's visibility cannot be changed —
    /// the lock is the invariant that a sealed resource is permanently-restricted. The reveal/hide command
    /// resolves a locked rule and refuses BEFORE calling this method (returning the 409 outcome), so this
    /// guard is defence-in-depth: it makes the locked invariant unbypassable at the aggregate level. The
    /// state and timestamp are left intact when locked.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The new visibility state is not defined.</exception>
    /// <exception cref="InvalidOperationException">The rule is locked, so its visibility cannot change.</exception>
    public void ChangeVisibility(VisibilityState visibility, DateTimeOffset updatedAt)
    {
        if (!IsValidVisibility(visibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibility),
                visibility,
                "Visibility is not a defined visibility state.");
        }

        if (Locked)
        {
            throw new InvalidOperationException(
                "A locked (sealed) visibility rule's visibility cannot be changed.");
        }

        Visibility = visibility;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// SEALS (locks) this rule (CORE-VSEAL-001): asserts the server-side authoring lock that makes the
    /// resource permanently-restricted, so a later reveal/hide/visibility-change targeting it is rejected
    /// fail-closed. Idempotent: locking an already-locked rule is a no-op that returns <see langword="false"/>
    /// and bumps no timestamp; a real lock returns <see langword="true"/> and updates the timestamp. The
    /// binary <see cref="Visibility"/> state is left exactly as it was — the lock is orthogonal to it, never
    /// a third state. Only the authoring command (gated to Owner/Admin/Host/CoHost) calls this.
    /// </summary>
    /// <returns>Whether the lock state actually changed (it was previously unlocked).</returns>
    public bool Lock(DateTimeOffset updatedAt)
    {
        if (Locked)
        {
            return false;
        }

        Locked = true;
        UpdatedAt = updatedAt.ToUniversalTime();
        return true;
    }

    /// <summary>
    /// Clears the seal (unlocks) on this rule (CORE-VSEAL-001): removes the authoring lock so the rule's
    /// visibility may be changed again, behaving exactly as a never-locked rule. Idempotent: unlocking an
    /// already-unlocked rule is a no-op that returns <see langword="false"/> and bumps no timestamp; a real
    /// unlock returns <see langword="true"/> and updates the timestamp. The binary <see cref="Visibility"/>
    /// state is left exactly as it was. Only the authoring command (gated to Owner/Admin/Host/CoHost) calls
    /// this.
    /// </summary>
    /// <returns>Whether the lock state actually changed (it was previously locked).</returns>
    public bool Unlock(DateTimeOffset updatedAt)
    {
        if (!Locked)
        {
            return false;
        }

        Locked = false;
        UpdatedAt = updatedAt.ToUniversalTime();
        return true;
    }

    /// <summary>
    /// Whether the given value is a defined <see cref="VisibilityResourceType"/>. Used to reject
    /// undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidResourceType(VisibilityResourceType resourceType)
        => Enum.IsDefined(resourceType);

    /// <summary>
    /// Whether the given value is a defined <see cref="VisibilityState"/>. Used to reject undefined
    /// enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidVisibility(VisibilityState visibility) => Enum.IsDefined(visibility);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: rule id, organization id,
    /// workspace id, resource type, resource id and visibility state. Every field is an
    /// identifier/enum, never free-form content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs
    /// carry identifiers, not sensitive content).
    /// </summary>
    public override string ToString()
        => $"VisibilityRule {Id} org={OrganizationId} ws={WorkspaceId} session={SessionId} "
            + $"resource={ResourceType}:{ResourceId} visibility={Visibility} locked={Locked} "
            + $"scheduledRevealAt={(ScheduledRevealAt is { } scheduled ? scheduled.ToString("O") : "none")} "
            + $"target={(TargetParticipantId is { } target ? target.ToString() : "audience")}";
}
