namespace LiveCore.Api.Organizations;

/// <summary>
/// Typed reason why an authenticated principal could not be resolved into a
/// trusted <see cref="TenantContext"/> for a target organization
/// (CORE-ID-005).
///
/// The tenant context resolver is fail-closed: it returns a
/// <see cref="TenantContext"/> only when every check passes, and otherwise one
/// of these denials, never a partially trusted context (docs/06: "Role checks
/// are not enough; object-level authorization is required"; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Resolving the wrong organization, or a
/// foreign organization's membership or role, must be impossible.
///
/// Values are deliberately identifier-only: they can be logged and translated
/// into a 403/404 response without ever echoing token contents or another
/// tenant's data (threat T7 in docs/07_SECURITY_THREAT_MODEL.md; "never leak
/// sensitive content in errors" in docs/02_ARCHITECTURE.md). Callers decide
/// whether a particular denial maps to "not found" or "forbidden"; the resolver
/// only reports why entitlement was denied.
/// </summary>
public enum TenantContextResolutionError
{
    /// <summary>Resolution succeeded; no error.</summary>
    None = 0,

    /// <summary>
    /// The principal is not a human user. Only user principals have a user
    /// profile reference and an organization membership; service accounts are
    /// machine clients and are not resolved into a user tenant context by this
    /// resolver (docs/05_MODULE_CONTRACTS.md; CORE-ID-002).
    /// </summary>
    NotAUser = 1,

    /// <summary>
    /// The principal's organization claims do not include the target
    /// organization. The organization claim is the token-asserted tenant
    /// boundary; a target the provider never asserted is denied before any
    /// persisted membership is consulted (defence in depth, threat T5). Applies
    /// only because the resolver enforces claim matching in addition to
    /// membership; see <see cref="TenantContextResolver"/>.
    /// </summary>
    ClaimMismatch = 2,

    /// <summary>
    /// No organization exists for the requested target identifier. A target
    /// that does not resolve to a tenant can never grant a context.
    /// </summary>
    OrganizationNotFound = 3,

    /// <summary>
    /// The principal's OIDC identity has no persisted user profile reference,
    /// so the subject is unknown to the platform and cannot hold a membership.
    /// </summary>
    UnknownSubject = 4,

    /// <summary>
    /// The subject has no membership in the target organization (including the
    /// case where the subject is a member of another organization only).
    /// Membership in one tenant never grants standing in another (threat T5).
    /// This is the deny-by-default outcome for an authenticated, known subject
    /// who is simply not entitled to the target tenant.
    /// </summary>
    NoMembership = 5,
}
