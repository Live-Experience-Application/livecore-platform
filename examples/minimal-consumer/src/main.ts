// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Runnable entry point for the worked consumer example (CORE-PUB-003). Reads the
 * Core API origin and OIDC bearer token from the environment and runs the
 * quickstart. Run it with `pnpm --filter @livecore-examples/minimal-consumer start`
 * (or `node dist/main.js`) after building, with `LIVECORE_API_BASE_URL` and
 * `LIVECORE_ACCESS_TOKEN` set — see this directory's README.
 */
import { loadQuickstartConfigFromEnv, runQuickstart } from "./quickstart.js";

await runQuickstart(loadQuickstartConfigFromEnv());
