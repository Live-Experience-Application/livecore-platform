# Vertical Adopter Integration Guide

This is the **self-contained getting-started guide** for a vertical author building a
product on the LiveCore Core Platform. It walks you from installing the published
`@livecore` packages to authenticating, creating a workspace and session, and
**driving a reveal end to end** — the smallest complete slice of what the Core does.

It builds on the worked, CI-exercised example in
[`examples/minimal-consumer`](../examples/minimal-consumer/README.md): that example is
your known-good starting point (install + authenticate + call a read route), and this
guide extends it into the full author flow.

**You can succeed without reading the full spec set.** Everything you need to make a
first reveal is here. The deeper specs are linked at the end
([Going deeper](#going-deeper-the-spec-set)) for when you need them — not before you
start.

## Who this is for and what you will build

The Core is a product-neutral **Live Experience Engine**: one foundation that multiple
vertical products build on (`docs/00_START_HERE.md`). A **vertical** is your own app —
in your own repository — that consumes the Core's typed packages and maps its generic
vocabulary (Organization, Workspace, Session, Scene, ContentBlock, Reveal, …) to your
own domain language in your own UI (`docs/03_DOMAIN_LANGUAGE.md`,
`docs/04_PRODUCT_BOUNDARIES.md`).

By the end of this guide your vertical will, against a running Core:

1. install `@livecore/sdk-ts` and `@livecore/contracts`,
2. authenticate with an OIDC bearer token and construct the typed client,
3. create an organization, a workspace and a session,
4. prepare a content block and **reveal it to the session audience**, and
5. handle a denial as a typed error.

## Before you adopt: the license implication

The Core is **AGPL-3.0-or-later and dual-licensed** (CORE-LIC-002). This affects
whether you can adopt it the way you intend, so decide it first. The authoritative,
consumer-facing treatment is `docs/16_LICENSING.md`; the essentials
([README "What the AGPL means if you build on the Core"](../README.md#what-the-agpl-means-if-you-build-on-the-core)):

- **Importing any package links your app against AGPL code.** All four packages
  (`@livecore/contracts`, `@livecore/sdk-ts`, `@livecore/ui-core`,
  `@livecore/design-tokens`) are declared `AGPL-3.0-or-later`. Importing even the
  type-only `@livecore/contracts` makes your vertical a work based on the Core, so by
  default you must license the vertical AGPL-3.0-or-later and offer its complete
  Corresponding Source to its users.
- **Deploying the Core API over a network triggers AGPL section 13.** The `/api/v1`
  surface and the realtime hub are network-interactive, so a hosted deployment must
  offer remote users the Corresponding Source of the running version.
- **A closed-source vertical, or hosting as a service without offering source, needs
  the commercial license** rather than complying with the AGPL. Self-hosting,
  AGPL-licensed open-source verticals and internal-only use are permitted under the
  AGPL grant alone. The commercial-licensing contact is in `docs/16_LICENSING.md`.
- **Trademark.** The AGPL grants no rights to the "LiveCore" name; you may state
  factually that your product is built on the Core, but may not brand with the name.

## Prerequisites

- **Node.js 22** and **pnpm 10** for your vertical (the package manager is your
  choice in your own repo; this guide uses pnpm).
- **A running Core deployment** with **OIDC configured**, and a **bearer access token**
  your OIDC provider issued for a user who is (or may create) a member of the tenant
  you target. The SDK never mints a token — authentication is OIDC-first
  (`docs/adr/0005-oidc-first-authentication.md`).

You do **not** need the Core source to build a vertical; you consume the published
packages. The steps below assume you run them from your own vertical project.

## Point at a running Core

You need a Core API origin to call and a token to call it with. For a turnkey local
setup, the in-repo Compose stack (`deploy/compose`) brings up PostgreSQL, the
migrations runner, the API and the worker; the opt-in overlay
`docker-compose.full.yml` (CORE-DEP-006) adds a **pre-wired Keycloak (OIDC) + RustFS
(S3-compatible storage) + Valkey (realtime backplane)** and a `livecore` realm, so you
get a real OIDC provider and a configured API without wiring one by hand. From
`deploy/compose`:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

The bundled base stack leaves `Authentication__Oidc__*` unset, so authenticated routes
answer `401` until OIDC is configured — the full overlay configures it for you. See
`deploy/compose/README.md` and `docs/13_SELF_HOSTING_REQUIREMENTS.md`.

The Core API origin in this guide is `http://localhost:5062`. Use **HTTPS in any real
deployment** — the bearer token is a secret in transit.

## Step 1 — Install the packages

A vertical in its own repository installs the published packages from the registry and
imports the typed surface:

```bash
pnpm add @livecore/sdk-ts @livecore/contracts
```

The four packages are released **together (lockstep)** and follow Semantic Versioning,
so they always share one version (`docs/23_PACKAGE_VERSIONING.md`). Each exports its
release as a runtime value so you can pin exactly which Core release you run against:

```ts
import { PACKAGE_NAME, VERSION } from "@livecore/sdk-ts";

console.log(`Running against ${PACKAGE_NAME}@${VERSION}.`);
```

## Step 2 — Authenticate and construct the client

The SDK is a **typed transport, not a security boundary**: it carries your OIDC bearer
token on every request and authorization is enforced server-side
(`docs/07_SECURITY_THREAT_MODEL.md`). You supply a token from your own login flow; the
provider may be async (for example, to refresh an expired token).

```ts
import { LiveCoreClient } from "@livecore/sdk-ts";

const client = new LiveCoreClient({
  // The Core API origin, WITHOUT the /api/v1 prefix (the SDK appends it).
  baseUrl: "http://localhost:5062",
  // The SDK never mints a token; supply one from your OIDC login flow.
  getAccessToken: () => process.env.LIVECORE_ACCESS_TOKEN ?? "",
});
```

A non-success response is surfaced as a typed `LiveCoreApiError` carrying the HTTP
status and the RFC 7807 Problem Details — **never** the token or request body. Wrap
calls and branch on it:

```ts
import { isLiveCoreApiError } from "@livecore/sdk-ts";

try {
  const principal = await client.identity.getCurrentPrincipal();
  console.log(principal.user.displayName ?? principal.user.subject);
} catch (error) {
  if (isLiveCoreApiError(error)) {
    // A 404 may be a genuine not-found OR a fail-closed hidden resource — never
    // infer existence from it (docs/06_AUTHORIZATION_MATRIX.md).
    console.error(`Core request failed (HTTP ${error.status}).`);
  } else {
    throw error;
  }
}
```

This authenticate-and-read slice is exactly what `examples/minimal-consumer` does —
run that first ([Run the worked example first](#run-the-worked-example-first)).

## Step 3 — Create a workspace and a session

The **organization** is the product-neutral tenant root; you may only create the tenant
your token is scoped to. A **workspace** is the container for a live experience, and a
**session** is a prepared or live run of it. A session is always created `Prepared`;
the only way into the live timeline is the guarded `start` command.

```ts
import type {
  OrganizationResponse,
  WorkspaceResponse,
  SessionResponse,
} from "@livecore/contracts";

// 1. Create (or reuse) the tenant root your token is scoped to.
const org: OrganizationResponse = await client.organizations.create({
  slug: "acme",
  name: "Acme Experiences",
});

// 2. Create a workspace in that tenant.
const workspace: WorkspaceResponse = await client.workspaces.create({
  organizationSlug: org.slug,
  slug: "first-experience",
  name: "First Experience",
});

// 3. Create a session — always created Prepared.
const session: SessionResponse = await client.sessions.create(workspace.id, {
  organizationSlug: org.slug,
  title: "Opening run",
});
```

## Step 4 — Prepare content and drive a reveal

A **reveal** is the Core's defining action: it decides what becomes visible to which
audience, when. Prepare a **scene** and a **content block** (host content, hidden from
the audience until revealed), start the session, then reveal the block to the whole
audience.

The reveal command is **idempotent**: you supply an `Idempotency-Key` and a retry with
the same key produces no duplicate effect. Reuse one key for one logical reveal across
all its retries — a fresh key on every retry would defeat idempotency.

```ts
import type {
  SceneResponse,
  ContentBlockResponse,
  RevealResponse,
} from "@livecore/contracts";

// Prepare something to reveal: a scene with a text content block.
const scene: SceneResponse = await client.scenes.create(workspace.id, {
  organizationSlug: org.slug,
  title: "Act One",
});

const block: ContentBlockResponse = await client.content.createBlock(
  scene.id,
  { organizationSlug: org.slug },
  { type: "Text", body: "The doors open." },
);

// Start the live timeline.
await client.sessions.start(session.id, { organizationSlug: org.slug });

// Reveal the content block to the whole audience (idempotently).
const reveal: RevealResponse = await client.visibility.reveal(
  session.id,
  {
    organizationSlug: org.slug,
    resourceType: "ContentBlock",
    resourceId: block.id,
  },
  { idempotencyKey: "reveal-act-one-block-1" },
);

console.log(
  `Revealed ${reveal.resourceType} ${reveal.resourceId} (${reveal.outcome}).`,
);
```

That is a reveal end to end: install → authenticate → organization → workspace →
session → scene → content block → reveal. To reveal to **one** participant instead of
the whole audience, pass that participant's `participantId` in the request body.

## (Optional) what a participant sees

A reveal is session-scoped, and the audience side reads its **already-filtered** view —
participants never receive host content until it is revealed (threat T2). A participant
app reads one participant's private feed within a session:

```ts
const feed = await client.visibility.getParticipantVisibleFeed(participantId, {
  organizationSlug: org.slug,
  sessionId: session.id,
});
```

The `participantId` comes from the session's participant roster/presence
(`docs/08_API_CONTRACTS.md`). Listing visibility rules and the full participant model
are authoring-role-only surfaces; see the API contracts for the complete set.

## Run the worked example first

Before wiring your own project, run the known-good base. `examples/minimal-consumer`
does Steps 1–2 against a live Core and is built in CI against the **published package
surfaces**, so it always matches the shipped shape. From the repository root:

```bash
# Build the workspace packages and the example.
pnpm --recursive run build

# Point at the Core origin and supply a bearer token, then run.
LIVECORE_API_BASE_URL=http://localhost:5062 \
LIVECORE_ACCESS_TOKEN=<oidc-bearer-token> \
  pnpm --filter @livecore-examples/minimal-consumer start
```

It prints the Core package release it runs against, the authenticated principal and its
organization memberships, and reports a denial as a typed `LiveCoreApiError`. Its
[`README`](../examples/minimal-consumer/README.md) has the full run instructions and
the worked source. Copy its `src/quickstart.ts` shape into your vertical, then add the
workspace/session/reveal calls from Steps 3–4.

## What the operator must supply (not Core)

The Core is **operator-run software, not a managed service**
(`docs/25_PRIVACY_AND_DATA_PROTECTION.md`). Some capabilities are deliberately the
deployment operator's (or your vertical's) responsibility, and the relevant routes
**fail closed** until the operator supplies them. Know these before you build a flow
that depends on them:

- **An object-storage adapter is operator-supplied; asset routes are `503` until it is
  configured.** Core ships no storage credentials. Asset upload-intent and
  signed-download operations return `503` (and leave no orphan asset) when no
  S3-compatible backend is configured — assets are private by default even when
  unconfigured (`docs/12_STORAGE_ASSETS.md`). The default example backend is **RustFS**,
  and any S3-compatible store works (ADR 0006).
- **The receipt-verification (store) adapter is operator-supplied; purchase
  verification is `503` until it is configured.** Apple/Google receipt verification is
  delegated to a deployment-supplied adapter behind a fail-closed port that ships no
  provider keys (CORE-MON-008). With no adapter wired, a verify/notification request is
  `503` and **never** changes a purchase — Core records and grants nothing without a
  real validator (`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`,
  `docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md`).
- **At-rest encryption of the live data stores is the operator's responsibility.** Core
  stores the personal-data columns (`users.email`, display names, invited emails)
  **plaintext** and relies on storage-layer encryption: run PostgreSQL on encrypted
  storage and enable bucket server-side encryption. Backups are encrypted by Core's own
  tooling (`docs/25_PRIVACY_AND_DATA_PROTECTION.md`, "At-rest encryption expectations").
- **Consent and lawful basis are the controller's (your vertical's) responsibility.**
  Core records **no** per-subject consent or legal basis and has no consent-capture
  surface; the lawful basis under GDPR Art.6 is the controller's to determine. Core
  provides the data-subject-rights *mechanisms* (access/portability, erasure, retention,
  offboarding) regardless of the chosen basis
  (`docs/25_PRIVACY_AND_DATA_PROTECTION.md`, "Consent / legal-basis recording
  decision"). The **self-hoster is the data controller**; Core and its maintainers are
  not a processor of your deployment's data.

## What is intentionally deferred

A few capabilities are modeled but deliberately **not exposed yet**, or out of Core's
scope by design. These are recorded — dated, with a named owner — in the single
deferral/decision register in
`docs/24_SPEC_CONSISTENCY.md` ("Deliberately-absent capabilities and authorization
model recorded as decisions"), so you can tell an intentional omission from a gap.
`csv/api_routes.csv` is the authoritative list of mounted `/api/v1` routes: no row
there means the route does not exist yet. The ones a new adopter is most likely to
meet:

- **No generic CRUD where you might expect it.** Some read/list endpoints (for example
  an entity-relationship list-everything route) are intentionally absent so a bare
  route cannot bypass tenant/workspace scoping (threat T5). Build to the routes that
  exist in `csv/api_routes.csv`.
- **`SceneCreated` and `ContentBlockCreated` are deferred session events.** A scene or
  content block is workspace-prepared and carries no session, so it is not (yet) a
  session-scoped event in the per-session stream (CORE-EVT-004). Your Step 4 content
  preparation happens before a session binds it.
- **A generated recap stays host content until a separate reveal.** The recap read
  route exists, but a participant never receives the recap body until it is revealed.
- **Vertical concerns are not in Core at all.** Paywall/store UI, ad rendering, domain
  vocabulary, themes and vertical-specific screens belong to your vertical, never the
  product-neutral Core (`docs/04_PRODUCT_BOUNDARIES.md`,
  `docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md`).

## Going deeper (the spec set)

You did not need any of these to make a first reveal. Reach for them when you need
depth on a specific concern:

| When you need…                          | Read                                                  |
| --------------------------------------- | ----------------------------------------------------- |
| The full route surface and DTO rules    | `docs/08_API_CONTRACTS.md`, `csv/api_routes.csv`      |
| Who may do what (and why a `404`)       | `docs/06_AUTHORIZATION_MATRIX.md`                      |
| The realtime hub and event stream       | `docs/11_REALTIME_SYNC.md`, `docs/09_EVENT_CATALOG.md` |
| The domain model and boundaries         | `docs/03_DOMAIN_LANGUAGE.md`, `docs/04_PRODUCT_BOUNDARIES.md` |
| Self-hosting, OIDC and storage          | `docs/13_SELF_HOSTING_REQUIREMENTS.md`, `docs/12_STORAGE_ASSETS.md` |
| Entitlements, quotas and store receipts | `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`   |
| Licensing in full                       | `docs/16_LICENSING.md`                                |
| Package versioning and upgrades         | `docs/23_PACKAGE_VERSIONING.md`                       |
| The threat model behind the fail-closed defaults | `docs/07_SECURITY_THREAT_MODEL.md`           |

The platform is product-neutral by rule: map the Core's generic shapes to your own
domain language in your UI, and never push vertical vocabulary down into the Core.
