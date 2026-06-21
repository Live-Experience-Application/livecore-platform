// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json.Serialization;

namespace LiveCore.Api.Audit;

/// <summary>
/// The kind of security-relevant action recorded by an <see cref="AuditLogEntry"/> in the append-only
/// audit log. The Audit module owns the "security event records" (docs/05_MODULE_CONTRACTS.md), and this
/// enum is the Core-level catalog of the generic actions those records capture. The names are the
/// generic, product-neutral event names from docs/09_EVENT_CATALOG.md (and, for the entitlement/store
/// actions added by CORE-SPEC-002, csv/entitlement_event_catalog.csv) — never vertical terms.
///
/// Serialized over HTTP by its stable NAME (the view-audit-log read endpoint, CORE-SEC-002), exactly as the
/// other Core enums surface as stable string names in their DTOs — the action is persisted by name too
/// (<see cref="AuditLogConfiguration"/>) and carried by name in <see cref="AuditLogEntryView"/>. The
/// converter is attached to the enum itself because this is the only enum surfaced directly (rather than
/// pre-mapped to a string in its DTO), and it is not serialized anywhere else, so the attribute has no
/// effect on any other endpoint. The numeric values stay storage discriminators only (see below).
///
/// CORE-VIS-006 seeded this catalog with the one action that epic produced,
/// <see cref="VisibilityRuleChanged"/> (the security-relevant visibility change of
/// docs/09_EVENT_CATALOG.md). CORE-AUD-001 — the generic append-only audit log — promotes the catalog to
/// a GENERIC one: the actions below name the product-neutral, security-relevant Core actions the log can
/// record through the generic <see cref="AuditLogEntry.Create"/> factory, not just visibility changes.
/// Each producer command wires its own action in its own story (exactly as the durable realtime events
/// are wired per command); cataloguing an action here is the generic audit contract, not a second
/// implementation of the command.
///
/// EXTENSIBLE WITHOUT A SCHEMA CHANGE: the action is persisted as a real string column by its stable
/// NAME, so adding a member never reshapes the table. The integer values are only in-memory storage
/// discriminators (persisted by name, like every other enum in the model — <c>VisibilityState</c>,
/// <c>SessionStatus</c>, <c>ContentBlockType</c>), carry no ordering meaning and must not be compared
/// with &gt;/&lt;.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditAction
{
    /// <summary>
    /// A resource's audience visibility rule changed — a host revealed a resource to the audience or to
    /// a selected participant (the Visibility module's reveal command, CORE-VIS-004/005). This is the
    /// security-relevant <c>VisibilityRuleChanged</c> event of docs/09_EVENT_CATALOG.md, recorded here
    /// as an append-only audit fact so every visibility change is auditable (the primary security
    /// promise of docs/07_SECURITY_THREAT_MODEL.md: "audit creation for visibility changes"). It is
    /// distinct from the durable realtime SESSION event of the same name that the Realtime epic
    /// (CORE-RT-003) will append to the <c>session_events</c> stream — that is event DELIVERY; this is
    /// the security AUDIT record.
    /// </summary>
    VisibilityRuleChanged = 1,

    /// <summary>
    /// A session moved from <c>Prepared</c> to <c>Live</c> — the Sessions module's start command, wired as
    /// a producer by CORE-EVT-001. The security-relevant <c>SessionStarted</c> event of
    /// docs/09_EVENT_CATALOG.md ("starts live timeline"). Like <see cref="SessionCancelled"/> it records a
    /// real STATE TRANSITION (the session survives), so the entry carries the before/after status NAMES
    /// (<c>Prepared</c> -&gt; <c>Live</c>), the session as the governed resource and the host who started
    /// it as the actor — a generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForSessionStart"/>).
    /// </summary>
    SessionStarted = 2,

    /// <summary>
    /// A session moved from <c>Live</c> to <c>Ended</c> — the Sessions module's end command, wired as a
    /// producer by CORE-EVT-001. The security-relevant <c>SessionEnded</c> event of
    /// docs/09_EVENT_CATALOG.md ("ends live timeline"). Like <see cref="SessionStarted"/> it records the
    /// before/after status NAMES (<c>Live</c> -&gt; <c>Ended</c>), the session as the governed resource and
    /// the host who ended it as the actor — a generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForSessionEnd"/>).
    /// </summary>
    SessionEnded = 3,

    /// <summary>
    /// A workspace member invitation was created — the Workspaces module's invite command. Auditing the
    /// invite is the threat model's stated control against invite abuse (threat T6 in
    /// docs/07_SECURITY_THREAT_MODEL.md lists "audit logs" among the controls). A generic,
    /// workspace-scoped audit fact: the actor is the host who issued the invite and there is no
    /// before/after visibility state. The invite TOKEN itself is never recorded (threats T6/T7) — only
    /// the fact that an invite was created.
    /// </summary>
    MemberInvited = 4,

    /// <summary>
    /// A workspace or organization member was removed, revoking the subject's access — the member removal
    /// command (CORE-LIFE-001). Auditing the removal is the threat model's stated control for access
    /// revocation: it records that an authorized admin took a member's standing away (threats T1/T6 in
    /// docs/07_SECURITY_THREAT_MODEL.md). A generic audit fact whose actor is the admin who performed the
    /// removal, whose resource is the removed membership (its generic kind name and surrogate id), and
    /// whose previous state records the role that was revoked; it is workspace-scoped for a workspace
    /// member removal and organization-level (no workspace) for an organization member removal. The
    /// removal is a deletion, not a transition to a new state, so the new state is null
    /// (<see cref="AuditLogEntry.ForMemberRemoval"/>).
    /// </summary>
    MemberRemoved = 5,

    /// <summary>
    /// A user redeemed a scoped workspace invitation and gained the granted workspace membership — the
    /// invitation accept/redeem command (CORE-WS-006). It is the counterpart of <see cref="MemberInvited"/>
    /// (the invite half, CORE-WS-004) and the inverse of <see cref="MemberRemoved"/>: where a removal
    /// records the role that was REVOKED, a join records the role that was GRANTED. Auditing the redemption
    /// is the threat model's stated control against invite abuse (threat T6 in
    /// docs/07_SECURITY_THREAT_MODEL.md lists "audit logs" among the invite-token controls): it records that
    /// a presented scoped token was redeemed and by whom (the bearer grant — whoever presented a valid token
    /// becomes the member). A generic, workspace-scoped audit fact whose actor is the authenticated caller
    /// who redeemed the token (NOT necessarily the invited email, which is data only), whose resource is the
    /// created membership (its generic kind name and surrogate id) and whose NEW state records the granted
    /// role — the access conferred. The invite TOKEN itself is never recorded (threats T6/T7), only the fact
    /// that a membership was granted by redemption (<see cref="AuditLogEntry.ForMemberJoined"/>).
    /// </summary>
    MemberJoined = 20,

    /// <summary>
    /// An Owner/Admin revoked a pending workspace invitation so its scoped token can never be redeemed — the
    /// invitation revoke command (CORE-WS-007). It is the take-back counterpart of <see cref="MemberInvited"/>
    /// (the invite half, CORE-WS-004) and pairs with the redemption path <see cref="MemberJoined"/>
    /// (CORE-WS-006): where a redemption consumes a token into a membership, a revocation retires the token
    /// before it is ever redeemed. Auditing the revocation is the threat model's stated control against invite
    /// abuse (threat T6 in docs/07_SECURITY_THREAT_MODEL.md lists "revocation" and "audit logs" among the
    /// invite-token controls): it records that an authorized admin took an outstanding invite back. Unlike the
    /// deletion actions — and exactly like <see cref="WorkspaceArchived"/> — a revoke is a real STATE TRANSITION
    /// (the invitation row survives so its audit history is preserved), so it records the before/after status
    /// NAMES (<c>Pending</c> -&gt; <c>Revoked</c>); its actor is the admin who revoked the invitation and its
    /// resource is the invitation itself (its generic kind name and surrogate id). A generic, workspace-scoped
    /// audit fact whose values are identifiers, an enum or a generic state name — never the invited email, the
    /// token or any free-form content (threats T6/T7) — recorded through
    /// <see cref="AuditLogEntry.ForMemberInvitationRevoked"/>.
    /// </summary>
    MemberInvitationRevoked = 21,

    /// <summary>
    /// An authoring role created a new entity in a workspace — the entity create command (CORE-ENT-006, the
    /// "Vertical Authoring and Read API Completeness" epic). Auditing the creation satisfies the story's
    /// "create … is tenant/workspace-scoped and audited" criterion: it records that an authorized authoring
    /// role added a workspace resource (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). It is the
    /// authoring counterpart of <see cref="EntityDeleted"/>: a creation is the birth of a resource, not a
    /// transition of an existing one, and an entity has no lifecycle state, so there is NO before/after state
    /// pair (both null). A generic, workspace-scoped audit fact whose actor is the authoring role who created
    /// the entity and whose resource is the created entity (its generic kind name and surrogate id); the
    /// entity's host-/template-supplied name and attribute values are NEVER recorded — only identifiers and
    /// the generic kind name (threat T7) (<see cref="AuditLogEntry.ForEntityCreation"/>).
    /// </summary>
    EntityCreated = 22,

    /// <summary>
    /// An authoring role defined a new entity TYPE in a workspace — the entity-type create command
    /// (CORE-ENT-007, the "Vertical Authoring and Read API Completeness" epic). It is the type-definition
    /// counterpart of <see cref="EntityCreated"/>: an entity type is the generic, DATA-DRIVEN definition of a
    /// kind of entity (its template key plus field/type metadata) through which a vertical maps its domain onto
    /// Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). Auditing the definition satisfies the
    /// story's "tenant/workspace-scoped; audited" criterion: it records that an authorized authoring role added
    /// a workspace type definition (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). A definition is the
    /// birth of a resource, not a transition of an existing one, and an entity type has no lifecycle state, so
    /// there is NO before/after state pair (both null). A generic, workspace-scoped audit fact whose actor is
    /// the authoring role who defined the type and whose resource is the created type (its generic kind name
    /// and surrogate id); the type's host-/template-supplied key, display name and attribute schema are NEVER
    /// recorded — only identifiers and the generic kind name (threat T7)
    /// (<see cref="AuditLogEntry.ForEntityTypeCreation"/>).
    /// </summary>
    EntityTypeCreated = 23,

    /// <summary>
    /// An Owner/Admin created a new organization-scoped template definition — the template create command
    /// (CORE-TMPL-001, the "Vertical Authoring and Read API Completeness" epic). It is the registry-level
    /// counterpart of <see cref="EntityTypeCreated"/>: a <c>Template</c> is reusable vertical scaffolding stored
    /// entirely as DATA (its template key plus the entity types it defines), through which a vertical maps its
    /// domain onto Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). Auditing the creation satisfies
    /// the story's "audited" criterion: it records that an authorized admin added a tenant template (threats
    /// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). A creation is the birth of a resource, not a transition of an
    /// existing one, and a template has no lifecycle state, so there is NO before/after state pair (both null).
    /// A template is an ORGANIZATION-level registry resource (not workspace content), so the fact is
    /// organization-level: it carries the tenant and records NO workspace (unlike the workspace-scoped entity /
    /// entity-type creations). A generic audit fact whose actor is the admin who created the template and whose
    /// resource is the created template (its generic kind name and surrogate id); the template's
    /// host-/template-supplied key and definition content are NEVER recorded — only identifiers and the generic
    /// kind name (threat T7) (<see cref="AuditLogEntry.ForTemplateCreation"/>).
    /// </summary>
    TemplateCreated = 24,

    /// <summary>
    /// A host deleted an entity, removing it and its dependent edges, visibility rules and asset links —
    /// the entity deletion command (CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic). Auditing
    /// the deletion satisfies the story's "deletion is authorized and audited" criterion: it records that
    /// an authorized host removed a workspace resource (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
    /// A generic, workspace-scoped audit fact whose actor is the host who deleted the entity and whose
    /// resource is the deleted entity (its generic kind name and surrogate id). The deletion is a removal,
    /// not a transition to a new state, so there is no before/after state pair (the entity has no lifecycle
    /// state); the dependent rows the deletion cascades are consequences of the same action and are not
    /// separately audited, exactly as the member-removal action records one fact for the removal it
    /// performs (<see cref="AuditLogEntry.ForEntityDeletion"/>).
    /// </summary>
    EntityDeleted = 6,

    /// <summary>
    /// A host deleted a content block from a scene, removing it (together with its revision history, which
    /// lives inline on the row) and its dependent visibility rules and asset links — the content block
    /// deletion command (CORE-LIFE-004, the "Resource Lifecycle and Deletion" epic). Recording the deletion
    /// keeps a content-block removal auditable exactly as an entity removal is, the consistent application of
    /// docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion implementations follow
    /// this decision: cascade the dependents, in the application, inside one transaction, and audit the
    /// deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the content
    /// block and whose resource is the deleted content block (its generic kind name and surrogate id). The
    /// deletion is a removal, not a transition to a new state, so there is no before/after state pair (a
    /// content block has no lifecycle state); the dependent rows the deletion cascades are consequences of
    /// the same action and are not separately audited, exactly as <see cref="EntityDeleted"/> records one
    /// fact for the deletion it performs (<see cref="AuditLogEntry.ForContentBlockDeletion"/>).
    /// </summary>
    ContentBlockDeleted = 7,

    /// <summary>
    /// A host deleted a scene from a workspace, removing it together with its child content blocks (and
    /// their inline revision history) and every dependent visibility rule and asset link — the scene
    /// deletion command (CORE-LIFE-005, the "Resource Lifecycle and Deletion" epic). After the deletion the
    /// remaining scenes re-pack their ordering so there is no gap; the re-pack is part of the same action and
    /// is not separately audited. Recording the deletion keeps a scene removal auditable exactly as an entity
    /// or content-block removal is, the consistent application of
    /// docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion implementations follow
    /// this decision: cascade the dependents, in the application, inside one transaction, and audit the
    /// deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the scene and
    /// whose resource is the deleted scene (its generic kind name and surrogate id). The deletion is a
    /// removal, not a transition to a new state, so there is no before/after state pair (a scene has no
    /// lifecycle state); the dependent rows the deletion cascades and the order re-pack are consequences of
    /// the same action and are not separately audited, exactly as <see cref="EntityDeleted"/> and
    /// <see cref="ContentBlockDeleted"/> record one fact for the deletion they perform
    /// (<see cref="AuditLogEntry.ForSceneDeletion"/>).
    /// </summary>
    SceneDeleted = 8,

    /// <summary>
    /// A host deleted an asset, removing its links and the underlying storage object — the host-initiated
    /// asset deletion command (CORE-LIFE-006, the "Resource Lifecycle and Deletion" epic). Until now an
    /// available asset could be created and linked but never removed; this adds the inverse. Recording the
    /// deletion keeps an asset removal auditable exactly as an entity, content-block or scene removal is, the
    /// consistent application of docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion
    /// implementations follow this decision: cascade the dependents, in the application, inside one transaction,
    /// and audit the deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the
    /// asset and whose resource is the deleted asset (its generic kind name and surrogate id). The deletion is a
    /// removal, not a transition to a new state, so there is no before/after state pair (the asset's lifecycle
    /// status is irrelevant once it is gone); the asset's links the deletion cascades and the storage object the
    /// deletion removes are consequences of the same action and are not separately audited, exactly as
    /// <see cref="EntityDeleted"/>, <see cref="ContentBlockDeleted"/> and <see cref="SceneDeleted"/> record one
    /// fact for the deletion they perform (<see cref="AuditLogEntry.ForAssetDeletion"/>). The storage object key
    /// is never recorded — only identifiers, never a means to reach private content (threats T4/T7).
    /// </summary>
    AssetDeleted = 9,

    /// <summary>
    /// An owner archived a workspace, taking it out of active use — the workspace archive command
    /// (CORE-LIFE-009, the "Resource Lifecycle and Deletion" epic). Until now a workspace had create/read/update
    /// but no lifecycle end-state; this records the soft, terminal Active -&gt; Archived transition. Auditing the
    /// archive satisfies the story's "audited" criterion: it records that an authorized owner took a workspace
    /// read-only (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). Unlike the deletion actions, an archive is
    /// a real STATE TRANSITION (the workspace survives), so it records the before/after status NAMES
    /// (<c>Active</c> -&gt; <c>Archived</c>) exactly like <see cref="VisibilityRuleChanged"/> records a
    /// visibility transition; its actor is the owner who archived the workspace and its resource is the workspace
    /// itself (its generic kind name and surrogate id). A generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForWorkspaceArchive"/>).
    /// </summary>
    WorkspaceArchived = 10,

    /// <summary>
    /// A host cancelled a not-yet-started session, taking it out of use before it ever ran — the session
    /// cancel command (CORE-LIFE-010, the "Resource Lifecycle and Deletion" epic). Until now a session could
    /// be created, started and ended but never cancelled; this records the soft, terminal Prepared -&gt;
    /// Cancelled transition. Auditing the cancel satisfies the story's "authorized" criterion: it records that
    /// an authorized host took a session out of use (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
    /// Unlike the deletion actions — and exactly like <see cref="WorkspaceArchived"/> — a cancel is a real
    /// STATE TRANSITION (the session row survives so its append-only <c>session_events</c> and audit history is
    /// preserved), so it records the before/after status NAMES (<c>Prepared</c> -&gt; <c>Cancelled</c>) like
    /// <see cref="VisibilityRuleChanged"/> records a visibility transition; its actor is the host who cancelled
    /// the session and its resource is the session itself (its generic kind name and surrogate id). A generic,
    /// workspace-scoped audit fact (<see cref="AuditLogEntry.ForSessionCancellation"/>).
    /// </summary>
    SessionCancelled = 11,

    // --- Entitlement / store / purchase actions (CORE-SPEC-002) -----------------
    //
    // The actions below back the entitlement/store domain events that csv/entitlement_event_catalog.csv marks
    // audit=true, so that catalog is a CONTRACT, not aspirational (CORE-SPEC-002). Each is emitted as a real
    // audit fact on the action it names, and scripts/spec-consistency.ps1 now binds every audit=true catalog
    // event to a member here (both directions), so a catalog claim with no backing action — or a backing action
    // with no catalog row — fails CI.
    //
    // PLATFORM-SCOPED vs TENANT-SCOPED. A purchase and the entitlement it grants are DEPLOYMENT-SPANNING, not
    // tenant-scoped: a user's premium state follows the user's purchase, not an organization, and a purchase is
    // named globally by its (provider, provider transaction id) pair with no organization_id
    // (EntitlementSubjectType.User; the store endpoints; docs/21). So the grant/revoke, the purchase
    // verification and the store-notification actions are recorded as PLATFORM-LEVEL audit facts (a null
    // organization, AuditLogEntry.OrganizationId), distinct from the tenant-scoped facts above. QuotaExceeded is
    // the exception: a quota is denied inside an already tenant-scoped, authenticated command, so it is a normal
    // tenant fact carrying its organization and actor.

    /// <summary>
    /// A verified, buyer-linked purchase granted the buyer the mapped <c>SubjectEntitlement</c> — the
    /// purchase → plan → entitlement grant chain (CORE-MON-003, emitted by
    /// <see cref="LiveCore.Api.Entitlements.ProductEntitlementGrantService"/>). The catalog's
    /// <c>EntitlementGranted</c> event (csv/entitlement_event_catalog.csv). A PLATFORM-level audit fact: the
    /// grant is deployment-spanning (a user subject is not tenant-scoped), so it carries a null organization and,
    /// being system-initiated by a verified purchase, no actor; its resource is the granted subject (the generic
    /// subject-kind name and id) — <see cref="AuditLogEntry.ForEntitlementGranted"/>. No receipt, proof or
    /// content is ever recorded (threat T7).
    /// </summary>
    EntitlementGranted = 12,

    /// <summary>
    /// A refund, cancellation or chargeback revoked the entitlement a purchase had granted — the inverse of the
    /// grant chain (CORE-MON-004, emitted by
    /// <see cref="LiveCore.Api.Entitlements.ProductEntitlementGrantService"/> through the store-notification
    /// revocation path). The catalog's <c>EntitlementRevoked</c> event. Like
    /// <see cref="EntitlementGranted"/> a PLATFORM-level, system-initiated audit fact (null organization, no
    /// actor) whose resource is the affected subject (<see cref="AuditLogEntry.ForEntitlementRevoked"/>).
    /// </summary>
    EntitlementRevoked = 13,

    /// <summary>
    /// A protected, server-enforced command was REFUSED because it would exceed the subject's quota limit
    /// (CORE-ENTL-004/CORE-MON-005/006, emitted at the quota-denial sites:
    /// <c>QuotaEnforcementService</c> callers — workspace create, session start, participant join and asset
    /// upload-intent). The catalog's <c>QuotaExceeded</c> event. Unlike the other entitlement/store actions a
    /// quota is denied INSIDE an already authenticated, tenant-scoped command, so this is a normal TENANT-scoped
    /// audit fact carrying its organization, the denied caller as the actor and the quota subject as the resource
    /// (<see cref="AuditLogEntry.ForQuotaExceeded"/>). It records only the generic quota key and the subject —
    /// never any caller-supplied content (threat T7).
    /// </summary>
    QuotaExceeded = 14,

    /// <summary>
    /// A client submitted an Apple/Google purchase proof for server-side verification — the catalog's
    /// <c>PurchaseVerificationSubmitted</c> event, emitted by the Apple/Google verification endpoints once the
    /// caller is authorized and the request validated, before the proof is verified. A PLATFORM-level audit fact
    /// (null organization) whose actor is the authenticated buyer; it records the provider only, NEVER the proof
    /// or receipt content (threat T7). <see cref="AuditLogEntry.ForPurchaseVerification"/>.
    /// </summary>
    PurchaseVerificationSubmitted = 15,

    /// <summary>
    /// A submitted purchase proof was verified server-side as a genuine purchase and recorded — the catalog's
    /// <c>PurchaseVerificationSucceeded</c> event, emitted by the Apple/Google verification endpoints after the
    /// adapter verifies the proof, the environment is honored and the purchase is recorded. A PLATFORM-level
    /// audit fact (null organization) whose actor is the buyer and whose resource is the recorded purchase
    /// transaction (<see cref="AuditLogEntry.ForPurchaseVerification"/>). No proof or receipt content (threat T7).
    /// </summary>
    PurchaseVerificationSucceeded = 16,

    /// <summary>
    /// A submitted purchase proof was NOT verified as a genuine grantable purchase, so nothing was recorded or
    /// granted (fail-closed) — the catalog's <c>PurchaseVerificationFailed</c> event, emitted by the
    /// Apple/Google verification endpoints on a rejected proof or an environment the deployment does not honor.
    /// A PLATFORM-level audit fact (null organization) whose actor is the buyer; it records the provider only,
    /// never the proof, the rejection detail or any receipt content (threat T7).
    /// <see cref="AuditLogEntry.ForPurchaseVerification"/>.
    /// </summary>
    PurchaseVerificationFailed = 17,

    /// <summary>
    /// An Apple/Google store server notification was received for handling — the catalog's
    /// <c>StoreNotificationReceived</c> event, emitted by <see cref="LiveCore.Api.Store.StoreNotificationService"/>
    /// on the first arrival of a validated, normalized notification (before its effect is applied). A
    /// PLATFORM-level, system audit fact (null organization, no actor); it records the provider and notification
    /// type only, never the payload or receipt content (threat T7).
    /// <see cref="AuditLogEntry.ForStoreNotificationReceived"/>.
    /// </summary>
    StoreNotificationReceived = 18,

    /// <summary>
    /// A received store notification was idempotently applied to the affected purchase's lifecycle — the
    /// catalog's <c>StoreNotificationProcessed</c> event, emitted by
    /// <see cref="LiveCore.Api.Store.StoreNotificationService"/> once a notification's effect (the purchase status
    /// change and its dedup-ledger row, CORE-MON-010) is committed. A PLATFORM-level, system audit fact (null
    /// organization, no actor) whose new-state records the applied outcome; it records the provider and
    /// notification type only, never the payload or receipt content (threat T7).
    /// <see cref="AuditLogEntry.ForStoreNotificationProcessed"/>.
    /// </summary>
    StoreNotificationProcessed = 19,

    /// <summary>
    /// A data subject's personal data was erased on an authorized request — the data-subject erasure command
    /// (CORE-PRIV-001, the "Privacy and Data Lifecycle" epic, GDPR Art.17 "right to erasure"). An authorized
    /// Owner/Admin erased a data subject (a member of the tenant): the subject's user profile (its OIDC subject,
    /// email and display name) is removed, their participant display data and any invited-email rows are
    /// anonymized, and the export/asset records they created survive anonymized via the schema's
    /// <c>ON DELETE SET NULL</c> foreign keys. Recording the erasure satisfies the story's "audited by id only"
    /// criterion: it records that an authorized admin exercised the right to erasure (threats T1/T5 in
    /// docs/07_SECURITY_THREAT_MODEL.md). An erasure is a removal, not a transition to a new state, so there is
    /// no before/after state pair; it is organization-level (no workspace), its actor is the admin who performed
    /// the erasure and its resource is the erased subject (its generic kind name <c>UserProfile</c> and the
    /// surrogate id). The audit row carries ONLY identifiers — never the erased subject's email, display name or
    /// OIDC subject — and outlives the now-deleted user profile it references because the reference is a recorded
    /// fact, not a foreign key (the PII-free audit chain is precisely what makes erasure reconcilable with the
    /// immutable audit log; threats T1/T5/T7) (<see cref="AuditLogEntry.ForUserProfileErasure"/>).
    /// </summary>
    UserProfileErased = 25,

    /// <summary>
    /// An authorized Owner deleted an organization, tearing the whole tenant down — the tenant organization
    /// deletion command (CORE-PRIV-002, the "Privacy and Data Lifecycle" epic, tenant offboarding / data
    /// deletion). The deletion removes the organization root and, through the schema's <c>ON DELETE CASCADE</c>
    /// foreign keys into <c>organizations(id)</c>, the tenant's workspaces, sessions, participants, memberships
    /// and its OWN audit log (the audit log is intentionally part of the tenant teardown). Recording the deletion
    /// satisfies the story's "authorized, guarded and fail-closed" intent: it records that an authorized Owner
    /// offboarded a tenant (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). A deletion is a removal, not a
    /// transition to a new state, and an organization has no lifecycle state, so there is NO before/after state
    /// pair; its actor is the Owner who performed the deletion and its resource is the deleted organization (its
    /// generic kind name <c>Organization</c> and the surrogate id). Unlike the tenant-scoped actions above this is
    /// a PLATFORM-LEVEL audit fact (a null organization, exactly like the entitlement/store facts): a tenant-scoped
    /// entry would be cascade-removed with the very tenant being torn down, so the offboarding record is recorded
    /// at the platform level — outside the per-tenant hash chain — so it SURVIVES the teardown. The audit row
    /// carries ONLY identifiers — never the deleted tenant's name or any free-form content (threat T7) — and
    /// outlives the now-deleted organization it references because the reference is a recorded fact, not a foreign
    /// key (<see cref="AuditLogEntry.ForOrganizationDeletion"/>).
    /// </summary>
    OrganizationDeleted = 26,

    /// <summary>
    /// A configurable data-retention sweep purged a terminal, past-its-window personal-data-bearing record — the
    /// data-retention sweeps job (CORE-PRIV-003, the "Privacy and Data Lifecycle" epic, GDPR Art.5(1)(e) storage
    /// limitation). The worker's retention sweep expires and purges terminal/old records that carry personal data:
    /// completed/expired sessions (and their cascade-removed session events, recaps and session-scoped visibility
    /// rules), generated recaps, completed export artifacts (the row and any object-storage blob) and
    /// closed/expired/revoked invitations (their plaintext invited email). Recording the purge satisfies the
    /// story's "purges are audited by id" criterion: it records WHICH record of WHICH generic kind was purged in
    /// WHICH tenant/workspace. It is a SYSTEM action (no user actor), so the actor is null, and a purge is a
    /// removal rather than a transition, so there is NO before/after state pair. The resource kind is passed as a
    /// generic NAME string (e.g. <c>Session</c> / <c>Recap</c> / <c>ExportJob</c> / <c>WorkspaceInvitation</c>) so
    /// the Audit module does not depend on the owning modules' types. The audit row carries ONLY identifiers —
    /// never the purged record's content, the recap body, the export blob or the invited email (threat T7) — and
    /// outlives the now-deleted record it references because the reference is a recorded fact, not a foreign key
    /// (the same posture that makes erasure/deletion reconcilable with the immutable audit log; threats T1/T5/T7).
    /// A tenant-scoped fact (it carries the tenant and workspace), recorded through
    /// <see cref="AuditLogEntry.ForRetentionPurge"/>.
    /// </summary>
    RecordRetentionPurged = 27,

    /// <summary>
    /// A data subject's personal data was disclosed in a machine-readable access/portability export on an
    /// authorized request — the data-subject access and portability export command (CORE-PRIV-004, the "Privacy
    /// and Data Lifecycle" epic, GDPR Art.15 access / Art.20 portability). It is the access counterpart of
    /// <see cref="UserProfileErased"/> (the erasure half, CORE-PRIV-001): where an erasure REMOVES the subject's
    /// personal data, an export READS it back to the entitled recipient (the data subject themselves, or an
    /// Owner/Admin acting on their behalf). The export is distinct from the session/workspace Exports feature
    /// (<see cref="AuditAction"/> has no Exports action; that is a content artifact, not a personal-data
    /// disclosure). Disclosing personal data is security-relevant, so the access is recorded as an append-only
    /// fact: it records WHO obtained the export (the actor) and WHOSE personal data was disclosed (the subject,
    /// by id), in WHICH tenant. An export is an access, not a transition or a removal, so there is NO before/after
    /// state pair; it is ORGANIZATION-level (no workspace), because the export is authorized within a tenant even
    /// though the disclosed profile is a global identity, mirroring how <see cref="UserProfileErased"/> records an
    /// organization-level erasure. The resource kind is passed as a generic NAME string (<c>UserProfile</c>) so
    /// the Audit module does not depend on the IdentityAccess module's types. The audit row carries ONLY
    /// identifiers — never the disclosed email, display names, invited emails or any of the exported personal data
    /// itself (threats T1/T5/T7): the PII lives only in the export RESPONSE delivered to the entitled subject, not
    /// in the audit log (<see cref="AuditLogEntry.ForPersonalDataExport"/>).
    /// </summary>
    PersonalDataExported = 28,

    /// <summary>
    /// A host confirmed a successful upload, moving an asset <c>Pending</c> -&gt; <c>Available</c> so it
    /// becomes downloadable — the asset confirm-upload command (CORE-ALC-001, the "Asset Lifecycle and
    /// Attachment Completeness" epic). It is the lifecycle counterpart of <see cref="AssetDeleted"/>: where a
    /// deletion removes an asset, a confirmation completes its upload. Until this story the
    /// <see cref="LiveCore.Api.Assets.Asset.MarkAvailable"/> domain transition was wired to no transport, so an
    /// asset could never leave <c>Pending</c> except via the background cleanup that reclaims it. Auditing the
    /// confirmation satisfies the story's "the transition is audited" criterion: it records that an authorized
    /// host completed an upload (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). Unlike the deletion actions
    /// — and exactly like <see cref="WorkspaceArchived"/> and <see cref="SessionStarted"/> — a confirmation is a
    /// real STATE TRANSITION (the asset survives), so it records the before/after status NAMES
    /// (<c>Pending</c> -&gt; <c>Available</c>); its actor is the host who confirmed the upload and its resource
    /// is the asset itself (its generic kind name and surrogate id). A generic, workspace-scoped audit fact whose
    /// values are identifiers, an enum or a generic state name — never the uploaded checksum or any storage
    /// coordinate (threats T4/T7) (<see cref="AuditLogEntry.ForAssetConfirmation"/>).
    /// </summary>
    AssetConfirmed = 29,

    /// <summary>
    /// An authoring role SEALED (locked) or unsealed (unlocked) a visibility rule — the visibility-rule lock
    /// command (CORE-VSEAL-001, the "Scheduled and Sealed Visibility" epic, raised by a vertical adopter,
    /// ARC-GAP-002). A sealed rule expresses a permanently-restricted resource whose audience visibility can
    /// no longer be changed or revealed (a reveal/hide/change targeting it is 409, fail-closed); clearing the
    /// seal restores it. Auditing the change satisfies the story's "the authoring roles set and clear the
    /// lock" being a security-relevant, server-asserted fact (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
    /// It is DISTINCT from <see cref="VisibilityRuleChanged"/>: that records a Hidden/Visible state transition,
    /// while this records the ORTHOGONAL lock transition and never moves the binary visibility state. Like
    /// <see cref="WorkspaceArchived"/> it is a real STATE TRANSITION (the rule survives), so it records the
    /// before/after lock-state NAMES (<c>Unlocked</c> -&gt; <c>Locked</c>, or the inverse); its actor is the
    /// authoring role who changed the lock, its resource is the governed resource the rule controls (its
    /// generic kind name and surrogate id) and it carries the optional selected-participant target. A generic,
    /// workspace-scoped audit fact whose values are identifiers, an enum or a generic state name — never any
    /// resolved content (threat T7) (<see cref="AuditLogEntry.ForVisibilityRuleLockChange"/>).
    /// </summary>
    VisibilityRuleLockChanged = 30,
}
