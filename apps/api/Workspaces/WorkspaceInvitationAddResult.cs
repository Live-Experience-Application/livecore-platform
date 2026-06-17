// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Outcome of persisting a new workspace invitation (CORE-WS-004).
/// </summary>
public enum WorkspaceInvitationAddResult
{
    /// <summary>The invitation was persisted.</summary>
    Added = 1,

    /// <summary>
    /// An invitation with the same token hash already exists. The token hash is
    /// uniquely indexed, so two invitations can never share one scoped secret —
    /// a token always identifies at most one invitation (threat T6). A genuine
    /// collision is astronomically unlikely (256-bit random token); this guards
    /// the invariant rather than expecting collisions.
    /// </summary>
    DuplicateToken = 2,
}
