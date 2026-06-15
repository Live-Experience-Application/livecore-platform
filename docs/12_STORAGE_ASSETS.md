# Storage and Assets

## Storage abstraction

Core uses an S3-compatible storage adapter.

Supported deployment options:

- RustFS for self-hosted S3-compatible object storage
- any S3-compatible provider in hosted environments

### Adapter port (CORE-AST-002)

The adapter is a Core **port**, `IAssetStorage` (Assets module). It is the single
seam between Core and the object storage, and the only access it ever yields is a
short-lived, signed URL (`SignedAssetUrl`) for an `Upload` or `Download` of an
already-resolved, tenant- and workspace-scoped asset's own object — never a public
or static URL, and never an arbitrary caller-supplied object key. The
`SignedAssetUrl` type enforces the security invariants structurally: it requires an
absolute URL and a strictly positive lifetime no longer than a one-hour ceiling, so
a long-lived, non-expiring or public URL is unrepresentable, and its `ToString`
omits the secret URL so a signed URL is never logged.

The port does not authorize the caller; the consuming upload-intent (CORE-AST-003)
and signed-download (CORE-AST-004) flows authorize server-side first and only then
ask the adapter to mint a URL.

Core now ships a **concrete** S3-compatible implementation of this port
(CORE-OPS-006, below); the object-storage endpoint and credentials are still
supplied by the deployment through configuration only (see
`docs/13_SELF_HOSTING_REQUIREMENTS.md`; ADR 0006), so no credentials live in
source. The concrete adapter is selected conditionally on that configuration; until
storage is configured the default stays a fail-closed adapter that denies every
operation, so assets stay private by default even when storage is not configured.

### Concrete S3-compatible adapter (CORE-OPS-006)

`S3CompatibleAssetStorage` implements `IAssetStorage` over the **AWS SDK for .NET**
S3 client (`AWSSDK.S3`), the official client for the S3 protocol, which speaks to
any S3-compatible backend (RustFS self-hosted or any S3-compatible provider
hosted). It mints **SigV4 pre-signed** `Upload` (PUT) and `Download` (GET) URLs —
computed locally by the SDK, with no network round-trip — for the **given asset's
own** bucket and object key, and re-validates each through `SignedAssetUrl`
(absolute, lifetime ≤ one hour), so a public, long-lived or cross-object URL is
impossible (threats T4/T5/T1). `DeleteObjectAsync` performs a real, server-side
object delete with the deployment's own credentials (no URL is handed to any
client; idempotent, so a never-uploaded pending object deletes cleanly).

The adapter is registered **conditionally** by `AddAssetStorage(configuration)`,
used by both the API host and the worker cleanup job:

- **configured** — `Assets:Storage:Endpoint`, `Assets:Storage:AccessKeyId` and
  `Assets:Storage:SecretAccessKey` all present → the concrete adapter, so the API
  mints real pre-signed URLs and the worker deletes real objects;
- **unconfigured or partial** → the fail-closed `UnconfiguredAssetStorage`, so
  asset operations return `503` (private by default even when unconfigured).

Optional settings: `Assets:Storage:Region` (default `us-east-1`),
`Assets:Storage:ForcePathStyle` (default `true`) and `Assets:Storage:UrlLifetime`
(default 15 minutes; validated `> 0` and `≤ 1h`). `Assets:Storage:Bucket` /
`:Provider` remain the per-asset naming. No storage credential is read anywhere but
configuration (threat T7). `AWSSDK.S3` is a justified dependency: S3 SigV4
pre-signing is security-sensitive crypto best left to the official SDK.

### Upload intent flow (CORE-AST-003)

The first asset HTTP route, `POST /api/v1/assets/upload-intent`
(`csv/api_routes.csv`, roles Owner/Admin/Host/CoHost), is the "Create upload intent"
step of the lifecycle above. After authorizing the caller server-side (tenant +
workspace role), the flow registers a new `Pending` asset and mints the short-lived,
signed **upload** URL through the adapter port. The storage coordinates are minted
**server-side**, never accepted from the client: the deployment's private provider
and bucket (configuration `Assets:Storage:Provider` / `Assets:Storage:Bucket`, with
safe private-by-default fallbacks — only the naming, no credentials) plus a tenant-
and workspace-scoped, collision-free object key (`assets/{organizationId}/{workspaceId}/{uuid}`).
A client can therefore never choose the bucket or point an upload at another tenant's
or workspace's object (the storage-object-key uniqueness guarantee). The signed URL
is minted before the metadata row is persisted, so an unconfigured storage backend
fails closed (`503`) leaving no orphan pending asset — assets stay private by default
even when storage is not configured.

### Signed download flow (CORE-AST-004)

The asset read route, `GET /api/v1/assets/{assetId}/download-url` (`csv/api_routes.csv`,
"authorized viewers", "Signed URL after permission check"), is the "download URL requires
authorization" step of the lifecycle above. The route path carries only the asset id, so
the target organization is a required `?organizationSlug=` query parameter; the asset is
loaded **within** that resolved tenant (`IAssetRepository.FindByIdInOrganizationAsync`, the
predicate leads with `organization_id` so a foreign-tenant asset is never found), its own
workspace is **discovered from the loaded row** after the tenant boundary is enforced, and
the caller is authorized **server-side** by their role in the asset's own workspace.

The authorized viewers are the host-content roles (Owner/Admin/Host/CoHost — the "View
host-only content" capability of `docs/06_AUTHORIZATION_MATRIX.md`), reused through the
central Visibility module's role classification so visibility logic is not duplicated.
Audience roles (Participant/Observer) and the audit role are **denied fail-closed**: an
asset becomes audience-visible only once it is linked to a visible content block/entity
(CORE-AST-005), which does not exist yet, so until then only host-content roles may
download (threat T4 "Asset leak"; threat T2 visibility leak). A caller who cannot see the
tenant, an unknown or cross-tenant asset, and a non-member of the asset's workspace are all
hidden as `404` (never `403`); a known member who is not an authorized viewer is `403`.

Only **after** the permission check passes does the flow mint the short-lived, signed
**download** URL through the adapter port and return `200 OK`. The asset stays private —
the only access handed out is that single signed URL. The asset must be `Available`: a
still-`Pending` asset (its upload not yet confirmed) is `409 Conflict`, reported only to an
authorized viewer. An unconfigured storage backend fails closed (`503`) and produces no URL,
so assets stay private by default even when storage is not configured.

### Asset linking flow (CORE-AST-005)

The asset-link route, `POST /api/v1/assets/{assetId}/links` (`csv/api_routes.csv`, roles
Host/CoHost/Owner/Admin), is the "asset can be linked to ContentBlock or Entity" step of the lifecycle
below. The route path carries only the asset id, so the request body supplies the target organization
(`organizationSlug`, resolved by the same token-claim-and-membership tenant check as the reveal command)
and the linked resource — its generic `targetType` (`ContentBlock` or `Entity`, never a `Scene`) and
`targetId`. The asset is loaded **within** the resolved tenant (`FindByIdInOrganizationAsync`, the
predicate leads with `organization_id`), its own workspace is **discovered from the loaded row** after the
tenant boundary is enforced, and the caller is authorized **server-side** by their role in the asset's own
workspace. A caller who cannot see the tenant, an unknown or cross-tenant asset, and a non-member of the
asset's workspace are all hidden as `404` (never `403`); a known member who lacks the link role is `403`.

Only after authorization is the target validated. The polymorphic `target_id` is a plain reference (not a
database foreign key), so the create flow enforces the **same-workspace coupling**: it resolves the target
content block / entity through the workspace-scoped repository of the asset's **own** organization and
workspace before creating the link (mirrors `visibility_rules.resource_id`, `content_blocks.scene_id`,
`entities.entity_type_id`). A target that does not exist in the asset's workspace — including one in
another workspace or tenant — is hidden as `404` and no link is created, so an asset can never be linked to
a foreign-workspace or foreign-tenant resource. A repeat of the same link is `409` (the per-workspace
unique `asset_links(workspace_id, asset_id, target_type, target_id)` key prevents duplicates); a new link
is `201`.

The `asset_links` table (`csv/database_tables.csv`, module Assets, scope `workspace`; the documented
critical index is `asset_links(workspace_id, asset_id)`) is the **join** that lets an asset **inherit**
the audience visibility of the resource it is attached to. Linking never makes an asset public: it only
records the attachment whose audience visibility the **central Visibility engine** then governs. The
signed download flow (CORE-AST-004) now consults these links — an **audience** role (Participant/Observer)
may obtain a download URL only when the asset is linked to a content block or entity that is **visible to
the audience** (`VisibilityPolicy.CanViewResource`, reused — not duplicated); host-content roles may always
download, and the audit role and any other role are denied fail-closed. The asset stays **private** and is
reached only through the single short-lived signed URL minted after the permission check (the epic
acceptance criterion; threat T4 "Asset leak"; threat T2 visibility leak).

### Session-scoped audience download (CORE-SVIS-003, completed by CORE-SVIS-004)

A reveal is **session-scoped** (`docs/adr/0013-session-scoped-visibility-rules.md`): a resource is made
visible to **one session's** audience, never workspace-wide. So when an audience caller downloads an asset,
"is the linked resource visible?" is only meaningful **within a session**. **Every** audience asset download
is therefore authorized against the **session-scoped** visibility of the linked resource; the workspace-wide,
session-agnostic overload that once backed the role-level path has been **removed** (CORE-SVIS-004), so a
session-agnostic audience decision can no longer be reintroduced.

The audience caller supplies the session in a `?sessionId=` **query parameter** on the signed download route
(the same way the participant-visible feed names its session). It is **required for any audience caller** —
both `Participant` and `Observer`:

- A **`Participant`**'s links are gated by the **same per-participant primitive the feed uses**
  (`AssetDownloadPolicy.CanParticipantDownloadAsync` over `VisibilityPolicy.CanParticipantViewResource`,
  reused — not forked), so a participant may obtain a download URL only when the asset is linked to a content
  block or entity **visible to them in that session** — an audience-wide reveal of that session **or** a
  reveal scoped to exactly them. A participant **cannot** obtain a download URL for an asset tied to a
  resource revealed only in a **sibling session** of the same workspace (the cross-session leak; threat
  T5/T3), nor for one revealed only to **another** participant (the selected-participant guarantee; threat
  T2). A participant-role member with **no active participant record** in the asset's workspace is denied
  fail-closed (`403`).
- The non-participant audience role **`Observer`** is gated by the **session-scoped role-level** decision
  (`AssetDownloadPolicy.CanDownload` over `VisibilityPolicy.CanViewResource`), so an Observer may download
  only when a linked target is **audience-wide visible in the supplied session** — never one revealed only in
  a **sibling session** (the residual the ADR 0013 role-level carve-out left, now closed).

Because the caller is already a known member of the asset's workspace, a missing or malformed `sessionId` for
an audience caller is a request-shape `400` (surfaced only after the membership `404`-hide gate). A foreign or
unknown session id simply matches no rule, so it is a fail-closed `403` and never probes session existence.

**Host-content access is unchanged.** The host-content roles (`Owner`/`Admin`/`Host`/`CoHost`) may always
download; their content access is **session-agnostic**, so they need no `sessionId`. The audit role and any
undefined role are denied fail-closed. The tenant/asset boundary is enforced **before** the
participant/session logic, so a foreign-tenant asset is still hidden as `404`.

### Host-initiated deletion flow (CORE-LIFE-006)

The asset delete route, `DELETE /api/v1/assets/{assetId}` (`csv/api_routes.csv`, roles
Host/CoHost/Owner/Admin), lets a host remove an asset directly — until this story an `Available` asset
could be created, linked and downloaded but never deleted (only the background cleanup job could reclaim
abandoned `Pending` intents). The route path carries only the asset id, so the target organization is a
required `?organizationSlug=` query parameter; the asset is loaded **within** that resolved tenant
(`IAssetRepository.FindByIdInOrganizationAsync`, the predicate leads with `organization_id`), its own
workspace is **discovered from the loaded row** after the tenant boundary is enforced, and the caller is
authorized **server-side** by their role in the asset's own workspace — exactly the load-then-authorize
shape of the signed download route. A caller who cannot see the tenant, an unknown or cross-tenant asset,
and a non-member of the asset's workspace are all hidden as `404` (never `403`); a known member who lacks
the delete role is `403`. The delete roles are the host-content `Owner`/`Admin`/`Host`/`CoHost`, the same
host-capable set that creates upload intents and links assets and that deletes scenes/entities/content
blocks (`docs/06_AUTHORIZATION_MATRIX.md`; `docs/adr/0012-resource-deletion-cascades-dependents.md`).

The deletion **cascades**, not blocks (ADR 0012). In one transaction it removes the asset's `asset_links`
(`asset_links.asset_id` is a real `ON DELETE CASCADE` foreign key, so the database would also cascade them;
the application removes them explicitly first so the cascade is deterministic and the "its links are removed"
effect is directly testable — ADR 0012 step 2), **then deletes the underlying storage object** via the
`IAssetStorage` adapter, **then deletes the metadata row**, and appends an `AssetDeleted` audit record. The
storage object is deleted **before** the metadata row — the same ordering the upload-intent flow uses (mint
the signed URL before persisting the row) — so a row is never removed while its object remains: a deletion
never leaves an orphaned object behind, and a storage failure leaves no dangling row. An asset is not a
visibility resource and is never an asset-link **target**, so there are no `visibility_rules` or target-side
links to clean up (only the asset's own links). An unconfigured storage backend fails closed (`503`): the
`UnconfiguredAssetStorage` throws when the object delete is attempted, the whole transaction rolls back having
removed nothing, and the asset stays private by default even when storage is not configured (threat T4 "Asset
leak"). On success the route returns `204 No Content`; deleting a non-existent asset is a safe hidden-`404`.

### Cleanup job (CORE-AST-006)

The asset cleanup job is the lifecycle's final step. It is a periodic background sweep that runs in the
**worker** host (docs/02_ARCHITECTURE.md: the worker owns "cleanup" and async jobs), not behind any HTTP
route. It reclaims **abandoned upload intents**: an asset registered `Pending` when its upload intent was
created (CORE-AST-003) whose upload was never confirmed (CORE-AST-004) within the deployment's grace window
(`Assets:Cleanup:PendingRetention`, default 24 hours). Each such asset leaves a stale metadata row and,
possibly, an orphaned object in private storage; the sweep deletes the **object first**, then the **metadata
row** (so a row never outlives its object — no orphaned object is ever left behind).

Object deletion is a new, server-side `IAssetStorage` operation (the adapter deletes the object directly with
the deployment's own credentials — no signed URL is produced and no bytes are served), so cleanup only ever
**removes** access and can never weaken the private-by-default posture (threat T4 "Asset leak"). It is
fail-closed like the signing operations: with no configured storage adapter (`UnconfiguredAssetStorage`) the
delete throws and the sweep removes **nothing** — it never deletes a metadata row whose object it could not
delete. Only `Pending` assets are ever touched; a confirmed (`Available`) asset — real, possibly-linked
content — is never reclaimed, however old. The cleanup logic lives in the Assets module
(`ExpiredPendingAssetCleanupService`); the worker only schedules it (`Assets:Cleanup:SweepInterval`,
`Assets:Cleanup:BatchSize`), and like the API host it is gated on a configured database connection string. No
storage credentials live in Core; the concrete S3-compatible adapter is supplied by the deployment
(docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006; threat T7).

## Security rules

- buckets private by default
- no public object listing
- upload intent requires authorization
- download URL requires authorization
- deletion requires authorization
- signed URLs are short-lived
- asset metadata is filtered by visibility rules
- an asset is audience-accessible only when linked to a visible content block or entity
- every audience download (participant and observer) is authorized against the session-scoped visibility of the linked resource; the workspace-wide overload has been removed (CORE-SVIS-004)
- deleting an asset removes its storage object before its metadata row (no orphaned object)

## Asset lifecycle

```text
Create upload intent
  -> client uploads to storage
  -> client confirms upload
  -> Core stores asset metadata
  -> asset can be linked to ContentBlock or Entity
  -> visibility controls whether it can be accessed
  -> a host can delete the asset (its links and storage object are removed)

(an upload intent that is never confirmed within the grace window is reclaimed
 by the background cleanup job: its object and metadata row are deleted)

(a host-initiated delete removes the asset's links, then its storage object,
 then its metadata row — object before row, so no orphaned object is left behind)
```

## Metadata

Assets should track:

```text
asset_id
organization_id
workspace_id
storage_provider
bucket
object_key
content_type
size_bytes
checksum
created_by
created_at
status
```

## Avoid

- storing large binary files in PostgreSQL
- public bucket access
- long-lived signed URLs
- direct storage credentials in frontend
