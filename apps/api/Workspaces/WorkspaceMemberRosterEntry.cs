// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Audience-safe read-model of one workspace-membership ROSTER entry (CORE-WSM-001), the projection the
/// administration member-roster read (<c>GET /api/v1/workspaces/{workspaceId}/members</c>) returns one page of.
///
/// It joins the existing <see cref="WorkspaceMember"/> aggregate (the membership id, its tenant/workspace, the
/// generic <see cref="Role"/> and the server timestamps) to the READ-ONLY, explicitly allow-listed audience-safe
/// display metadata from the subject's <c>users</c> profile — today just the optional
/// <see cref="DisplayName"/> — so a host can render a members screen and, crucially, obtain the membership
/// <see cref="Id"/> that the member-removal command (<c>removeMember</c>) requires (the membership id was
/// otherwise unobtainable: the existing <see cref="WorkspaceMemberResponse"/> is the invitation-redemption
/// projection returned only to the accepting caller, never an administrator roster).
///
/// Data minimization / PII discipline (docs/08_API_CONTRACTS.md DTO rules; threats T6/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md): this read-model carries ONLY generic identifiers, the generic role, the
/// audience-safe display name and the server timestamps. It deliberately does NOT carry the subject's
/// invited/login email, any token or token hash, or any internal authorization rationale — none of those columns
/// is selected by the roster query (the read joins only the audience-safe display column of the profile, never
/// the profile's email). It is an INTERNAL repository projection; the wire shape is
/// <see cref="WorkspaceMemberRosterEntryResponse"/>.
/// </summary>
/// <param name="Id">Surrogate id of the membership row (the id <c>removeMember</c> addresses).</param>
/// <param name="OrganizationId">Tenant the membership belongs to.</param>
/// <param name="WorkspaceId">Workspace the membership grants standing in.</param>
/// <param name="UserProfileId">Subject (the member's user-profile id).</param>
/// <param name="Role">Generic role the subject holds in the workspace.</param>
/// <param name="DisplayName">
/// The subject's optional, audience-safe display name, mirrored read-only from the <c>users</c> profile. It is
/// <see langword="null"/> when the profile asserts none; it is NEVER the subject's email (data minimization).
/// </param>
/// <param name="CreatedAt">When the membership was created (UTC).</param>
/// <param name="UpdatedAt">When the membership was last updated (UTC).</param>
public sealed record WorkspaceMemberRosterEntry(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid UserProfileId,
    MembershipRole Role,
    string? DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
