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

The concrete, provider-specific implementation (its SDK and the object-storage
endpoint/credentials) is supplied by the deployment (see
`docs/13_SELF_HOSTING_REQUIREMENTS.md`; ADR 0006), so Core carries no
object-storage SDK dependency and no credentials in source. Until one is wired, the
default is a fail-closed adapter that denies every operation, so assets stay private
by default even when storage is not configured.

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
acceptance criterion; threat T4 "Asset leak"; threat T2 visibility leak). The cleanup job (CORE-AST-006) is
a later story.

## Security rules

- buckets private by default
- no public object listing
- upload intent requires authorization
- download URL requires authorization
- signed URLs are short-lived
- asset metadata is filtered by visibility rules
- an asset is audience-accessible only when linked to a visible content block or entity

## Asset lifecycle

```text
Create upload intent
  -> client uploads to storage
  -> client confirms upload
  -> Core stores asset metadata
  -> asset can be linked to ContentBlock or Entity
  -> visibility controls whether it can be accessed
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
