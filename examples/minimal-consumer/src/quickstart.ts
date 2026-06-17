// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The worked Core quickstart (CORE-PUB-003): the reference integration a vertical
 * author copies. It does exactly what the documented quick start describes —
 * authenticate, construct the typed SDK client (logging the published package
 * `PACKAGE_NAME`/`VERSION` it runs against), and call a Core API read route — using
 * only the PUBLIC surfaces of the published-shape `@livecore/sdk-ts` and
 * `@livecore/contracts` packages. It imports them by their package entry point, the
 * way a consumer in another repository would after `pnpm add` from the registry, so
 * a breaking change to that published surface breaks this build (CORE-PUB-001).
 *
 * It is product-neutral: it speaks only generic Core vocabulary (an authenticated
 * principal and its organization memberships) and maps nothing to a vertical's own
 * domain language (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md). Authorization stays
 * server-side — the SDK only carries the OIDC bearer token, never a security
 * boundary (docs/07_SECURITY_THREAT_MODEL.md).
 */
import {
  PACKAGE_NAME as CONTRACTS_PACKAGE_NAME,
  VERSION as CONTRACTS_VERSION,
} from "@livecore/contracts";
import type { CurrentPrincipalResponse } from "@livecore/contracts";
import {
  isLiveCoreApiError,
  LiveCoreClient,
  PACKAGE_NAME,
  VERSION,
} from "@livecore/sdk-ts";

/**
 * Minimal, dependency-free view of the Node process environment the runnable entry
 * reads its configuration from. Declared locally so this example type-checks against
 * the published `@livecore` package surfaces ALONE — it pulls in no ambient
 * `@types/node` — while still running under Node (`node dist/main.js`), where
 * `process` is a global. A real vertical app would use `@types/node` and its
 * framework's own configuration system instead.
 */
declare const process: { readonly env: Record<string, string | undefined> };

/** The two inputs a consumer must supply to talk to a Core deployment. */
export interface QuickstartConfig {
  /**
   * Absolute base URL of the Core API origin, WITHOUT the `/api/v1` version prefix
   * (the SDK appends it). Use HTTPS in any real deployment: the bearer token is a
   * secret in transit (threats T4/T7).
   */
  readonly baseUrl: string;
  /**
   * A bearer access token issued by the configured OIDC provider. The SDK never
   * mints a token; a vertical obtains one through its OIDC login flow and supplies
   * it here (docs/adr/0005-oidc-first-authentication.md).
   */
  readonly accessToken: string;
}

/** The environment variables the runnable entry reads its configuration from. */
export const BASE_URL_ENV = "LIVECORE_API_BASE_URL";
/** The environment variable carrying the OIDC bearer access token. */
export const ACCESS_TOKEN_ENV = "LIVECORE_ACCESS_TOKEN";

/**
 * Reads the quickstart configuration from the environment, failing closed with a
 * clear message when either value is absent so the example never calls an
 * authenticated route without a token.
 */
export function loadQuickstartConfigFromEnv(): QuickstartConfig {
  const baseUrl = process.env[BASE_URL_ENV];
  const accessToken = process.env[ACCESS_TOKEN_ENV];
  if (!baseUrl || !accessToken) {
    throw new Error(
      `Set ${BASE_URL_ENV} (the Core API origin, e.g. http://localhost:5062) and ` +
        `${ACCESS_TOKEN_ENV} (a bearer token from your OIDC provider) before running the example.`,
    );
  }
  return { baseUrl, accessToken };
}

/**
 * Runs the worked quickstart against a Core deployment: logs the package release
 * this app runs against, constructs the typed client and calls `GET /api/v1/me`
 * through the SDK, projecting the generic principal context. A non-success response
 * surfaces as a typed {@link isLiveCoreApiError | LiveCoreApiError} rather than a
 * throw a vertical has to special-case.
 */
export async function runQuickstart(config: QuickstartConfig): Promise<void> {
  // 1. Record EXACTLY which published Core package releases this app is running
  //    against. The exported VERSION is the single source of truth inside the
  //    bundle and is kept in lockstep with the manifest and changelog
  //    (docs/23_PACKAGE_VERSIONING.md), so this line pins the integration.
  console.log(
    `Running against ${PACKAGE_NAME}@${VERSION} and ${CONTRACTS_PACKAGE_NAME}@${CONTRACTS_VERSION}.`,
  );

  // 2. Construct the typed client. The caller supplies an access-token provider
  //    (it may be async, e.g. to refresh an expired token); here it is constant.
  const client = new LiveCoreClient({
    baseUrl: config.baseUrl,
    getAccessToken: () => config.accessToken,
  });

  // 3. Call a read route through the typed surface and project the generic result.
  try {
    const principal: CurrentPrincipalResponse =
      await client.identity.getCurrentPrincipal();
    const who =
      principal.user.displayName ??
      principal.user.email ??
      principal.user.subject;
    console.log(
      `Authenticated as ${who} with ${principal.memberships.length} organization membership(s):`,
    );
    for (const membership of principal.memberships) {
      console.log(`  - ${membership.organizationName} [${membership.role}]`);
    }
  } catch (error) {
    // A non-success response is a typed LiveCoreApiError carrying the HTTP status
    // and the RFC 7807 Problem Details — never the token or request body. A 404 may
    // be a genuine not-found OR a fail-closed hidden resource, so a consumer never
    // infers existence from it (docs/08_API_CONTRACTS.md).
    if (isLiveCoreApiError(error)) {
      console.error(
        `Core API request failed (HTTP ${error.status}): ${error.problem?.detail ?? error.message}`,
      );
      return;
    }
    throw error;
  }
}
