# Storage and Assets

## Storage abstraction

Core uses an S3-compatible storage adapter, so **any S3-compatible provider works**:
the adapter speaks the standard S3 protocol and never depends on a specific vendor
(ADR 0006). RustFS is the **default example** backend the in-repo stack bundles — an
Apache-2.0-licensed, S3-compatible object store — but it is only the example default;
an operator may still bring their own S3-compatible provider.

Supported deployment options:

- RustFS for self-hosted S3-compatible object storage (the bundled default example)
- any S3-compatible provider in hosted or self-hosted environments (bring your own)

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

**Bounded outbound calls (CORE-RES-005).** The one storage operation that makes a
real network round-trip is `DeleteObjectAsync` (the worker cleanup/retention jobs;
minting a pre-signed URL is local). The SDK client applies a short **per-request
timeout** plus a **bounded retry** count and mode so a hung storage backend **fails
fast** instead of holding a worker thread up to the AWS SDK's 100-second default:
`Assets:Storage:RequestTimeout` (default `00:00:30`, validated `> 0`),
`Assets:Storage:MaxErrorRetry` (default `2`, validated `≥ 0`) and
`Assets:Storage:RetryMode` (default `Standard`; one of `Legacy`/`Standard`/`Adaptive`).
A storage failure stays fail-closed and contained (threat T4) — these bounds never
weaken the private-by-default posture, they only stop a slow dependency from stalling.

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

### Upload acceptance: content-type allowlist and size ceiling (CORE-AST-007)

The upload-intent flow additionally validates the **declared** content type and size against a
deployment-configurable **acceptance policy** (`AssetUploadConstraints`), abuse-surface hardening that
is **independent of the workspace storage quota** (CORE-MON-006): the quota bounds a workspace's
**total** stored bytes, while this policy bounds a **single** object's declared type and size.

- **MIME allowlist.** The declared `contentType` must be on a configurable allowlist
  (`Assets:Upload:AllowedContentTypes`, matched case-insensitively). A well-formed but disallowed type
  is rejected with `422 Unprocessable Entity` (`unprocessable_entity`).
- **Absolute per-object size ceiling.** The declared `sizeBytes` must be at most a configurable
  absolute ceiling (`Assets:Upload:MaxObjectSizeBytes`, inclusive). An over-ceiling object is rejected
  with `413 Payload Too Large` (`payload_too_large`); the response names only the byte ceiling.

Both checks run **before** the storage quota is consulted and **before any signed upload URL is minted**,
so a disallowed type or an over-ceiling object is rejected **fail-closed** with no quota consumed, no
metadata row persisted and **no URL minted** — the rejected intent never reaches the storage adapter, and
the response carries **no storage coordinate** (threats T4 "Asset leak"/T7). The privacy model (private
bucket, signed URL only after authorization) is unchanged. The checks apply only **after** the
server-side tenant/workspace/role authorization, so an unauthorized caller never receives request-shape
feedback.

Both settings ship **safe, already-hardened defaults** so an unconfigured deployment is not wide open:
the allowlist defaults to a curated set of common safe media types (images, `application/pdf`, audio,
video, `text/plain`) and the ceiling defaults to **1 GiB**. A deployment overrides either under
`Assets:Upload:*`; an **absent** allowlist falls back to the curated default (a blank value never
silently disables it), while an explicitly **empty** configured list disables the content-type
restriction. A configured ceiling that is not strictly positive is rejected at startup. No value here is
a secret (threat T7).

### Binding the declared type and size into the upload URL (CORE-AST-008)

CORE-AST-007 validates the **declared** `contentType`/`sizeBytes` and CORE-MON-006 reserves `sizeBytes`
against the workspace storage quota, but both only ever see the **declared** metadata. Until this story
the minted presigned `PUT` bound only bucket/key/verb/expiry, so a client could declare 1 byte of an
allowed MIME — passing the allowlist, the ceiling and the quota — and then `PUT` a multi-gigabyte object
of **any** type, bypassing all three (the bytes that actually land in storage were never constrained).

The upload URL now **binds the declared content type and content length into the SigV4 signature** so the
storage backend itself enforces them on the real upload (`S3CompatibleAssetStorage`):

- the presigned `PUT` signs **`content-type`** (`GetPreSignedUrlRequest.ContentType`) and
  **`content-length`** (the declared `sizeBytes`, reserved against the quota) as required, signed headers —
  they appear in the URL's `X-Amz-SignedHeaders`;
- an upload whose actual `Content-Type` or byte count differs from the declared/reserved value fails the
  SigV4 signature check and is **rejected by storage**, so a client can no longer declare-small-then-upload-large
  to bypass the quota or the CORE-AST-007 ceiling/allowlist;
- the client therefore sends a matching `Content-Type` (the `contentType` returned by the upload-intent
  response) and the declared number of bytes; an **in-spec** upload still succeeds.

The declared values live on the asset row itself (its content type is always set; its size is the bytes
reserved against the quota), so binding them needs no extra input or contract change. Only the **upload**
direction is bound — a **download** URL serves an already-stored object, so it signs nothing extra. The
binding adds only signed **request conditions**: it mints no public affordance, surfaces no storage
coordinate to any caller, and leaves the private-bucket / signed-URL-after-authorization model unchanged
(threats T4 "Asset leak"/T7). The signature crypto is the official AWS SDK's (no new dependency).

### Confirm upload flow (CORE-ALC-001)

The asset confirm route, `POST /api/v1/assets/{assetId}/confirm-upload` (`csv/api_routes.csv`, roles
Host/CoHost/Owner/Admin), is the **"client confirms upload"** step of the lifecycle below. After the client
has uploaded the object against the server-minted signed `PUT` URL (CORE-AST-003/008), it calls this route to
drive the existing `Asset.MarkAvailable` domain transition: a still-`Pending` asset records its uploaded
`sizeBytes` and `checksum`, moves to `Available` and **becomes downloadable** (CORE-AST-004). It is the **only
`Pending`→`Available` transition** — until this story the `MarkAvailable` method and
`IAssetRepository.UpdateAsync` existed but were wired to **no transport**, so an asset could never leave
`Pending` except via the background cleanup that reclaims an abandoned, never-confirmed intent (CORE-AST-006).
A storage-event/webhook-driven transition is a documented alternative, **out of scope** here (the explicit
confirm route needs no new inbound infrastructure).

The route path carries only the asset id, so the request body supplies the target organization
(`organizationSlug`, resolved by the same token-claim-and-membership tenant check as the upload-intent command)
and the confirmed upload facts (`sizeBytes`, `checksum`). The asset is loaded **within** the resolved tenant
(`FindByIdInOrganizationAsync`, the predicate leads with `organization_id`), its own workspace is **discovered
from the loaded row** after the tenant boundary is enforced, and the caller is authorized **server-side** by
their role in the asset's own workspace. The confirm roles are the host-content `Owner`/`Admin`/`Host`/`CoHost`
— the **same set that creates the upload intent** (`docs/06_AUTHORIZATION_MATRIX.md`). A caller who cannot see
the tenant, an unknown or cross-tenant asset, and a non-member of the asset's workspace are all hidden as `404`
(never `403`); a known member who lacks the confirm role is `403`. Only **after** authorization is the request
validated (a negative `sizeBytes` or a missing/malformed `checksum` is `400`), so an unauthorized caller never
receives request-shape feedback.

It is **fail-closed** on the lifecycle: confirming a **non-`Pending`** (already-confirmed) asset is `409
Conflict` — the guarded transition is the only path to `Available`, so a re-confirm changes nothing and can
never silently overwrite a different already-recorded size/checksum (reported only to an authorized caller).
The transition and the **audit** append (`AssetConfirmed`, recording the `Pending`→`Available` state names —
never the checksum or any storage coordinate) commit in **one transaction**, so a confirmation is recorded as
a fact or it does not happen. The flow touches **no object storage** (the upload already happened against the
signed `PUT`), so it never returns `503` for unconfigured storage. On success the route returns `200 OK` with
the now-`Available` asset; a download URL, which stays `409` while the asset is `Pending`, then succeeds.

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
- confirm upload requires authorization; it is the only Pending-to-Available transition and is fail-closed (a non-Pending asset is 409) and audited (CORE-ALC-001)
- upload intent validates the declared content type against a configurable MIME allowlist and the declared size against a configurable absolute per-object ceiling, fail-closed before any signed URL is minted (CORE-AST-007)
- the signed upload URL binds the declared content type and content length into the SigV4 signature, so storage rejects an upload whose actual type or byte count violates what was declared/reserved (CORE-AST-008)
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
  -> client uploads to storage (the signed upload URL pins the declared
     content type and content length, so storage rejects a non-conforming upload)
  -> client confirms upload (POST /api/v1/assets/{assetId}/confirm-upload, CORE-ALC-001:
     drives Asset.MarkAvailable Pending->Available with the uploaded size and checksum)
  -> Core stores asset metadata (the asset is now Available and downloadable)
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

## Export artifacts (CORE-EXP-001, CORE-EXP-002)

The export read/download route — `GET /api/v1/exports/{exportId}` (`ExportEndpoints.cs`,
`csv/api_routes.csv`) — applies the same "no public URL, authorized delivery" rule as the asset
signed-download flow, for BOTH export scopes. The Core stores no separate export blob in object
storage, so an export is always delivered as an **authorized stream** — the response body of the
authenticated, authorized request — and **never** through a public or static URL. Either way the
download is **authorized before any artifact/state is disclosed** (the asset signed-URL discipline):
resolve the trusted tenant, load the export job within it, and only then authorize and return.

- a **workspace** export's produced artifact is its `ExportManifest` (the per-kind table of contents —
  counts only, never any exported scene/content body; threats T7/T8). Only the **"Export workspace"**
  roles {Owner, Admin, Host} may download (`ExportAccessPolicy`, resolved in the export's **own**
  workspace); a non-authoring role is **403**, so a participant-scoped (audience) caller never receives
  host-only export content (threat T8 "Export leak"). The manifest is role-projected through the existing
  `ExportManifestProjection`, so the export shape stays role-scoped (defence in depth)
- a **user-data** export (`ExportScope.UserData`, CORE-EXP-002) discloses the data subject's personal
  data. The worker PRODUCES it by driving the queued job to terminal `Completed` (no persisted artifact —
  the PII is never stored), and the download then assembles the subject's data **on disclosure** through
  the reused `PersonalDataExportService` (CORE-PRIV-004): authorized to the data subject themselves or an
  Owner/Admin acting on their behalf (the access/portability model), tenant-scoped, and audited
  (`PersonalDataExported`, by id only); a non-entitled requester is **403**
- for either scope a foreign-tenant or unknown export (and, for a workspace export, a non-member of its
  workspace) is hidden as **404** (threats T1/T5); an incomplete or failed export (no retrievable
  artifact/personal data) is **409**

A retention-based expiry of the artifact (a true `ExpiresAt` with an object-storage purge) lands with
the data-retention sweeps (CORE-PRIV-003).
