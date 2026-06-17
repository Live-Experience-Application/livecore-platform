// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Outcome of persisting a new organization (CORE-ID-003).
/// </summary>
public enum OrganizationAddResult
{
    /// <summary>The organization was persisted.</summary>
    Added = 1,

    /// <summary>
    /// An organization with the same slug already exists. The existing
    /// organization was not modified; the slug is the unique natural key, so
    /// a second writer can never overwrite an existing tenant with its own
    /// row (enforced by the unique database index).
    /// </summary>
    DuplicateSlug = 2,
}
