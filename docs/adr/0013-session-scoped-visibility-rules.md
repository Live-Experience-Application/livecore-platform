# ADR 0013: Visibility Rules (Reveals) Are Session-Scoped

## Status

Accepted for initial implementation. First applied by CORE-SVIS-001 ("Scope visibility rules and
realtime fan-out to the session").

## Context

The Core lets a `Workspace` run more than one `Session` at a time — nothing in the model forbids two
concurrent sessions of the same workspace (`SessionRepository.AddAsync` has no uniqueness or
single-live constraint, and `session.active.max` is a quota, not a cap of one). A `Reveal` is the
command that makes a resource (a `Scene`/`ContentBlock`/`Entity`) visible to a session's audience, and
it is executed against a specific session (`POST /api/v1/sessions/{sessionId}/reveal`).

But the `VisibilityRule` that records a reveal carried only `OrganizationId`, `WorkspaceId`,
`ResourceType`, `ResourceId` and an optional `TargetParticipantId` — **no `SessionId`**. So every
visibility decision and the realtime recipient set were computed **workspace-wide**:

- `VisibilityPolicy` looked rules up by `(organization, workspace, resource)`, so a reveal in one
  session made the resource visible to **every** session of the workspace.
- The participant-visible feed (`VisibilityPreviewService`) and the entity-search audience filter
  reused that workspace-wide decision.
- `SessionEventRecipientResolver` fanned an audience event out to **all active participants of the
  workspace** (`IParticipantRepository.ListActiveByWorkspaceAsync`), not the session's audience.

The result was a **cross-session data leak** (the most severe finding of the release-readiness audit;
threats T5/T3 in `docs/07_SECURITY_THREAT_MODEL.md`): a reveal made in session A was visible to a
participant connected to a *different* concurrent session B of the same workspace — in B's
participant-visible feed, and (because the decision was workspace-wide) in any visibility-gated
delivery. A workspace running two simultaneous sessions for two disjoint audiences could not keep one
session's reveals out of the other.

Two ways to close the leak were considered:

- **(A) Reveals are session-scoped.** A reveal belongs to the session it was made in; "is this resource
  visible?" is only meaningful within a session. A reveal in A is, by construction, invisible in B.
- **(B) Model a workspace-shared resource explicitly.** Keep reveals workspace-wide and add an explicit
  opt-in for the (rare) resource that is meant to be shared across a workspace's sessions.

## Decision

**A reveal is SESSION-SCOPED.** `VisibilityRule` gains a required, immutable `SessionId`, and every
visibility decision and realtime recipient set on the per-session surfaces is bounded by it:

1. `VisibilityRule` carries `SessionId` (a real `session_id` column, a foreign key into `sessions(id)`,
   `ON DELETE CASCADE`, alongside the existing tenant and workspace foreign keys). The documented
   critical index becomes `visibility_rules(session_id, resource_type, resource_id)`.
2. The reveal/hide command (`RevealService`) reads, flips and creates rules **within the command's
   session**, so a reveal/hide acts only on its own session's rule.
3. `VisibilityPolicy` exposes a **session-scoped** `CanViewResource`/`CanParticipantViewResource` that
   consults only the rules of the supplied session — the single decision point all per-session surfaces
   use: the reveal command, the participant-visible feed (now requires a `sessionId`), the realtime
   recipient gate (`EventRecipientVisibility`, passing the event's `SessionId`) and reconnect replay.
4. The realtime recipient set is bounded by the event's session: every group the resolver emits is keyed
   by the event's `SessionId`, and every visibility gate is the session-scoped decision, so a reveal in
   one session can never be delivered into a concurrent session.

The visibility decision still lives in **exactly one place** — `VisibilityPolicy` — and is not forked:
the session-scoped and the workspace-wide entry points share one private decision core and differ only
in which rules they fetch (`docs/05_MODULE_CONTRACTS.md`: "Do not duplicate visibility logic
elsewhere").

### Why session-scoped, not workspace-shared-by-default

A reveal is an act *within a live run*: a host reveals a scene or a private clue to **this session's**
audience. Treating that as workspace-wide is the surprising, unsafe default — it leaks one run's reveals
into another. Making the reveal belong to its session is the least-surprising model, matches how the
reveal command is already addressed (`/sessions/{sessionId}/reveal`) and how the durable event stream is
already session-scoped (`session_events.session_id`), and closes the leak by construction rather than by
asking every reveal to opt out of sharing. A genuinely workspace-shared resource (option B) is a
narrower, additive future capability that, if ever required, can be modeled explicitly on top of the
session-scoped default; it needs its own ADR and human approval.

### Scope boundary (what this decision deliberately does NOT change)

- **Asset download** (`AssetDownloadPolicy`, `GET /api/v1/assets/{assetId}/download-url`) and
  **entity search** (`EntitySearchService`) are ROLE-level, workspace operations whose routes carry no
  session. They keep using a **workspace-wide, session-agnostic** visibility check (an explicit
  `VisibilityPolicy` overload), preserving their pre-existing behavior. Making those surfaces
  session-scoped is a follow-up, not part of this decision.

  **Update (CORE-SVIS-003):** the **participant** path of asset download is now **session-scoped**. The
  signed download route accepts an optional `?sessionId=` query parameter (required for a `Participant`
  caller), and a participant's download is authorized against the **session-scoped** per-participant
  visibility of the linked resource (`AssetDownloadPolicy.CanParticipantDownloadAsync` over the
  session-scoped `VisibilityPolicy.CanParticipantViewResource` — the same primitive the participant feed
  uses, reused not forked), so a participant cannot obtain a download URL for an asset tied to a resource
  revealed only in a sibling session.

  **Update (CORE-SVIS-004) — the carve-out is now closed.** Both remaining session-agnostic consumers are
  migrated, so **every** visibility decision is session-scoped:
  - **Entity search** (`EntitySearchService`) now scopes its audience filter to a `sessionId`: a
    participant's entity search returns only the entities revealed **in the session it names**, never one
    revealed only in a sibling session, gated by the same session-scoped per-participant primitive the feed
    uses.
  - The **role-level** asset download — the non-participant audience role `Observer` — is now session-scoped
    too (`AssetDownloadPolicy.CanDownload` over the session-scoped `VisibilityPolicy.CanViewResource`): an
    Observer must supply the `sessionId` and may download only a target audience-wide visible **in that
    session**. Host-content roles still need no session (their access is session-agnostic).
  - With both consumers migrated, the **workspace-wide, session-agnostic overloads have been DELETED**
    (`VisibilityPolicy.CanViewResource`/`CanParticipantViewResource` no-session overloads and
    `VisibilityRuleRepository.ListByResourceAcrossSessionsAsync`), so a session-agnostic visibility decision
    is now **structurally impossible** — the leak class cannot be reintroduced (the build proves there are no
    remaining callers).
- There is still **no persisted session-participant roster** (a participant is workspace-scoped, and the
  participant connection metadata is deferred — see `SessionParticipantJoinService`). The realtime
  audience fan-out therefore still *enumerates* the workspace's active participants as the candidate set;
  the session boundary is enforced by the session-scoped visibility gate and the session-keyed delivery
  groups, not by a session roster. A persisted roster is a Presence-epic concern (CORE-PRS-001).
- A single, deduplicated rule per `(session, resource, dimension)` and insert-on-conflict handling is the
  follow-up CORE-SVIS-002; this ADR keeps the resource index **non-unique** so that story does not have
  to widen a uniqueness this one imposed.

## Consequences

- `VisibilityRule` can never be created without a session; the reveal/hide commands, the feed, the
  realtime gate and replay all carry and enforce the session boundary, checked **after** the organization
  and workspace boundaries.
- The participant-visible feed route gains a required `sessionId` query parameter; the route path,
  method and roles in `csv/api_routes.csv` are unchanged.
- A `session_id` migration (`AddVisibilityRuleSessionScope`) adds the column, the
  `(session_id, resource_type, resource_id)` index and the session foreign key; the optimistic
  concurrency token and the existing tenant/workspace cascades are unaffected.
- Any LLM-proposed change to this policy — reverting to workspace-wide reveals, or adding a
  workspace-shared resource model — requires a new ADR and human approval.
