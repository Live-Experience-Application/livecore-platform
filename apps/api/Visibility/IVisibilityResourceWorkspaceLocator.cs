// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's PORT for resolving whether a governed resource exists IN A GIVEN WORKSPACE
/// (CORE-SVIS-005). The visibility-rule create command enforces the documented same-workspace invariant
/// server-side — a rule may only reference a resource that lives in the rule's own workspace — but a
/// <see cref="VisibilityRule"/>'s <c>resource_id</c> is a POLYMORPHIC reference across the
/// <c>scenes</c>/<c>content_blocks</c>/<c>entities</c> tables (no database foreign key can enforce it;
/// see <see cref="VisibilityRule"/> and <see cref="VisibilityRuleConfiguration"/>), so the create flow
/// must ask the owning modules whether the referenced resource is really in the workspace.
///
/// THE CENTRAL SECURITY MODULE DOES NOT REACH INTO RESOURCE MODULES (CORE-ARCH-001). The Visibility
/// module is a node in the enforced module dependency graph (docs/05_MODULE_CONTRACTS.md) and is NOT
/// allowed to reference the Scenes, Content or Entities modules. So this is a port the Visibility module
/// OWNS, declared in terms of its own <see cref="VisibilityResourceType"/> and plain ids only; the
/// adapter that fulfils it by consulting the three resource repositories lives in the COMPOSITION ROOT
/// (the shared-kernel <c>LiveCore.Api.Hosting</c> namespace, which references every module by design),
/// not in this module. This keeps the same-workspace enforcement server-side and unit-testable without
/// adding a forbidden Visibility -&gt; Scenes/Content/Entities edge.
/// </summary>
internal interface IVisibilityResourceWorkspaceLocator
{
    /// <summary>
    /// Whether a resource of the given kind and id EXISTS in exactly the given (organization, workspace).
    /// The lookup is tenant- AND workspace-scoped: a resource in another tenant or workspace, or an
    /// unknown id, yields <see langword="false"/> — never borrowing another workspace's resource (threats
    /// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). An empty resource id can never address a stored
    /// resource, so it also yields <see langword="false"/> (fail-closed). An undefined
    /// <paramref name="resourceType"/> yields <see langword="false"/>.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the resource must belong to.</param>
    /// <param name="resourceType">The kind of resource to resolve (Scene/ContentBlock/Entity).</param>
    /// <param name="resourceId">The surrogate id of the resource to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ResourceExistsInWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);
}
