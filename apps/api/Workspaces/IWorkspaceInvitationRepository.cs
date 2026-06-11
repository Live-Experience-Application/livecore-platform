namespace LiveCore.Api.Workspaces;

/// <summary>
/// Persistence contract for workspace invitations (CORE-WS-004). The Workspaces
/// module owns the <c>workspace_invitations</c> table; other modules access
/// invitations only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Workspaces module owns workspace membership).
///
/// Every lookup is explicitly scoped by BOTH the organization (the tenant,
/// checked before the workspace boundary) and the workspace, mirroring
/// <see cref="IWorkspaceMemberRepository"/>: an invitation is only ever returned
/// for exactly the (organization, workspace) it belongs to, so an invitation in
/// one tenant or workspace can never be read through another's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization). Resolving the "current" organization or workspace from a
/// request is the tenant context resolver (CORE-ID-005) and the workspace
/// endpoints, not this contract; this contract takes explicit ids.
///
/// The token lookup takes a token HASH, never a plaintext token: callers hash a
/// presented token with <see cref="WorkspaceInvitationToken.Hash"/> and look up
/// by the hash, so the plaintext secret is never passed to or stored by the
/// persistence layer (threats T6/T7).
/// </summary>
public interface IWorkspaceInvitationRepository
{
    /// <summary>
    /// Persists a new invitation. Returns
    /// <see cref="WorkspaceInvitationAddResult.DuplicateToken"/> when an
    /// invitation with the same token hash already exists (enforced by the
    /// unique token-hash database index, so one scoped token can never address
    /// two invitations; threat T6).
    /// </summary>
    Task<WorkspaceInvitationAddResult> AddAsync(
        WorkspaceInvitation invitation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the invitation with exactly the given token hash WITHIN the given
    /// organization and workspace, or <see langword="null"/> when none exists.
    /// The organization and workspace both scope the lookup, so a token hash is
    /// only ever resolved inside the tenant and workspace it was scoped to: a
    /// matching hash under another organization or workspace is never returned
    /// (threats T5/T6). The caller passes a HASH, never a plaintext token. This
    /// is provided for the redeem/lookup-by-token model; the redeem HTTP flow
    /// itself is a follow-up (no acceptance route in this placeholder story).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, or the token hash is blank
    /// or not a valid hash.
    /// </exception>
    Task<WorkspaceInvitation?> FindByTokenHashAsync(
        Guid organizationId,
        Guid workspaceId,
        string tokenHash,
        CancellationToken cancellationToken);
}
