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

    /// <summary>
    /// Finds the invitation with exactly the given surrogate id WITHIN the given
    /// organization and workspace, or <see langword="null"/> when none exists
    /// (CORE-WS-007). The id is the row's own key, but the organization and
    /// workspace both additionally scope the lookup (the organization boundary
    /// checked before the workspace boundary), so an invitation with that id in
    /// another workspace, or in the same workspace id under a different
    /// organization, is never returned: a foreign-tenant or wrong-workspace
    /// invitation is indistinguishable from a non-existent one (threats T1/T5).
    /// This is the by-id lookup the revoke command uses to address a specific
    /// pending invitation by its <see cref="WorkspaceInvitation.Id"/>; unlike the
    /// token-hash lookup it never touches the secret (no plaintext, no hash).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or invitation id is empty.
    /// </exception>
    Task<WorkspaceInvitation?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid invitationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the PENDING invitations of exactly the given organization and
    /// workspace, oldest first (CORE-WS-008). "Pending" is the lifecycle status
    /// only (<see cref="WorkspaceInvitationStatus.Pending"/>): an already-accepted
    /// or already-revoked invitation is never returned, so the result is the
    /// workspace's outstanding invites. The organization and workspace both scope
    /// the read (the organization boundary checked before the workspace boundary),
    /// so an invitation in another workspace, or in the same workspace id under a
    /// different organization, is never returned: a foreign-tenant or
    /// wrong-workspace caller sees an empty result, never another scope's invites
    /// (threats T1/T5). This is a read-only projection source for the
    /// manage-members pending-invitations list; it returns the aggregates and the
    /// endpoint maps them to a PII-safe DTO that never exposes the token hash
    /// (threats T6/T7).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty.
    /// </exception>
    Task<IReadOnlyList<WorkspaceInvitation>> ListPendingByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the PENDING invitations of exactly the given organization and workspace,
    /// oldest first, in the same scoped/ordered way as <see cref="ListPendingByWorkspaceAsync"/>, limited to
    /// <paramref name="take"/> rows starting at <paramref name="skip"/>, backing the bounded pending-invitations
    /// list route (<c>GET /api/v1/workspaces/{workspaceId}/invitations</c>, CORE-DX-003). The organization and
    /// workspace both scope the read (the organization boundary checked before the workspace boundary), so a
    /// foreign-tenant or wrong-workspace caller never sees another scope's invites (threats T1/T5); the token
    /// hash rides along on the aggregate but the endpoint projects it to a PII-safe DTO that never emits it
    /// (threats T6/T7). The caller over-fetches one extra row (<c>take = limit + 1</c>) to decide <c>hasMore</c>
    /// without a second COUNT. Bounding the read means a single list response can never materialize an unbounded
    /// array (threat T9).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<WorkspaceInvitation>> ListPendingPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an invitation previously loaded through this repository
    /// (CORE-WS-006). The tenant, workspace, invited email, role, token hash and
    /// expiry of an invitation are immutable (<see cref="WorkspaceInvitation"/>);
    /// the only change an update ever applies is the single-use lifecycle
    /// transition (Pending -> Accepted on redemption, or Pending -> Revoked), so an
    /// update can never move the invitation to another tenant or workspace
    /// (threat T5) nor re-open a consumed token. The caller is responsible for
    /// having loaded the invitation through a tenant-scoped lookup and for running
    /// the update inside the redeem command's transaction (CORE-CONC-002), so the
    /// status change and the membership create commit together or not at all.
    /// </summary>
    /// <exception cref="ArgumentNullException">The invitation is null.</exception>
    Task UpdateAsync(WorkspaceInvitation invitation, CancellationToken cancellationToken);

    /// <summary>
    /// Anonymizes EVERY invitation whose invited email matches the given address — scrubbing the email to the
    /// fixed PII-free placeholder (<see cref="WorkspaceInvitation.AnonymizeInvitedEmail"/>) — and returns how
    /// many were anonymized (CORE-PRIV-001, GDPR Art.17). It is the data-subject erasure counterpart of the
    /// other "by X" methods, addressing invitations by the only PII column they carry (the invited email), which
    /// no foreign key references — so the erasure must scrub it explicitly here.
    ///
    /// CROSS-TENANT BY DESIGN. Unlike the read/lookup methods on this contract, this one is deliberately NOT
    /// tenant- or workspace-scoped: a data subject's email must be erased EVERYWHERE it was stored (GDPR
    /// Art.17), and an invitation can precede the subject ever having a profile/membership, so it is matched by
    /// the email value alone. This is safe under threat T5 because it is a write keyed by the subject's own
    /// email (never a guessed foreign id), it READS nothing back to any caller (it returns only a count) and it
    /// is reachable only from the authorized erasure command. The match is EXACT (ordinal): the invited email is
    /// stored as trimmed, case-preserved data, so the erasure scrubs exactly the rows that recorded this address.
    /// </summary>
    /// <param name="invitedEmail">The erased data subject's email to scrub (required, non-blank).</param>
    /// <param name="updatedAt">The erasure timestamp stamped on each anonymized invitation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of invitations anonymized (zero when none matched).</returns>
    /// <exception cref="ArgumentException">The invited email is blank.</exception>
    Task<int> AnonymizeByInvitedEmailAsync(
        string invitedEmail,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}
