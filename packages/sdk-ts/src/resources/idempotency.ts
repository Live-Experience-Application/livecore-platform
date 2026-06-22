// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Shared optional idempotency-key option for the retry-safe SDK create commands
 * (CORE-DX-008).
 *
 * The resource-create POSTs the Core API dedupes server-side (CORE-DX-004) —
 * workspace, session, scene, content-block and asset-link create — honor an
 * optional `Idempotency-Key` header: a retry under the SAME key replays the
 * ORIGINAL resource the server already recorded instead of creating a duplicate
 * (docs/08_API_CONTRACTS.md "Idempotency"). Pass
 * {@link IdempotentCreateOptions.idempotencyKey} to opt in; omitting it preserves
 * the prior unconditional create, so it is non-breaking.
 *
 * Unlike the reveal/hide commands — where the key is REQUIRED, because a fresh
 * key on every retry would defeat idempotency — the key is OPTIONAL here: a
 * create is meaningful without it, and only the create routes the server actually
 * dedupes advertise it. The SDK never generates one per call; reuse the SAME value
 * across every retry of one logical create.
 */

/** Options for a retry-safe SDK resource create (CORE-DX-008). */
export interface IdempotentCreateOptions {
  /**
   * The retry-safety key sent as the `Idempotency-Key` header. Reuse the SAME
   * value for every retry of one logical create so the server replays the
   * original resource (CORE-DX-004) instead of creating a duplicate; omit it to
   * create unconditionally (the prior behavior).
   */
  idempotencyKey?: string;
}
