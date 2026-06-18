# Minimal consumer example (CORE-PUB-003)

A minimal, product-neutral reference integration for a **vertical app** built on the
LiveCore Core Platform. It does exactly what the documented quick start describes:

1. installs the published Core packages,
2. authenticates with an OIDC bearer token,
3. constructs the typed SDK client (logging the package `PACKAGE_NAME`/`VERSION` it
   runs against), and
4. calls a Core API read route (`GET /api/v1/me`) through the typed surface,
   handling a denial as a typed error.

It exists so a vertical author has a known-good starting point — there was none
before. Keep it minimal: it is a client/integration sample, **not** a product UI.

## Install (in your own repository)

A vertical app in another repository installs the packages from the registry with
its package manager and imports the typed surface:

```bash
pnpm add @livecore/sdk-ts @livecore/contracts
```

```ts
import { LiveCoreClient, PACKAGE_NAME, VERSION } from "@livecore/sdk-ts";
import type { CurrentPrincipalResponse } from "@livecore/contracts";

console.log(`Running against ${PACKAGE_NAME}@${VERSION}.`);

const client = new LiveCoreClient({
    // The Core API origin, WITHOUT the /api/v1 prefix (the SDK appends it).
    baseUrl: "http://localhost:5062",
    // The SDK never mints a token; supply one from your OIDC login flow. May be async.
    getAccessToken: () => process.env.LIVECORE_ACCESS_TOKEN ?? "",
});

const principal: CurrentPrincipalResponse =
    await client.identity.getCurrentPrincipal();
console.log(principal.user.displayName ?? principal.user.subject);
```

The full worked version — with the fail-closed configuration load and the typed
`LiveCoreApiError` handling — is in [`src/quickstart.ts`](src/quickstart.ts).

## Run this example against a local Core

You need a running Core with OIDC configured and a bearer token your provider issued.
The in-repo Compose stack (`deploy/compose`) brings up PostgreSQL, the migrations
runner, the API and the worker; configure its `Authentication__Oidc__*` values
against your OIDC provider (the bundled stack leaves them unset, so authenticated
routes answer `401` until you do — see `deploy/compose/README.md` and `docs/13`).
For a turnkey local setup, the opt-in overlay `deploy/compose/docker-compose.full.yml`
(CORE-DEP-006) adds a pre-wired Keycloak (OIDC) + MinIO (storage) + Valkey (backplane)
and a `livecore` realm, so you get a real OIDC provider and API to talk to without
configuring one by hand:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

Then, from the repository root:

```bash
# Build the workspace packages and this example.
pnpm --recursive run build

# Point at the Core origin and supply a bearer token from your OIDC provider, then run.
LIVECORE_API_BASE_URL=http://localhost:5062 \
LIVECORE_ACCESS_TOKEN=<oidc-bearer-token> \
  pnpm --filter @livecore-examples/minimal-consumer start
```

The example prints the Core package release it is running against, the authenticated
principal and its organization memberships. A non-success response (for example a
`401` for an expired token, or a `403`/`404` fail-closed denial) is caught as a typed
`LiveCoreApiError` and reported with its HTTP status — never the token or request
body.

## Why it is built in CI against the published surface

The example takes `@livecore/sdk-ts` and `@livecore/contracts` as `workspace:*`
dependencies and imports each **only by its package entry point**. Module resolution
therefore follows each package's `exports`/`types` to its built `dist/index.d.ts` —
the same entry point a registry consumer resolves — and the packages expose no deep
import path, so the example can never reach internal `src/`. CI builds it in the
`typescript` job (`pnpm --recursive run build`), so a breaking change to the
published surface fails this build. `tests/minimal-consumer.public-surface.test.mjs`
guards that the example keeps importing only the public entry points and that the
compiled output is loadable.

This package is `private` and is never published; it is not one of the four released
`@livecore` packages (`docs/23_PACKAGE_VERSIONING.md`).
