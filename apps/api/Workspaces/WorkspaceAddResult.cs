// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Outcome of persisting a new workspace (CORE-WS-001).
/// </summary>
public enum WorkspaceAddResult
{
    /// <summary>The workspace was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A workspace with the same slug already exists in the same organization.
    /// The existing workspace was not modified; the
    /// (organization_id, slug) pair is the per-tenant unique natural key, so a
    /// second writer can never overwrite an existing workspace with its own row
    /// (enforced by the unique database index). The same slug is still
    /// available to other organizations (threat T5).
    /// </summary>
    DuplicateSlug = 2,
}
