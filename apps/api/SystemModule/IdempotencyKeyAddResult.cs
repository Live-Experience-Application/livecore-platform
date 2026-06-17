// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.SystemModule;

/// <summary>
/// Outcome of recording an <see cref="IdempotencyKey"/> (CORE-VIS-004). The unique
/// <c>idempotency_keys(scope, key)</c> index means an insert either succeeds
/// (<see cref="Added"/>, the first time this (scope, key) is seen) or is rejected as a duplicate
/// (<see cref="Duplicate"/>, a concurrent or repeated record of the same key) — that distinction is
/// exactly the idempotency signal a caller acts on. Mirrors <c>EntityTypeAddResult</c>'s
/// Added/Duplicate shape.
/// </summary>
public enum IdempotencyKeyAddResult
{
    /// <summary>The idempotency key was recorded for the first time.</summary>
    Added = 1,

    /// <summary>
    /// An idempotency key with the same (scope, key) already existed: the unique index rejected the
    /// insert. The caller treats this as "already processed".
    /// </summary>
    Duplicate = 2,
}
