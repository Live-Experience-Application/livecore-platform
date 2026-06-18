# ADR 0015: Default to RustFS over MinIO for the Object Store

## Status

Accepted for initial implementation. First applied by CORE-STOR-001 (the full local stack overlay) and
CORE-STOR-002 (the storage and self-hosting docs).

## Context

Core stores assets behind an **S3-compatible abstraction** (ADR 0006): the Assets port `IAssetStorage`
and its concrete `S3CompatibleAssetStorage` speak the standard S3 protocol over the AWS SDK, and the
object-storage endpoint and credentials are supplied by the deployment through configuration only
(`docs/12_STORAGE_ASSETS.md`, `docs/13_SELF_HOSTING_REQUIREMENTS.md`). So **any** S3-compatible backend
works, and no object-store code or credential lives in Core — nothing in the shipped API/worker images
or the published `@livecore/*` packages embeds an object store.

But the in-repo operator stack still has to present a **default example** backend so an operator can run
an authenticated, asset-serving Core locally out of the box. The optional full local stack overlay
(`deploy/compose/docker-compose.full.yml`, CORE-DEP-006) bundled **MinIO** as that default, and the
storage/self-hosting docs named MinIO as the example provider.

MinIO is licensed **AGPL-3.0**. **RustFS** is an S3-compatible object store under the more permissive
**Apache-2.0** license with comparable coverage of the S3 operations the adapter uses (PutObject,
GetObject, SigV4 pre-signed GET/PUT, DeleteObject). Steering the default operator stack and the docs to
an AGPL component is a heavier license posture than necessary for an external dependency the operator
merely runs alongside Core, and a weaker long-term-value bet than an Apache-2.0 equivalent.

The license concern here is specifically about **the operator stack and the default we steer them to**,
**not** a license obligation on the shipped LiveCore distribution. The object store runs as an
**external container** — a separate process the operator runs, reached only over the S3 **network
protocol**. Core neither links it nor redistributes it inside its images or package tarballs, so neither
MinIO's AGPL nor RustFS's Apache-2.0 attaches to the LiveCore distribution itself (which stays
AGPL-3.0-or-later regardless; ADR 0009). This is why the object store appears in **neither**
`THIRD-PARTY-NOTICES.md` **nor** `csv/third_party_notices.csv`: that inventory attributes only the
components actually **redistributed** in the images and tarballs — the shipping NuGet/npm dependencies,
drift- and coverage-gated by CORE-LIC-003 (`scripts/generate-third-party-notices.ps1`,
`scripts/test-distribution-compliance.ps1`).

The choice this ADR records is therefore narrow: **which S3-compatible backend should the in-repo
operator stack and the docs present as the default example?**

- **Keep MinIO (AGPL) as the bundled default.** Rejected: a more permissive Apache-2.0 alternative with
  comparable S3 coverage exists, and steering operators to AGPL tooling by default is avoidable friction
  and a worse long-term-value bet.
- **Default to RustFS (Apache-2.0).** Chosen.
- **Bundle no default and force every operator to bring their own.** Rejected: the full local stack
  overlay's whole point (CORE-DEP-006) is a one-command authenticated, asset-serving Core; removing the
  default backend breaks that.

## Decision

**The in-repo operator stack and the docs DEFAULT to RustFS (Apache-2.0) as the example S3-compatible
object store, in place of MinIO (AGPL).**

1. The **S3-compatible abstraction (ADR 0006) is unchanged** and remains the architectural decision.
   RustFS is only the **default example** backend the in-repo stack bundles; Core speaks the standard S3
   protocol, so an operator may still bring **any** S3-compatible provider. The docs keep that
   any-S3-compatible contract explicit (CORE-STOR-002).
2. The storage **tooling stays off AGPL components** too: the overlay's one-shot bucket bootstrap uses
   the Apache-2.0 AWS CLI, not MinIO's AGPL `mc` (CORE-STOR-001).
3. The object store stays an **external, digest-pinned container** the operator runs (CORE-STOR-001),
   **not** a component bundled into the shipped LiveCore images or published packages. Consequently the
   AGPL-vs-Apache distinction is about the **default the operator stack steers to**, not a license
   obligation on the LiveCore distribution, and the object store is deliberately **not** an entry in
   `THIRD-PARTY-NOTICES.md` / `csv/third_party_notices.csv`. Swapping MinIO for RustFS adds and removes
   **nothing** there, so the license-compliance and notice-drift gates are unaffected and stay green.

## Consequences

- All future storage-stack work follows this default: the in-repo stack and the docs present **RustFS**
  as the example S3-compatible backend, while keeping the any-S3-compatible contract (ADR 0006) explicit
  so operators can bring their own provider.
- Because the object store is **external and not redistributed**, the third-party attribution inventory
  and the per-image SBOM scope (CORE-DEP-003) are **unchanged**: no attribution row is added or removed,
  and the license-compliance gate (`generate-third-party-notices.ps1` check plus
  `test-distribution-compliance.ps1`) and the notice-drift gate stay green.
- A `storage-docs` grep backstop (CORE-STOR-002, `scripts/lint-storage-docs-rustfs.ps1`) keeps the
  storage/self-hosting docs from regressing to MinIO setup guidance; this ADR records the **why** behind
  that gate.
- This ADR **complements, and does not supersede, ADR 0006** (the S3-compatible abstraction stays) and is
  consistent with **ADR 0009** (the LiveCore distribution stays AGPL-3.0-or-later; the external object
  store's license does not attach to it).
- Any LLM-proposed change — reverting the default to MinIO, **bundling** the object store **into** the
  shipped images (which would pull its license into the distribution and require an attribution row), or
  dropping the any-S3-compatible contract — requires a new ADR and human approval.
