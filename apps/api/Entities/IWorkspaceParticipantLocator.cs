// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// The Entities module's PORT for resolving the CALLER'S OWN active participant id within a workspace
/// (CORE-ENT-009). The entity-search route's AUDIENCE path must drive
/// <see cref="EntitySearchService.SearchAsync"/> with the calling participant, and that participant MUST be
/// resolved server-side from the authenticated principal (the existing principal-to-participant mapping,
/// <c>IParticipantRepository.FindByUserAsync</c>), NEVER a client-supplied id — otherwise an audience caller
/// could ask for another participant's revealed set (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE ENTITIES MODULE DOES NOT REACH INTO THE PARTICIPANTS MODULE (CORE-ARCH-001). The Entities module is a
/// node in the enforced module dependency graph (docs/05_MODULE_CONTRACTS.md) and is NOT allowed to reference
/// the Participants module. So this is a port the Entities module OWNS, declared in terms of plain ids only;
/// the adapter that fulfils it by consulting the Participants-owned <c>IParticipantRepository</c> lives in the
/// COMPOSITION ROOT (the shared-kernel <c>LiveCore.Api.Hosting</c> namespace, which references every module by
/// design), not in this module — exactly the port-and-adapter shape
/// <see cref="Visibility.IVisibilityResourceWorkspaceLocator"/> uses. This keeps the self-only participant
/// resolution server-side and unit-testable without adding a forbidden Entities -&gt; Participants edge.
/// </summary>
internal interface IWorkspaceParticipantLocator
{
    /// <summary>
    /// Resolves the surrogate id of the participant the given user OWNS in exactly the given (organization,
    /// workspace), or <see langword="null"/> when the user has no participant there or that participant is not
    /// <c>Active</c>. The lookup is tenant- AND workspace-scoped and keyed by the caller's OWN resolved user
    /// profile id (never a client-supplied participant id), so a caller can only ever resolve ITSELF (threats
    /// T1/T5). A removed participant holds no standing and resolves to <see langword="null"/> (fail-closed), so
    /// the entity-search audience path then takes the empty view exactly like a caller with no participant at
    /// all. An empty organization/workspace/user id can never address a stored participant, so it likewise
    /// yields <see langword="null"/>.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the participant must belong to.</param>
    /// <param name="userProfileId">The caller's own resolved user-profile id (never a client-supplied id).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> FindActiveParticipantIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken);
}
