// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Outcome of persisting a new workspace membership (CORE-WS-002).
/// </summary>
public enum WorkspaceMemberAddResult
{
    /// <summary>The membership was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A membership for the same (workspace, subject) pair already exists. The
    /// existing membership was not modified; the (workspace_id, user_id) pair is
    /// the unique natural key, so a second writer can never overwrite an
    /// existing standing with its own row (enforced by the unique database
    /// index).
    /// </summary>
    DuplicateMembership = 2,
}
