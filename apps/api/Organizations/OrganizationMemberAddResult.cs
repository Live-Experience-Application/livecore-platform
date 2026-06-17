// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Outcome of persisting a new organization membership (CORE-ID-004).
/// </summary>
public enum OrganizationMemberAddResult
{
    /// <summary>The membership was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A membership for the same (organization, subject) pair already exists.
    /// The existing membership was not modified; the
    /// (organization_id, user_id) pair is the unique natural key, so a second
    /// writer can never overwrite an existing standing with its own row
    /// (enforced by the unique database index).
    /// </summary>
    DuplicateMembership = 2,
}
