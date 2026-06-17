namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Outcome of the data-subject erasure command (<see cref="DataSubjectErasureService"/>, CORE-PRIV-001). The
/// endpoint maps each outcome to a fail-closed HTTP status without echoing any detail (threat T7).
/// </summary>
internal enum DataSubjectErasureResult
{
    /// <summary>
    /// The subject's personal data was erased: the user profile row was deleted (its dependent participant
    /// display data and invited-email rows anonymized, its exports/assets anonymized via the schema's
    /// <c>ON DELETE SET NULL</c> foreign keys and its memberships revoked via <c>ON DELETE CASCADE</c>) and the
    /// erasure was audited by id. The endpoint returns <c>204 No Content</c>.
    /// </summary>
    Erased,

    /// <summary>
    /// No user profile exists for the resolved subject id. The endpoint hides this as a <c>404</c> (it should
    /// not occur once a tenant-scoped membership has been resolved, since the membership references a real user
    /// profile by foreign key, but the command fails closed rather than assuming).
    /// </summary>
    SubjectNotFound,

    /// <summary>
    /// The subject is the sole Owner of at least one organization, so erasing them — which CASCADE-removes their
    /// memberships across every tenant — would leave that tenant permanently unreachable. The erasure is refused
    /// as a <c>409 Conflict</c> until ownership is transferred (the generalized last-Owner invariant). Nothing is
    /// changed.
    /// </summary>
    SubjectIsSoleOrganizationOwner,
}
