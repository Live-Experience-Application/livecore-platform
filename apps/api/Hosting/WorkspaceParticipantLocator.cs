// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;
using LiveCore.Api.Participants;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Composition-root ADAPTER fulfilling the Entities module's <see cref="IWorkspaceParticipantLocator"/> port
/// (CORE-ENT-009) by consulting the Participants module. The entity-search route's AUDIENCE path must resolve
/// the CALLER'S OWN participant server-side from the authenticated principal (never a client-supplied id) to
/// drive <see cref="EntitySearchService.SearchAsync"/>, but the Entities module is, by the enforced module
/// dependency graph (CORE-ARCH-001; docs/05_MODULE_CONTRACTS.md), NOT allowed to reference the Participants
/// module. This adapter lives in the shared-kernel composition root (<c>LiveCore.Api.Hosting</c>, which
/// references every module by design), so it can resolve the participant through the Participants-owned
/// <see cref="IParticipantRepository.FindByUserAsync"/> without adding a forbidden Entities -&gt; Participants
/// edge — the same hexagonal port-and-adapter shape as <see cref="VisibilityResourceWorkspaceLocator"/>.
///
/// The lookup is keyed by the caller's OWN resolved user-profile id and is tenant- AND workspace-scoped (the
/// repository predicate leads with <c>organization_id</c> then <c>workspace_id</c>), so a caller can only ever
/// resolve ITSELF and a participant in another tenant or workspace is never returned (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). A user with no participant, an anonymous participant (no user link) and a
/// REMOVED participant — which holds no standing — all resolve to <see langword="null"/>, so the audience
/// search then takes the fail-closed empty view; an empty id likewise yields <see langword="null"/>.
/// </summary>
internal sealed class WorkspaceParticipantLocator : IWorkspaceParticipantLocator
{
    private readonly IParticipantRepository _participants;

    public WorkspaceParticipantLocator(IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        _participants = participants;
    }

    /// <inheritdoc />
    public async Task<Guid?> FindActiveParticipantIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // An empty boundary or subject id can never address a stored participant, so fail closed without a
        // query (the repository would reject it anyway). The ids come from the resolved tenant context and the
        // route, so in practice they are non-empty.
        if (organizationId == Guid.Empty || workspaceId == Guid.Empty || userProfileId == Guid.Empty)
        {
            return null;
        }

        var participant = await _participants
            .FindByUserAsync(organizationId, workspaceId, userProfileId, cancellationToken)
            .ConfigureAwait(false);

        // Only an ACTIVE participant has standing; a removed participant (and a missing/anonymous one) resolves
        // to null so the audience search takes the empty view — fail-closed, mirroring how the visible feed
        // hides a removed participant.
        return participant is { Status: ParticipantStatus.Active }
            ? participant.Id
            : null;
    }
}
