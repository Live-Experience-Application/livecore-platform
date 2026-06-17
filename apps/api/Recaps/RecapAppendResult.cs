// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Recaps;

/// <summary>
/// The outcome of an idempotent SYSTEM-recap append (CORE-RCP-001). A system recap is produced by the
/// background generation job, which may run in any number of worker replicas with overlapping sweeps; the
/// partial unique index <c>recaps(session_id) WHERE generated_by IS NULL</c> guarantees AT MOST ONE system
/// recap per session, so a losing race does not error — it converges onto the one recap that already exists.
/// </summary>
public enum RecapAppendResult
{
    /// <summary>The system recap was inserted; this caller is the one that produced the session's recap.</summary>
    Appended,

    /// <summary>
    /// A system recap already existed for the session, so this insert was rejected by the partial unique index
    /// and nothing was written. A concurrent sweep or another worker replica produced it first; the duplicate
    /// is a no-op, never an error (the acceptance criterion: "a second concurrent sweep produces no duplicate").
    /// </summary>
    AlreadyExists,
}
