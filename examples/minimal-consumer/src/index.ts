// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The example's library surface (CORE-PUB-003): re-exports the worked quickstart so
 * it can be imported (for example by the public-surface guard test) without running
 * it. The runnable entry that actually calls a live Core deployment is `main.ts`.
 */
export * from "./quickstart.js";
