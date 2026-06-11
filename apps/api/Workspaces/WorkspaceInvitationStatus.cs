namespace LiveCore.Api.Workspaces;

/// <summary>
/// Lifecycle status of a <see cref="WorkspaceInvitation"/> (CORE-WS-004). The
/// status, together with the expiry timestamp, determines whether the scoped
/// invite token may still be redeemed (threat T6 "Invite abuse" in
/// docs/07_SECURITY_THREAT_MODEL.md: a token is single-use, expires and may be
/// revoked).
///
/// The status is persisted by its stable name (not its numeric value), so the
/// integers below are only in-memory storage discriminators and carry no
/// ordering meaning.
/// </summary>
public enum WorkspaceInvitationStatus
{
    /// <summary>
    /// The invitation has been created and its token has not yet been redeemed
    /// or revoked. A pending invitation may still be redeemed while it has not
    /// expired (the expiry is checked separately, so an unexpired-by-status but
    /// past-expiry invitation is still not redeemable).
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The token has been redeemed. The token is single-use, so an accepted
    /// invitation can never be redeemed again (threat T6).
    /// </summary>
    Accepted = 2,

    /// <summary>
    /// The invitation has been revoked before redemption. A revoked invitation
    /// can never be redeemed, so a leaked-but-revoked token grants nothing
    /// (threat T6 revocation control).
    /// </summary>
    Revoked = 3,
}
