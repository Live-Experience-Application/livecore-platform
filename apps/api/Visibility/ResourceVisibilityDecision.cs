// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Why a <see cref="ResourceVisibilityDecision"/> turned out the way it did (CORE-VIS-002). The
/// reason is recorded alongside the boolean so callers and tests can assert the BASIS of the
/// decision (not just the yes/no), and so the later audit story (CORE-VIS-006) can record WHY a
/// resource was or was not shown. It is never itself an authorization input.
/// </summary>
public enum VisibilityAccessReason
{
    /// <summary>
    /// Allowed: the viewer's role grants "View host-only content" (Owner/Admin/Host/CoHost), so the
    /// resource is visible regardless of any visibility rule (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    GrantedByHostRole = 1,

    /// <summary>
    /// Allowed: the viewer is an audience role (Participant/Observer) and a visibility rule makes the
    /// resource visible to the audience ("View participant-visible content" = "if visible",
    /// docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    GrantedByVisibleRule = 2,

    /// <summary>
    /// Denied: the viewer is an audience role but no visibility rule makes the resource visible, so
    /// it is host-only content the audience may not see (deny-by-default; threat T5).
    /// </summary>
    DeniedNotVisible = 3,

    /// <summary>
    /// Denied: the viewer's role grants neither host-only-content access nor audience visibility —
    /// the audit role (audit-only, not a live content grant) or any undefined role (deny-by-default;
    /// threats T1/T5).
    /// </summary>
    DeniedRoleNotPermitted = 4,
}

/// <summary>
/// The outcome of a <see cref="VisibilityPolicy.CanViewResourceAsync(System.Guid, System.Guid, System.Guid, Organizations.MembershipRole, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/> decision (CORE-VIS-002):
/// whether a viewer may see a specific resource, together with the <see cref="VisibilityAccessReason"/>
/// that produced it. It is a server-side authorization decision; the reason exists for testing and
/// the later audit trail, never as an input to another decision.
/// </summary>
public sealed class ResourceVisibilityDecision
{
    private ResourceVisibilityDecision(bool canView, VisibilityAccessReason reason)
    {
        CanView = canView;
        Reason = reason;
    }

    /// <summary>Whether the viewer may see the resource.</summary>
    public bool CanView { get; }

    /// <summary>Why the decision turned out the way it did.</summary>
    public VisibilityAccessReason Reason { get; }

    /// <summary>Builds an ALLOW decision with the given (allow) reason.</summary>
    public static ResourceVisibilityDecision Allow(VisibilityAccessReason reason)
        => new(true, reason);

    /// <summary>Builds a DENY decision with the given (deny) reason.</summary>
    public static ResourceVisibilityDecision Deny(VisibilityAccessReason reason)
        => new(false, reason);
}
