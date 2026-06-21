# Privacy and Data Protection

This note is the **single place a self-hoster** uses to assess and configure the
Core's data-protection posture (CORE-PRIV-005). It records, for a deployment of
the product-neutral Core:

- the **PII inventory / data map** — what personal data Core stores and where;
- the **controller-vs-processor split** for self-hosters;
- the **data-residency configuration** — where the database and the asset storage
  physically live;
- the **default retention windows** (CORE-PRIV-003);
- the **at-rest-encryption expectations**; and
- the **consent / legal-basis recording decision**.

It is **documentation only**: it adds no route, table, event or migration and
changes no Core source — it records the data map and two explicit decisions — so
all eleven spec-consistency checks (`docs/24_SPEC_CONSISTENCY.md`) and the boundary
scan stay green. It pairs with the data-lifecycle mechanisms that were built:
data-subject erasure (CORE-PRIV-001, GDPR Art.17), authorized tenant deletion
(CORE-PRIV-002), the data-retention sweep (CORE-PRIV-003, Art.5(1)(e), extended to
bound the idempotency-key store by CORE-PRIV-006) and the data-subject
access/portability export (CORE-PRIV-004, Art.15/20). The
decision-register sections below mirror the dated, named-owner decision style of
`docs/24_SPEC_CONSISTENCY.md`.

This is engineering documentation, not legal advice. The lawful basis, the privacy
notice, the records of processing and any data-protection-impact assessment are the
**controller's** responsibility (see the controller/processor split below).

## Controller vs processor (the self-hoster is the controller)

The Core is **self-hosted software the operator runs on their own
infrastructure**, not a managed service. So in GDPR terms:

- **The self-hoster/operator is the data controller.** They decide the purposes
  and means of processing — which identity provider issues subjects, which
  workspaces and sessions exist, what content participants enter, which storage and
  region the data lives in, and how long it is kept. Every data-subject-rights
  mechanism Core ships (access, erasure, retention, offboarding) is exercised *by
  the controller*, through the controller's own authenticated administrators.
- **Core (and its maintainers) are not a processor of the deployment's data.** The
  software contacts **no** default external service and sends **no** personal data
  to the project maintainers; there is no upstream telemetry, no phone-home and no
  shared backend. The maintainers of `livecore-platform` therefore process nothing
  on the controller's behalf — they ship code, not a service.
- **The external services the operator wires up are the controller's
  sub-processors**, under the controller's own agreements — Core ships **no
  credentials** for any of them (threat T7, `docs/07_SECURITY_THREAT_MODEL.md`):
  the OIDC identity provider (`Authentication:Oidc:*`), the S3-compatible
  object-storage provider (`Assets:Storage:*`, CORE-OPS-006), the optional
  Redis/Valkey realtime backplane (`Realtime:Backplane:*`, CORE-OPS-007), the
  optional OTLP tracing collector (`Tracing:Otlp:Endpoint`, CORE-OBS-003) and the
  Apple/Google receipt-verification providers reached by the deployment-supplied
  adapter (CORE-MON-008). The controller contracts with and configures each, and is
  responsible for the sub-processor terms and any cross-border-transfer safeguards.
- **For a vertical built on Core** (the dependency rule, `docs/04_PRODUCT_BOUNDARIES.md`),
  the **vertical's operator is the controller**; Core remains the same
  product-neutral engine underneath, with the same data map and the same
  mechanisms.

**Decision (2026-06-17): the self-hoster is the controller; Core is operator-run
software, not a processor.** Owner: **operator** (the self-hoster / the vertical).
This is final, not deferred: Core deliberately ships no managed backend and no
default external dependency, so there is no maintainer-operated processor to put
under a data-processing agreement; the operator's DPAs are with the sub-processors
*they* choose above.

## PII inventory / data map

The table below is the authoritative map of every place Core persists personal
data, drawn from `docs/10_DATABASE_SCHEMA.md` and `csv/database_tables.csv`. "Scope"
is **global** (one deployment-wide identity, no `organization_id`), **tenant**
(carries `organization_id`, removed by the tenant cascade, CORE-PRIV-002) or
**subject-keyed/deployment-spanning** (keyed by a polymorphic subject pair or by an
external receipt, not by tenant).

| Where (table.column) | Scope | Personal data | Erasure / retention path |
| --- | --- | --- | --- |
| `users.email`, `users.display_name`, `users.issuer`, `users.subject_id` | global | The data subject's identity: email, display name and the OIDC issuer+subject pair. The row **is** the PII. | **Hard-deleted** on erasure (CORE-PRIV-001, Art.17); the row itself is the personal data. |
| `push_subscriptions.endpoint`, `.p256dh`, `.auth` (FK `user_id`→`users`, `CASCADE`) | global | A principal's browser Web Push subscription: the push service `endpoint` URL plus the client `p256dh` public key and `auth` encryption secret — per-device personal data the subject registers (CORE-PUSH-001). | `CASCADE` on erasure: the subject's subscriptions are removed with their profile. **Disclosed** in the access/portability export (CORE-PRIV-004) as the subscription id, endpoint and creation time only — the `p256dh`/`auth` keys are **never** projected (threat T7). |
| `participants.display_name` | tenant | A participant's free-text display name (may be a real name). | **Anonymized** to a fixed placeholder on erasure; the `user_id` link is cleared. Removed with its session by the retention sweep / tenant cascade. |
| `participants.user_id` (FK→`users`, `SET NULL`) | tenant | Pseudonymous link from a participant record to an identity. | `SET NULL` on erasure: the participant record survives, de-linked. |
| `workspace_invitations.invited_email` | tenant | The plaintext email an invitation was addressed to. | **Anonymized** on erasure; **purged** by the retention sweep on terminal invitations (30-day default, **on**, CORE-PRIV-003). The verified owner of the address can **self-discover** their own pending invitations (`GET /api/v1/me/invitations`, CORE-INV-002) — matched server-side only on the caller's `email_verified` email (CORE-INV-001) and scoped to the caller's claimed tenants — but that read's projection **never returns the email itself** (nor the token hash), only the organization slug, workspace id, role, status and expiry the caller needs to accept; it discloses no new personal data. |
| `organization_members.user_id`, `workspace_members.user_id` (FK→`users`, `CASCADE`) | tenant | Pseudonymous link associating a person with a tenant/workspace and a role. | `CASCADE` on erasure: the access grants are revoked everywhere. |
| `assets.created_by` (FK→`users`, `SET NULL`) | tenant | Pseudonymous "who uploaded this" link. Asset **binaries** (object storage) may themselves contain personal data depending on what is uploaded. | `SET NULL` on erasure: the asset survives, creator anonymized. Binaries live in the private bucket (CORE-OPS-006). |
| `export_jobs.requested_by` (FK→`users`, `SET NULL`); export **manifests** | tenant | Pseudonymous requester link; a manifest can contain personal data (role-projected). | `SET NULL` on erasure; completed exports **purged** by retention (90-day default, **off**, CORE-PRIV-003). |
| `session_events` payloads | tenant | Append-only session stream; payloads may carry content that is personal data, depending on the vertical's use. | Removed only via the session cascade in the retention sweep (Art.5(1)(e)); otherwise append-only history. |
| `recaps` | tenant | A generated recap is host content and may contain personal data. | **Purged** by retention (365-day default, **off**, CORE-PRIV-003). |
| `audit_logs` (actor/resource ids + enums) | tenant + platform | **PII-free by design**: references the actor and resource **by id** (pseudonymous identifiers) plus action enums — never an email, display name or content (threat T7). | Append-only and tamper-evident (per-tenant SHA-256 hash chain, CORE-SEC-003). **Not** erased: the references are recorded facts, not foreign keys, so the chain still verifies after an erasure. |
| `billing_account_links.subject_*`; `purchase_transactions`, `purchase_events`, `store_notification_events` | subject-keyed / deployment-spanning | A purchase is made by a person: the buyer is a `User` subject whose id is their `users(id)` (a pseudonymous link), plus external provider transaction/notification ids. **No payment-card / instrument data** is stored — verification is store-delegated (CORE-MON-008). | Tied to the buyer's `users` id; the purchase ledger is an append-only system of record (`docs/13`, "Backup and restore"). |
| `subject_entitlements`, `quota_usage` (subject pair) | subject-keyed | For a `User` subject, the subject id is the user-profile id (a pseudonymous link). No name/email. | Keyed by the subject pair; an organization-subject row is unreachable residue after a tenant delete (recorded in `docs/10`). |

### What Core deliberately does **not** store

- **No authentication credentials.** Authentication is delegated to the OIDC
  identity provider (ADR 0005); Core validates tokens and never sees or stores a
  password (`docs/07`). There is no custom password auth (`AGENTS.md`).
- **No payment-instrument data.** Receipt verification is delegated to a
  deployment-supplied adapter (CORE-MON-008); Core stores only provider transaction
  ids and a purchase status, never card numbers or billing addresses.
- **No advertising identifiers / ad tracking.** Ads stay in the vertical apps
  (ADR 0011, `docs/22`); Core answers ad **eligibility** from entitlements only and
  stores no ad identifier.
- **No persisted IP addresses or device fingerprints in the domain model.** The
  client IP is used **in memory** to partition the webhook rate limiter
  (CORE-SEC-001) and is restored only from a trusted proxy; it is not written to a
  domain table. Logs, metrics and traces carry **ids and low-cardinality
  attributes only — never content or PII** (threat T7, CORE-OBS-003).

## Data residency (storage and DB region)

Core keeps all of its **database**-resident personal data in **one** PostgreSQL
database and all **asset binaries** in **one** object-storage bucket. Residency is
therefore wholly the operator's placement of those two stores — Core never
replicates either to a second location and has no hidden region of its own.

| Surface | Residency control | Where the data physically lives |
| --- | --- | --- |
| **Database** (all DB-resident PII above) | `ConnectionStrings:Database` (`ConnectionStrings__Database`) | Wherever the operator runs PostgreSQL — the host in the connection string. Core has **no** database-region setting and never copies the database elsewhere. To keep the data in a region, run PostgreSQL (and its replicas, PITR WAL archive and dumps) in that region. |
| **Asset binaries** | `Assets__Storage__Endpoint` (placement) + `Assets__Storage__Region` (SigV4 signing region) | The private bucket behind the endpoint (CORE-OPS-006). `Endpoint` decides the **physical** location; `Region` is the SigV4 signature region and **must match the bucket's region**. Point the endpoint at an in-region bucket to keep asset content in a region. |
| **Backups** | `-OutputDirectory` / mirror destination of the backup tooling (CORE-OPS-010) | The encrypted dump and the mirrored, encrypted asset copy (CORE-DR-001). Keep the backup destination **in-region** if residency requires; it is encrypted at rest by the tooling itself. |
| **Realtime backplane** (optional) | `Realtime:Backplane:ConnectionString` (CORE-OPS-007) | Redis/Valkey carries **transient** pub/sub of already-authorized deliveries — no durable PII — but still place it in-region. |
| **Tracing collector** (optional) | `Tracing:Otlp:Endpoint` (CORE-OBS-003) | The operator's OTLP collector. Spans carry **no content/PII**, only low-cardinality attributes. |
| **Identity provider** (external) | `Authentication:Oidc:Authority` | The IdP holds the authoritative identity; its region is the operator's IdP choice (a sub-processor, above). |

**Decision context:** `Assets__Storage__Region` already existed
(`.env.example`, `docs/13`) as SigV4 tuning but was undocumented as a **residency**
control, and the **database** region was unaddressed. This note records both as the
two residency knobs a self-hoster sets, with no Core change required.

## Default retention windows (CORE-PRIV-003)

The data-retention sweep (a worker loop, `docs/13_SELF_HOSTING_REQUIREMENTS.md`,
`docs/10_DATABASE_SCHEMA.md`) expires and purges terminal/old
personal-data-bearing records on configurable, **per-family** windows
(GDPR Art.5(1)(e), storage limitation). Each window is measured from the record's
age. The defaults:

| Family | Default window | Default state | What a purge removes |
| --- | --- | --- | --- |
| **Sessions** (`Ended`/`Cancelled`) | 365 days | **off** | The session row; its `session_events`, `recaps` and session-scoped `visibility_rules` cascade away with it. |
| **Recaps** | 365 days | **off** | The generated recap body (host content). |
| **Exports** (`Completed`) | 90 days | **off** | The `export_jobs` row, its manifest and any object-storage blob (object first, then row; fail-closed if storage is unconfigured). |
| **Invitations** (terminal: accepted/revoked/expired) | 30 days | **on** | The plaintext `invited_email`. |
| **IdempotencyKeys** (CORE-PRIV-006) | 30 days | **on** | Aged `idempotency_keys` rows, by **count only** — see below. |

The deletions an operator would be surprised to lose (sessions, recaps, exports)
are **disabled by default** — enable them per family once the windows fit your
retention obligations — while the clear privacy-hygiene purges are **enabled by
default**: the invitation-email purge and the idempotency-key purge. Configure each
with `Retention:<Family>:Enabled` and `Retention:<Family>:RetentionWindow` (plus a
global `Retention:SweepInterval` / `Retention:BatchSize`); see `.env.example` and
`docs/13`. Every **tenant-scoped** purge is **audited by id** (`RecordRetentionPurged`),
with no actor and no content, so the audit hash chain still verifies (CORE-SEC-003).

The **idempotency-key purge** (CORE-PRIV-006) bounds the otherwise insert-only
`idempotency_keys` table — a row is written on every idempotent create/reveal/purchase
replay and was never reclaimed, so it grew without limit. The table is generic
retry-safety infrastructure: it is **not tenant-scoped** and its rows hold no host
content (only a server-composed `scope` partition and a client `key` correlation
token), so this purge deletes **by age alone** (a 30-day window, well beyond any
plausible client retry horizon) and is logged **by count, never by key value**. It
takes no per-record audit (there is no tenant subject to audit), and the bounded bulk
delete is idempotent and concurrency-safe.

**Not swept (append-only systems of record):** `audit_logs`, the purchase ledger
(`purchase_transactions` / `purchase_events` / `store_notification_events`) and
`session_events` other than via the session cascade above — these are backup
systems of record (`docs/13`, "Backup and restore").

## At-rest encryption expectations

**Decision (2026-06-17): at-rest encryption of the live data stores is the
operator's responsibility (storage-layer encryption); Core does not implement
application-layer / field-level encryption of PII columns. Backups are encrypted by
Core's own tooling.** Owner: **operator** (configuration). This is the
document-vs-build split the story calls for: documented-as-operator-responsibility
for the live database and bucket, built for backups.

What the operator must enable for at-rest confidentiality:

- **Database** — run PostgreSQL on encrypted storage (encrypted block volume /
  full-disk encryption, a managed-Postgres encryption-at-rest / TDE feature). This
  protects every personal-data column and index uniformly.
- **Object storage** — enable bucket server-side encryption (SSE) on the private
  asset bucket; keep it private (no public access/listing, threat T4, CORE-OPS-006).
- **Backups** — **already enforced by Core**: the backup/restore tooling encrypts
  the dump and the mirrored asset copy with AES-256-CBC + HMAC-SHA256 and **refuses
  to run without a passphrase** (CORE-DR-001), so the audit trail, purchase ledger
  and tenant data never land as plaintext.
- **In transit** — terminate TLS at the reverse proxy and keep OIDC discovery over
  HTTPS (`Authentication:Oidc:RequireHttpsMetadata=true`); CORE-OPS-003.

What Core does cryptographically today is **integrity/secret-hashing, not at-rest
confidentiality of PII**: invitation tokens are stored only as a **SHA-256 hash**
(`workspace_invitations.token_hash`, `WorkspaceInvitationToken.cs`), so a leaked
table never reveals a usable token (threat T6); and the audit log is sealed into a
per-tenant **SHA-256 hash chain** for tamper-evidence (CORE-SEC-003). The
PII columns (`users.email`, `users.display_name`, `participants.display_name`,
`workspace_invitations.invited_email`) are stored **plaintext** in the database and
rely on the storage-layer encryption above.

**Why field-level encryption is not built.** Storage-layer (volume / managed-DB)
encryption protects every column and index uniformly with no query, index or
erasure penalty, and the operator already controls it. Encrypting individual PII
columns inside the product would break the unique lookups the schema depends on
(the `invited_email` anonymization path and the `token_hash` unique index),
complicate the erasure/anonymization (CORE-PRIV-001) and access-export
(CORE-PRIV-004) paths, and move key-management into the product for marginal
benefit over encryption the deployment already runs. Art.17 erasure provides the
crypto-shredding-equivalent guarantee (a hard delete of the identity row). Revisit
only if a deployment needs per-field crypto-shredding beyond hard-delete; that would
be a new story with its own design.

## Consent / legal-basis recording decision

**Decision (2026-06-17): Core does not record per-subject consent or lawful basis.**
There is no consent table, no legal-basis field and no consent-capture surface in
Core. Owner: **controller / vertical** (final, not deferred).
Build-vs-document: documented-as-controller-responsibility — **not built**.

Rationale: the lawful basis under GDPR Art.6 (consent, contract, legitimate
interests, legal obligation, …) is determined by the **controller** and depends on
the deployment's purpose, audience and jurisdiction — none of which the
product-neutral Core can know or decide. Consent capture, its user experience and
the proof-of-consent record belong to the controller/vertical, alongside the
privacy notice and the records of processing. Core's role is to provide the
**mechanisms** a controller needs to honor data-subject rights *regardless of the
chosen basis*: access and portability (CORE-PRIV-004), erasure (CORE-PRIV-001),
storage-limitation retention (CORE-PRIV-003) and tenant offboarding
(CORE-PRIV-002). A vertical that needs consent records adds them in its own layer;
were Core ever to need a generic consent ledger it would be a new, product-neutral
story with its own table and route — recorded here so its absence reads as an
intentional decision, not a gap.

## Data-subject-rights mechanisms (what is built)

A self-hoster operationalizes the controller's obligations with these built-in
Core capabilities:

| Right (GDPR) | Core mechanism | Authorization |
| --- | --- | --- |
| Access (Art.15) / Portability (Art.20) | `GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export` (CORE-PRIV-004) — synchronous; OR a `ExportScope.UserData` export job produced by the worker and downloaded via `GET /api/v1/exports/{exportId}` (CORE-EXP-002) | The subject themselves, or `Owner`/`Admin` on their behalf; tenant-scoped, fail-closed. |
| Erasure (Art.17) | `DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data` (CORE-PRIV-001) | `Owner`/`Admin`; tenant-scoped authorization, global effect; audited by id. |
| Storage limitation (Art.5(1)(e)) | The data-retention sweep (CORE-PRIV-003), per-family windows above. | System job; audited by id. |
| Erasure of a whole tenant / offboarding | `DELETE /api/v1/organizations/{organizationSlug}` (CORE-PRIV-002) | `Owner` only; cascades the tenant; platform-level audit fact survives. |

Access and portability are served by **two paths that share one assembly**: the
synchronous `personal-data-export` route (CORE-PRIV-004) returns the subject's data
inline, while a `ExportScope.UserData` **export job** lets the same right be fulfilled
through the asynchronous Exports pipeline (CORE-EXP-002) — the worker drives the queued
job to terminal `Completed` (its producer), and the existing export download route
`GET /api/v1/exports/{exportId}` then discloses the subject's data. Both reuse
`PersonalDataExportService`, so the disclosed set, the tenant scoping and the
`PersonalDataExported` audit (by id, never the PII) are identical; the user-data export
is authorized to the subject themselves or an `Owner`/`Admin`, fail-closed, and — unlike
a workspace export's manifest — its personal data is never persisted in an artifact, only
assembled into the authorized response on download (threats T7/T8). It is DISTINCT from a
workspace export (which exports a workspace's content artifacts, not a subject's personal
data).

## Compliance posture checklist for the self-hoster

A controller deploying Core should:

1. **Place the data stores in the required region** — run PostgreSQL and the asset
   bucket (and their backups) in-region (see "Data residency").
2. **Enable at-rest encryption** on the database volume and the asset bucket, set
   a backup encryption passphrase (`Backup:Encryption:Passphrase`), and terminate
   TLS at the edge (see "At-rest encryption expectations").
3. **Set retention windows** that match your obligations and enable the families you
   need (the surprising deletions are off by default; see "Default retention
   windows").
4. **Wire the sub-processors you choose** (IdP, object storage, optional backplane /
   tracing / store adapter) under your own agreements; Core ships no credentials.
5. **Own the legal layer** — determine and record lawful basis, publish the privacy
   notice, capture consent where it is your basis, and maintain records of
   processing; Core does not record consent or legal basis (see the decision above).
6. **Honor data-subject requests** through the built-in access, erasure, retention
   and offboarding mechanisms (see "Data-subject-rights mechanisms").
7. **Apply the audit-log REVOKE** (`UPDATE`/`DELETE` on `audit_logs`, CORE-SEC-003)
   and run the backup/restore drill (CORE-OPS-010) so the systems of record are
   tamper-evident and recoverable.
