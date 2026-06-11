namespace LiveCore.Api.Participants;

/// <summary>
/// Persistence contract for the participant aggregate (CORE-SES-001). The
/// Participants module owns the <c>participants</c> table; other modules access
/// participants only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Participants module owns "session/workspace
/// participant records").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the
/// organization id and the workspace id together with the participant id (or the
/// linked user id), and a participant is only ever returned when it belongs to
/// exactly that (organization, workspace) pair. The organization boundary is
/// checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles), so a participant is never returned through a
/// foreign organization's id even when the workspace and participant ids are
/// correct, and never through a foreign workspace's id even when the organization
/// and participant ids are correct. There is deliberately no lookup of a
/// participant by id or user alone and no lookup that crosses tenants, so one
/// workspace's participant can never be read through another workspace's id and a
/// participant in one tenant can never be read through another tenant's id
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization). Resolving the "current" organization or workspace from a
/// request is not done here; that is the tenant context resolver (CORE-ID-005)
/// and later endpoint stories. This is the aggregate + persistence story; HTTP
/// endpoints (the participant-visible feed CORE-SES-005) and the session-join
/// flow (CORE-SES-003) are later stories and are deliberately not built here.
/// This contract takes explicit ids.
/// </summary>
public interface IParticipantRepository
{
    /// <summary>
    /// Finds the participant with exactly the given id WITHIN the given
    /// organization and workspace, or <see langword="null"/> when no such
    /// participant exists there. The organization and workspace both scope the
    /// lookup, so a participant that exists under another organization's or
    /// workspace's id is never returned, even when the surrogate id matches
    /// (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or participant id is empty. An empty id
    /// can never address a stored participant, so the lookup is rejected instead
    /// of silently returning nothing.
    /// </exception>
    Task<Participant?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the participant linked to exactly the given user WITHIN the given
    /// organization and workspace, or <see langword="null"/> when the user has no
    /// participant there. Only user-linked participants are addressed by this
    /// lookup (anonymous participants have no user link); the unique
    /// (workspace_id, user_id) index guarantees at most one such participant per
    /// workspace. The organization and workspace both scope the lookup, so a
    /// participant the user has in another workspace, or in the same workspace id
    /// under a different organization, is never returned (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or user profile id is empty.
    /// </exception>
    Task<Participant?> FindByUserAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new participant. Returns
    /// <see cref="ParticipantAddResult.DuplicateUserParticipant"/> when the
    /// participant is linked to a user that already has a participant in the same
    /// workspace (enforced by the filtered unique (workspace_id, user_id)
    /// database index, so concurrent first-time callers can never create two
    /// participants for one user in one workspace). Anonymous participants are not
    /// subject to this uniqueness.
    /// </summary>
    Task<ParticipantAddResult> AddAsync(Participant participant, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a participant previously loaded through this
    /// repository. The organization, workspace, user link and id of a participant
    /// are immutable (<see cref="Participant"/>), so an update only ever changes
    /// the display name, the lifecycle status and the update timestamp; it can
    /// never move the participant to another tenant, workspace or subject
    /// (threat T5). The caller is responsible for having loaded the participant
    /// through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(Participant participant, CancellationToken cancellationToken);
}
