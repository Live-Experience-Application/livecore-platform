# Authorization Matrix

Roles are generic. Verticals may rename them in UI.

| Action | Owner | Admin | Host | CoHost | Participant | Observer | Auditor |
|---|---:|---:|---:|---:|---:|---:|---:|
| View workspace metadata | yes | yes | yes | yes | limited | limited | yes |
| Manage workspace settings | yes | yes | no | no | no | no | no |
| Manage members | yes | yes | limited | no | no | no | no |
| Create session | yes | yes | yes | yes | no | no | no |
| Start/end session | yes | yes | yes | yes | no | no | no |
| Create scene | yes | yes | yes | yes | no | no | no |
| View host-only content | yes | yes | yes | yes | no | no | audit-only |
| View participant-visible content | yes | yes | yes | yes | if visible | if visible | audit-only |
| Change visibility rule | yes | yes | yes | yes | no | no | no |
| Execute reveal | yes | yes | yes | yes | no | no | no |
| Send private content | yes | yes | yes | yes | no | no | no |
| View own participant feed | no | no | no | no | yes | no | no |
| Resolve own session participant context | own | own | own | own | own | own | own |
| View observer feed | yes | yes | yes | yes | no | yes | no |
| View audit log | yes | yes | optional | no | no | no | yes |
| Export workspace | yes | yes | optional | no | no | no | optional |
| Delete workspace | yes | optional | no | no | no | no | no |
| Erase data subject personal data | yes | yes | no | no | no | no | no |
| Export data subject personal data | yes | yes | own | own | own | own | own |
| Delete organization | yes | no | no | no | no | no | no |

## Authorization principles

- The participant-visible-feed route (`GET /api/v1/participants/{participantId}/visible-feed`) is authorized to the participant who OWNS the feed ("View own participant feed" above, participant only) OR a Host/CoHost PREVIEWING it (preview-as-participant — `docs/05_MODULE_CONTRACTS.md` gives the Visibility module "preview-as-participant"; `csv/authorization_matrix.csv` grants Host and CoHost `preview` for the visible feed, and `csv/api_routes.csv` lists the route roles as "Participant owner or Host"). Owner/Admin/Observer/Auditor have no access to a participant's feed unless they are its owner or a Host/CoHost of its workspace.
- A participant resolving its OWN session participant context (`GET /api/v1/sessions/{sessionId}/me`, CORE-PSELF-001, the "Resolve own session participant context" row above) is a self-service read: it returns the CALLER'S OWN participant identity for the session — its `participantId`, `displayName` and presence — so an audience surface can call the participant-keyed reads (`getParticipantVisibleFeed`, the roster) for itself without the host passing the surrogate `participantId` out of band. The caller's participant is resolved ENTIRELY server-side from the authenticated principal via the existing principal-to-participant mapping (`IParticipantRepository.FindByUserAsync`, scoped by the resolved tenant and the session's own workspace), NEVER a client-supplied id, so a caller can only ever resolve ITSELF and never address another participant (threats T1/T5). The response carries only the caller's own identity, never another participant and no host-only field (the participant's user-account link is absent; threats T2/T7). It is authorized within a tenant (the tenant context resolver proved token-claim AND organization membership) and is fail-closed and HIDDEN-404: a caller who is not a participant of the session (a tenant member who never joined as a participant), a removed participant, a foreign-tenant caller and an unknown session are all an indistinguishable `404`, never `403`. The "own" cells mean the read is keyed to the caller's own participant regardless of workspace role — a Host who is also a participant resolves only their own context, exactly as a Participant does. Separately, the AUDIENCE roster projection (`GET /api/v1/sessions/{sessionId}/roster`) carries a server-computed `isSelf` marker, true only for the caller's own entry; it is the audience-safe counterpart of the host view's host-only participant user link and leaks no other participant's user id (it is derived only from the caller's own server-resolved participant id).
- Role checks are not enough; object-level authorization is required.
- Organization boundary must be checked before workspace boundary.
- Workspace boundary must be checked before resource-level visibility.
- Participant visibility is computed server-side.
- Audit roles may view metadata but should not automatically view sensitive content unless explicitly allowed.
- A membership or role **revocation takes effect within a bounded, documented window across every API replica**
  (CORE-RES-007, the "Multi-Instance Runtime Correctness" epic). The authorization-lookup cache (CORE-PERF-003) is a
  per-process `IMemoryCache`, so a revocation handled on one replica is reflected there immediately but, without
  propagation, lingers on the other replicas until their cached entry expires. Two mechanisms bound that window, and
  neither can ever widen access (the cache is **positive-only** and never serves a denial from cache, so a removed
  member is always re-evaluated against the database once their cached grant is gone):
  - **Cross-instance invalidation over the backplane (the default when configured).** When a Valkey/Redis backplane
    is configured (the same `Realtime:Backplane:*` connection the realtime scale-out uses), each invalidation is
    broadcast to every replica, which evicts the affected subject/organization group from its own cache — so a
    revocation takes effect across all replicas within a near-immediate window (the next request after the broadcast
    is applied). The broadcast is best-effort: if it is lost, the replicas fall back to the TTL backstop below — a
    smaller window, never a stale serve forever (**no new fail-open path**).
  - **The TTL backstop (the documented eventual-consistency window).** The cache's absolute TTL
    (`AuthorizationCache:Ttl`, **default 10s**, configurable) bounds how long a revocation can linger on a replica
    that did not handle it and did not receive the broadcast (including a single-instance deployment, which has no
    peers). It is the **documented operational caveat**: a revocation is guaranteed to take effect everywhere within
    the TTL even with no backplane, and configuring a backplane shrinks the typical window to near-immediate. See
    docs/13_SELF_HOSTING_REQUIREMENTS.md ("Cross-instance authorization-cache invalidation").
- Erasing a data subject's personal data (the right to erasure, `DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data`, CORE-PRIV-001) is a member-management privilege restricted to Owner/Admin, exactly like removing a member. It is authorized within a tenant (the caller must be an Owner/Admin of the resolved organization and the target a member of it), but because Core's user profile is a single global identity its effect is global: the subject's profile is deleted and their per-tenant personal-data copies are anonymized everywhere (GDPR Art.17). The sole Owner of an organization cannot be erased (an ownerless tenant would be permanently unreachable); a non-privileged member is denied `403` and a foreign-tenant/unknown member is hidden as `404` (fail-closed).
- Obtaining a data subject's personal-data export (the right of access and to portability, `GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export`, CORE-PRIV-004, GDPR Art.15/20) is the read-side counterpart of erasure and is DISTINCT from the session/workspace Exports feature (which exports content artifacts, not a subject's personal data). It discloses the documented personal-data set — the subject's identity profile plus their organization membership, workspace memberships, participant records and the invitations addressed to their email — gathered TENANT-SCOPED for the resolved organization, so an Owner/Admin exporting on the subject's behalf never learns of the subject's activity in a tenant they do not control and the subject reaches their data in other tenants only through those tenants' own export routes (threat T5). Because the export DISCLOSES personal data, the PII is delivered ONLY to the entitled recipient: the data subject THEMSELVES (self-service access — the row's `own` cells: any member may export their OWN personal data regardless of role) OR an Owner/Admin acting on their behalf (the tenant's data controller, which GDPR permits — the row's `yes` cells). The caller is a known member of the resolved tenant (the tenant context resolver proved token-claim AND membership); ANY other tenant member — a Host/CoHost/Participant/Observer/Auditor who is neither the subject nor an Owner/Admin — is denied `403`, and a foreign-tenant/unknown member is hidden as `404` (fail-closed). The access is recorded as a `PersonalDataExported` audit fact BY ID only (actor + exported subject id, in the tenant); the disclosed PII lives only in the export response, never in the audit log, so the PII-free per-tenant hash chain stays intact and the audit row survives a later erasure of the same subject.
- Deleting an organization (tenant offboarding / data deletion, `DELETE /api/v1/organizations/{organizationSlug}`, CORE-PRIV-002) is the most destructive tenant action, so it is **Owner-only** — strictly narrower than member management or erasure (which are Owner/Admin). It tears the whole tenant down through the schema's existing `ON DELETE CASCADE` foreign keys (the tenant's workspaces, sessions, participants, memberships and its own audit log are removed; the audit log is intentionally part of the teardown). It is authorized within a tenant (the caller must be an Owner of the resolved organization), fail-closed: an Admin or any other non-Owner member is denied `403`, and a foreign-tenant/unknown organization is hidden as `404`. Because the deleted tenant's own audit log is cascade-removed, the offboarding itself is recorded as a **platform-level** `OrganizationDeleted` audit fact (a null organization, outside the per-tenant hash chain) so the security record survives the teardown.
