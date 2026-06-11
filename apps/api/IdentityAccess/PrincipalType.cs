namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Kind of authenticated caller represented by an <see cref="OidcPrincipal"/>
/// (CORE-ID-001).
///
/// The principal type only separates human users from machine clients
/// (docs/05_MODULE_CONTRACTS.md: "service account support"). Workspace and
/// organization roles (Owner, Admin, Host, ...) are authorization concerns
/// owned by later stories and are never part of the principal type.
/// </summary>
public enum PrincipalType
{
    /// <summary>
    /// A human end user authenticated through an interactive OIDC login flow.
    /// </summary>
    User = 1,

    /// <summary>
    /// A machine client authenticated through a client-credentials style
    /// flow, for example an automation or integration client.
    /// </summary>
    ServiceAccount = 2,
}
