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
  "sourceUrl": "https://github.com/Live-Experience-Application/livecore-platform"
}
```

The build version is read from the running assembly, so the offer always identifies
the exact source revision deployed. A deployment that runs **modified** source must
offer **its own** Corresponding Source, so the offered location is
configuration-overridable with `SourceOffer:RepositoryUrl` (env
`SourceOffer__RepositoryUrl`); unset, it falls back to the canonical upstream
repository.

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
