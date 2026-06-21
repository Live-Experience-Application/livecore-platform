# Licensing Strategy

## Recommended starting point

Core may be licensed AGPL-3.0-or-later if you want a strong open-source/self-hosting model.

## Important warning

AGPL can affect network software and modified server-side deployments. The dual-license strategy for proprietary/enterprise offerings is no longer a "plan for later": it is **decided and recorded below** (the _Commercial and dual-license decision (CORE-LIC-002)_ section) — the Core is dual-licensed, with a commercial license available now for proprietary use.

This is not legal advice.

## Practical strategy

```text
livecore-platform
  AGPL-3.0-or-later + commercial license available for proprietary use (CORE-LIC-002)

arcanos-app
  AGPL if open source; commercial/license review if you want a closed commercial app

scenarioos-enterprise
  private until legal strategy is confirmed

livecore-deploy
  align with Core or use a compatible documentation/deployment license
```

## What the Core's AGPL license means for a consuming vertical (CORE-LIC-001)

The Core Platform is licensed **AGPL-3.0-or-later** (`LICENSE`; the SPDX identifier
on the source, e.g. `SystemModule/SourceOffer.cs`, and on all four published
packages). This section states precisely what that license means for a **vertical
app built on the Core** — a separate product such as `arcanos-app` or
`scenarioos-enterprise` that consumes the Core's packages and/or its hosted API. It
is the consumer-facing companion to the commercial/dual-license decision
(CORE-LIC-002). **This is not legal advice.**

### Importing the packages links your app against AGPL code

The four published TypeScript packages — `@livecore/contracts`, `@livecore/sdk-ts`,
`@livecore/ui-core` and `@livecore/design-tokens` — are each declared
`AGPL-3.0-or-later` (the `license` field of their `package.json`). Importing any of
them into a vertical app makes that app a **work based on** the Core: your app links
against AGPL-licensed code, so the combined work is a derivative governed by the
AGPL. By default that obligates you to license your vertical app under
AGPL-3.0-or-later (or a compatible license) and to make its **complete Corresponding
Source** available to its users on the same terms.

This applies to the type-only `@livecore/contracts` import as well: the package
carries the AGPL identifier, so a closed-source importer is, by default, obligated to
release source. A vertical that does not want that obligation needs a **commercial
license** (CORE-LIC-002), not the AGPL grant.

### Deploying the Core API over a network triggers AGPL section 13

The Core API and worker are **network-interactive** (the `/api/v1` surface plus the
SignalR hub). AGPL-3.0 **section 13** therefore obliges any deployment that lets
remote users interact with the software over a network to **offer those users the
Corresponding Source** of the exact version running — even when no binary is ever
distributed. An **unmodified** upstream deployment already discharges this with the
anonymous `GET /source` offer (CORE-CMP-001, below). A deployment that runs
**modified** Core source must offer **its own** Corresponding Source (point
`SourceOffer:RepositoryUrl` at the repository that serves it).

Crucially, once your vertical app imports the packages, section 13 applies to the
**whole deployed app**, not just the embedded Core: hosting that app for network
users obliges you to offer the Corresponding Source of the app, not only of the Core
it builds on.

### Permitted consumption modes vs. modes that require a commercial license

The following consumption modes are **permitted under the AGPL grant alone**, with no
separate license — each carries the AGPL obligation named beside it:

| Consumption mode                                                       | AGPL obligation you must meet                                                                                                                                |
| ---------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Self-host the **unmodified** Core API/worker for network users         | Keep the anonymous `GET /source` offer reachable; it points remote users at the upstream Corresponding Source (section 13).                                  |
| Run a **modified** Core over a network                                 | License your modifications AGPL-3.0-or-later and point `SourceOffer:RepositoryUrl` at the repository serving your modified Corresponding Source (section 13). |
| Build a vertical on the packages and **release it open source**        | License the vertical AGPL-3.0-or-later (or a compatible license) and offer its complete Corresponding Source to its users; a network deployment owes section 13 for the whole app. |
| **Internal-only** use (no third party interacts over a network; nothing conveyed) | None beyond preserving notices — AGPL obligations attach on conveying or on network interaction by users other than you.                          |

The following modes are **not** available under the AGPL grant and require a
**commercial license** (CORE-LIC-002):

| Consumption mode                                                                       | Why the AGPL grant is insufficient                                                                                              |
| -------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| A **closed-source / proprietary** vertical that imports any `@livecore/*` package      | Linking against AGPL code makes the app a derivative; the AGPL would require releasing the app's source, which a proprietary product will not do. |
| Offering the Core (or a vertical built on it) as a **hosted service without offering Corresponding Source** | Section 13's network source offer cannot be waived under the AGPL; declining it requires an alternative grant.   |
| **Embedding or redistributing** the Core inside a proprietary product conveyed to others | Conveying a work based on AGPL code obliges AGPL source disclosure to the recipients.                                          |

The commercial/dual-license path itself — whether it is offered, by whom, and how to
obtain terms — is decided and recorded by CORE-LIC-002 (the _Commercial and
dual-license decision_ section below); the `README.md` License section carries the
current contact for commercial inquiries.

### Trademark: the AGPL grant does not license the LiveCore name

The AGPL is a **copyright** license. It grants rights to use, modify and convey the
**software**; it grants **no rights to the "LiveCore" name, logo or other
trademarks** (AGPL section 7(e) expressly permits declining to grant trademark
permission). You may state, factually, that your product is "built on the LiveCore
Core" or "compatible with LiveCore", but you may **not** use the LiveCore name or
marks to brand your own product, as a product or company name, or in any way that
implies endorsement. Any trademark permission is separate from, and is not implied
by, the AGPL copyright grant.

## Commercial and dual-license decision (CORE-LIC-002)

This is the **single source of truth** for the Core's commercial/dual-license model
— the decision the CORE-LIC-001 consumer section above defers to ("whether it is
offered, by whom, and how to obtain terms"). It is a **dated decision record** in
the style of the `docs/24_SPEC_CONSISTENCY.md` decision register. **This is not
legal advice.**

**Decision (recorded 2026-06-17): the Core adopts a dual-license model —
AGPL-3.0-or-later by default, with a commercial license available on request.** The
default, public license stays **AGPL-3.0-or-later** (`LICENSE`; the SPDX identifier
on every first-party source file and on all four published packages). In addition, a
**separate commercial license is offered** for the uses the AGPL grant does **not**
permit — exactly the modes the CORE-LIC-001 table above lists as "not available under
the AGPL grant":

- a **closed-source / proprietary** vertical that imports any `@livecore/*` package;
- **hosting** the Core (or a vertical built on it) as a service **without** offering
  the AGPL section 13 Corresponding Source;
- **embedding or redistributing** the Core inside a proprietary product conveyed to
  others.

The project is therefore **explicitly not AGPL-only**: a non-AGPL vertical has a
**defined legal path**, not a documented "no".

**The named non-AGPL vertical has a path, not a refusal.** The closed-commercial /
enterprise case — a `scenarioos-enterprise`-style closed product, or a closed-source
`arcanos-app` — that cannot accept the AGPL obligations obtains the **commercial
license** instead of complying with the AGPL. That commercial license **is** the
alternative grant. It is a **bespoke commercial agreement**, not a public,
SPDX-expressible license, so it does **not** appear as an SPDX `LicenseRef` or
alternative identifier on the source or the packages — those stay
`AGPL-3.0-or-later`, and the alternative terms are granted privately by the terms
owner. (This is why a reader sees only `AGPL-3.0-or-later` in the repository: the
commercial grant lives in a signed agreement, not in an SPDX field.)

- **Terms owner.** The **LiveCore copyright holder** — the project maintainer,
  reachable at `singh.harwinder@outlook.com` — owns the upstream copyright and is the
  **only** party that can grant a non-AGPL license. The terms owner sets, negotiates
  and issues the commercial license agreement.
- **Contact-to-terms path.** Commercial-licensing inquiries go to
  **`singh.harwinder@outlook.com`** (the `README.md` License section carries the same
  contact). The terms owner responds with the commercial license agreement; there is
  no public price list or self-service portal — terms are issued per inquiry.

**A contributor CLA (CORE-LIC-004) is a prerequisite for ever relicensing.** The
project may grant a commercial (non-AGPL) license **only over code whose copyright it
holds, or has been granted the right to relicense.** The codebase is **first-party
today** (a single copyright holder), so the terms owner can already grant commercial
terms over the whole of it. But the moment **third-party contributions** are
accepted, offering them under non-AGPL terms requires each contributor to have
**granted that relicensing right** in advance. A **contributor CLA (CORE-LIC-004)** —
the contributor IP policy that secures that right — is therefore a **prerequisite for
ever relicensing contributed code**. Until that CLA is in place and signed, the
commercial license covers **first-party code only**; a third-party contribution must
be **CLA-covered** (or removed/reimplemented) before it can be included under the
commercial license. This is the conceptual dependency on CORE-LIC-004.

**Scope and status.** This is a licensing **decision record**, documentation only: it
adds no route, table, event, migration or Core source change, so the boundary scan and
the doc/CSV spec-consistency checks stay green. Owner of the decision: the **terms
owner** above. The dual-license model itself is **final and in effect now** (not
deferred); only the relicensing of **contributed** code waits on the CLA
(CORE-LIC-004).

## Contributor IP policy and SPDX source headers (CORE-LIC-004)

The dual-license decision (CORE-LIC-002, above) records that **a contributor IP
policy is a prerequisite for ever relicensing contributed code**: the project may
grant the commercial (non-AGPL) license only over code whose copyright it holds or
has been granted the right to relicense. This section is that policy. The operative
document for contributors is `CONTRIBUTING.md`; this is the licensing rationale.
**This is not legal advice.**

### The model: DCO sign-off plus a dual-license grant

The project uses the **Developer Certificate of Origin (DCO) 1.1**
(`DEVELOPER_CERTIFICATE_OF_ORIGIN`) — a lightweight, per-commit, machine-checkable
certification — rather than a separately administered, signed CLA. Every commit
must carry a `Signed-off-by: Name <email>` trailer whose email matches the commit
author (`git commit -s`), certifying the contributor's right to submit the work
under the project license. This gives clean, auditable **provenance**.

Because the trailer's email must match the **commit author**, the way a maintainer
merges a pull request matters. A **squash merge** re-authors the resulting commit with
the merger's GitHub commit email — the private `…@users.noreply.github.com` form when
"Keep my email address private" is on — so a sign-off carrying the contributor's
ordinary email no longer matches and the lint fails on the squashed commit. Merge with
**rebase** (or a merge commit) instead: both preserve each original, already-signed
commit verbatim, so the author and its sign-off stay in agreement. If a squash is
preferred, sign off with the GitHub commit email the squash will carry, or turn off
email privacy so the squash uses the verified address the sign-off already names.

A bare DCO certifies provenance but does **not**, by itself, grant the project the
right to relicense a contribution. So `CONTRIBUTING.md` pairs the DCO sign-off with
an explicit **contribution license grant**: by signing off under the policy, the
contributor (a) contributes under AGPL-3.0-or-later (inbound = outbound) and (b)
grants the LiveCore copyright holder the perpetual, non-exclusive, worldwide,
royalty-free right to **also** license the contribution under the project's
commercial license. The contributor keeps their copyright; the grant only preserves
the project's ability to offer the **same** code under both the AGPL and the
commercial license.

This is what **discharges the CORE-LIC-002 prerequisite**: with the sign-off and
grant in place, a third-party contribution is covered by the commercial license the
same way first-party code is, so the dual-license model stays coherent as soon as
contributions are accepted — no reimplementation required. A contribution made
without the grant (stated explicitly in its pull request) stays AGPL-only and must
be isolated, removed or reimplemented before it can ship under the commercial
license.

### SPDX source headers

So that license context travels with a file even when it is copied out of the
repository, every first-party, hand-authored **source file that ships in a
distribution artifact** carries a two-line SPDX header:

```text
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) <year> The LiveCore Platform contributors
```

The same `//` header is used for the **C# (`.cs`)** that builds the API/worker
container images and the **TypeScript (`.ts`/`.tsx`)** that builds the published
packages. The copyright line names a **collective holder** ("The LiveCore Platform
contributors"): contributors retain their own copyright under the DCO, and the
collective notice is the honest, future-proof attribution. The machine-readable
`SPDX-License-Identifier` (rather than only a prose notice) makes every file's
license unambiguous to scanners and to anyone who reuses the file.

**Out of scope** (no header; the lint excludes them): generated source — the EF
Core migrations under `apps/api/Persistence/Migrations` (scaffolded by the EF
tooling) and the generated, drift-gated OpenAPI contract types
(`packages/contracts/src/openapi.ts`, CORE-OAS-002); build output and `*.d.ts`
declaration files; and first-party tooling that is **not shipped** in an artifact
(the PowerShell scripts under `scripts/` and the `.mjs` build/test helpers). The
exact scope is the single source of truth in
`scripts/LiveCoreContributorPolicy.psm1`.

### CI enforcement

The `contributor-policy` CI job (`.github/workflows/ci.yml`) enforces both controls
on every push and pull request, fail-closed:

- **DCO sign-off** — `scripts/lint-dco-signoff.ps1` validates that every commit
  introduced by the push/PR carries a matching `Signed-off-by` trailer (merge
  commits exempt). It validates only the new commits (the base..head range), not the
  project's whole history, so it binds new contributions without retroactively
  failing pre-policy commits.
- **SPDX headers** — `scripts/lint-license-headers.ps1` validates that every
  in-scope shipped source file carries the header (and inserts missing headers with
  `-Fix`).
- **The gate logic is itself tested** — `scripts/test-contributor-policy.ps1`
  proves on every run that an unsigned commit and a headerless source file are
  rejected, and that a signed-off commit and a headered file pass (the CORE-LIC-004
  acceptance test). All logic lives in `scripts/LiveCoreContributorPolicy.psm1`.

Unlike the supply-chain and coverage gates, which ramped in report-only before
turning blocking (`docs/17`), this gate is **blocking from the start**: provenance
and headers are binary, low-risk checks with no transitive-dependency surprises to
ramp in.

## AGPL section 13 source offer (CORE-CMP-001)

Because the Core is AGPL-3.0-or-later and network-interactive (the SignalR hub and
the `/api/v1` surface), AGPL-3.0 section 13 obliges a hosted deployment to offer
remote users access to its Corresponding Source. The API host satisfies this with a
small, anonymous endpoint:

| Endpoint      | Purpose                                                                                                       |
| ------------- | ------------------------------------------------------------------------------------------------------------ |
| `GET /source` | Offers the Corresponding Source: the SPDX license, the running build version and where the source is hosted. |

The response is JSON and requires no authentication — the offer is owed to every
remote user the application interacts with:

```json
{
  "license": "AGPL-3.0-or-later",
  "version": "<running build version>",
  "sourceUrl": "https://github.com/Live-Experience-Application/livecore-platform/tree/v<running build version>"
}
```

The build version is read from the running assembly, so the offer always identifies
the exact source revision deployed.

### The offer is pinned to the running revision (CORE-LIC-005)

Section 13 obliges the offer to resolve to the Corresponding Source of the **exact
version running**, so `sourceUrl` is **pinned to the running revision** rather than
the repository root: it is the repository pinned to the build version's release tag,
`<repository>/tree/v<version>` (e.g.
`https://github.com/Live-Experience-Application/livecore-platform/tree/v1.2.3`). The
version is the single build version the release pipeline stamps (reused via
`ResolveBuildVersion`), and the pipeline tags each release `v<version>`, so the
pinned URL resolves to that tag's tree — a remote user can fetch the source that is
actually running, not whatever the default branch later becomes.

### A modified deployment cannot silently keep offering upstream (CORE-LIC-005)

A deployment that runs **modified** source must offer **its own** Corresponding
Source, so the offered repository is configuration-overridable with
`SourceOffer:RepositoryUrl` (env `SourceOffer__RepositoryUrl`); unset, it falls back
to the canonical upstream repository (still revision-pinned). To stop a modified
deployment **silently** keeping the upstream offer, the configuration is **validated,
fail-closed**, by the documented rule:

- A deployment that **declares modified source** by setting `SourceOffer:Modified`
  (env `SourceOffer__Modified`) to `true` **must also set** `SourceOffer:RepositoryUrl`
  to its own Corresponding Source. If it leaves the repository unset, the host
  **refuses to start** — rather than silently offering the canonical upstream source
  as if it were the modified source running there.
- A configured `SourceOffer:RepositoryUrl` must be an **absolute http(s) URL**; a
  malformed value would offer a location no remote user can fetch, so the host
  **refuses to start**.

This mirrors the OIDC audience startup guard (CORE-OPS-004): a configuration foot-gun
that would otherwise serve a wrong value is refused at startup. An **unmodified**
deployment leaves both keys unset and always starts, offering the revision-pinned
canonical upstream source. The rule is exercised end-to-end against the real host by
`SourceOfferStartupGuardTests`.

Like `/health/*` and `/metrics`, `/source` is a top-level infrastructure route, not
part of the versioned `/api/v1` product surface (so it is not a row in
`csv/api_routes.csv`). It exposes only the license, a build version and a public
repository URL — never a token, tenant identifier, configuration value or resource
content (threat T7 in `docs/07_SECURITY_THREAT_MODEL.md`).

## Dependency review

Every new dependency must be checked for:

- license compatibility
- maintenance status
- security posture
- necessity

## Third-party attribution and the license-compliance gate (CORE-LIC-003)

The Core is AGPL-3.0-or-later and **redistributes** third-party code in two
shipped artifacts — the container images and the published npm packages. AGPL
section 7 and (for the Apache-2.0 dependencies such as `AWSSDK.S3` and the
`OpenTelemetry.*` packages) Apache section 4 require **preserving the copyright and
permission notices** of those dependencies on redistribution. Three controls make
the distribution legally complete and license-checked:

### A generated third-party NOTICE inventory

`THIRD-PARTY-NOTICES.md` is a **generated** attribution inventory of the runtime
NuGet (and npm) dependencies. Its single source of truth is
`csv/third_party_notices.csv`; `scripts/generate-third-party-notices.ps1` renders
the CSV into the committed `THIRD-PARTY-NOTICES.md` and keeps a copy in each
published package. Do **not** edit the Markdown by hand — run the generator with
`-Write` and commit. The inventory **ships in both** distribution artifacts:

- the API and worker container images carry it (and the AGPL `LICENSE`) under
  `/licenses` (`apps/api/Dockerfile`, `apps/worker/Dockerfile`), and
- each package tarball lists `LICENSE` and `THIRD-PARTY-NOTICES.md` in its
  `files[]` (`packages/*/package.json`), so a consuming vertical receives both.

The authoritative, **per-build** component list for a published image is its
CycloneDX SBOM (CORE-DEP-003); the NOTICE is the curated human-readable
attribution that travels with the artifact.

The inventory is **drift-gated** in CI (the `license-compliance` job runs the
generator in check mode) and **coverage-gated**: every direct, runtime-shipping
NuGet `PackageReference` in the API/worker projects must have an attribution row,
so a newly added attribution-requiring dependency cannot ship without a notice. A
build/design-only reference (`<PrivateAssets>all</PrivateAssets>`, e.g. the EF Core
design package) never lands in the runtime output and is excluded.

The per-package `LICENSE` and `THIRD-PARTY-NOTICES.md` reproduce verbatim
license/notice text, so the boundary scan excludes them by file name (they would
otherwise trip on words such as "party" that the verbatim AGPL text and the
`THIRD-PARTY-NOTICES` name contain); the scanner also treats the standard compound
"third-party" as legitimate in Core source (`scripts/boundary-scan.ps1`).

### OCI image license labels

Both runtime images declare their license to any registry or scanner with the OCI
`org.opencontainers.image.licenses="AGPL-3.0-or-later"` label, alongside
`org.opencontainers.image.source` (the upstream repository) and
`org.opencontainers.image.revision` (the build commit, passed as
`--build-arg SOURCE_REVISION`). The `docker` CI job asserts the license label is
present and that the `LICENSE`/`THIRD-PARTY-NOTICES.md` ship in the image.

### A CI license-compliance gate

`scripts/assert-license-compliance.ps1` (logic in
`scripts/LiveCoreLicenseCompliance.psm1`) scans the dependency closure recorded in
the image's CycloneDX/SPDX **SBOM** — reusing the SBOM CORE-DEP-003 already
produces — and is **fail-closed**: a license on the deny-list blocks, and any
license **not** on the allow-list (including an absent or `NOASSERTION` license) is
treated as **unknown** and blocks. The allow-list (permissive plus the
AGPL-compatible licenses common in the .NET/Debian closure) and deny-list are
configurable.

Like the coverage gate (`docs/17`), the gate over the real SBOM **starts
report-only** in the `publish-dry-run` and `publish` jobs so a first real SBOM
documents any not-yet-allow-listed license without blocking the initial releases;
drop `-ReportOnly` to make a disallowed or unknown license block the publish once
the allow-list is validated against the published closure. The gate **decision** is
pure logic, proven by `scripts/test-license-compliance.ps1` (a seeded disallowed
license fails the gate) on every push and pull request, so the failure behavior is
guaranteed regardless of the live posture.
