// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Request body for creating a workspace (CORE-WS-003,
/// <c>POST /api/v1/workspaces</c>).
///
/// The target organization is supplied in the request as
/// <see cref="OrganizationSlug"/>: <c>POST /workspaces</c> creates a NEW
/// workspace, so it cannot be scoped by an existing workspace membership and the
/// route carries no organization in its path. The slug is matched against the
/// caller's token organization claim and a persisted organization membership by
/// the tenant context resolver (CORE-ID-005); the create is then authorized by
/// the caller's organization role (Owner or Admin), never by silently picking
/// "the caller's only organization" (threat T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): a
/// workspace has a slug (the per-tenant natural key) and a display name only.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the target organization the workspace is created in.
/// </param>
/// <param name="Slug">
/// Per-tenant natural key of the new workspace (lower-case, URL-safe). Unique
/// within the organization (not globally).
/// </param>
/// <param name="Name">Human-readable display name of the workspace.</param>
public sealed record CreateWorkspaceRequest(string? OrganizationSlug, string? Slug, string? Name);

/// <summary>
/// Request body for updating a workspace (CORE-WS-003,
/// <c>PUT /api/v1/workspaces/{workspaceId}</c>).
///
/// This is the "update" slice of the workspace create/read/UPDATE story. The
/// route table (csv/api_routes.csv) has no dedicated workspace update route, but
/// the story title calls for update and the aggregate supports rename
/// (<see cref="Workspace.Rename"/>), so the update is a minimal rename only,
/// authorized by the "Manage workspace settings" matrix row (Owner, Admin;
/// docs/06_AUTHORIZATION_MATRIX.md). The organization, slug and id are immutable
/// on the aggregate, so a rename never moves the workspace to another tenant
/// (threat T5). No field beyond the display name is accepted.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the workspace, used to resolve
/// the tenant context (the route carries no organization in its path).
/// </param>
/// <param name="Name">New human-readable display name of the workspace.</param>
public sealed record UpdateWorkspaceRequest(string? OrganizationSlug, string? Name);

/// <summary>
/// Response projection of a workspace (CORE-WS-003). Generic and product-neutral
/// (docs/04_PRODUCT_BOUNDARIES.md, docs/08_API_CONTRACTS.md): identifiers, the
/// per-tenant natural key, the display name and server timestamps only. It
/// carries no host-only or hidden fields and no internal authorization rationale
/// (docs/08 DTO design rules; threat T7).
/// </summary>
/// <param name="Id">Surrogate id of the workspace (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the workspace belongs to.</param>
/// <param name="Slug">Per-tenant natural key of the workspace.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Status">
/// Lifecycle status name of the workspace (<c>Active</c> or <c>Archived</c>;
/// CORE-LIFE-009). An archived workspace is read-only and excluded from the
/// active workspace list, so a client renders it accordingly. Serialized as the
/// stable enum name, exactly like the session lifecycle status.
/// </param>
/// <param name="CreatedAt">When the workspace was created (UTC).</param>
/// <param name="UpdatedAt">When the workspace was last updated (UTC).</param>
public sealed record WorkspaceResponse(
    Guid Id,
    Guid OrganizationId,
    string Slug,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The resource's optimistic-concurrency version — the opaque value of the weak
    /// <c>ETag</c> the same read/mutation response carries in its header (CORE-DX-002,
    /// "Include resource version where concurrent updates matter"; docs/08). It is the
    /// PostgreSQL <c>xmin</c> token (CORE-CONC-006) surfaced for consumers: a consumer
    /// echoes it back as <c>If-Match</c> on a later write to make that write conditional,
    /// so a GET-then-PUT across HTTP cannot silently clobber a concurrent change. It is
    /// <see langword="null"/> on a representation with no single token to surface — a
    /// collection item (the list never tracks a per-row token) or the tokenless SQLite
    /// test provider — never a secret (it leaks no tenant/internal state; threat T7).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Projects a <see cref="Workspace"/> aggregate into its response DTO. Only
    /// the generic, non-sensitive fields are copied; the lifecycle status is
    /// emitted by its stable name. <paramref name="version"/> is the resource's
    /// optimistic-concurrency token (<see cref="Version"/>) when the caller resolved
    /// one for a single-resource response, otherwise <see langword="null"/> (the list
    /// projection passes none).
    /// </summary>
    public static WorkspaceResponse From(Workspace workspace, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new WorkspaceResponse(
            workspace.Id,
            workspace.OrganizationId,
            workspace.Slug,
            workspace.Name,
            workspace.Status.ToString(),
            workspace.CreatedAt,
            workspace.UpdatedAt)
        {
            Version = version,
        };
    }
}

/// <summary>
/// Request body for inviting a member to a workspace (CORE-WS-004,
/// <c>POST /api/v1/workspaces/{workspaceId}/members</c>, csv/api_routes.csv
/// "Invite/add member", roles Owner,Admin).
///
/// The target organization is supplied as <see cref="OrganizationSlug"/> (the
/// route carries no organization in its path), matched against the caller's
/// token organization claim and a persisted organization membership by the
/// tenant context resolver (CORE-ID-005); the invite is then authorized by the
/// caller's organization role (Owner or Admin). The workspace is taken from the
/// route. The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md):
/// it names the invitee by email (data, not a credential;
/// docs/adr/0005) and the generic role to grant. It carries NO token: the
/// scoped token is generated server-side and returned only in the response.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to
/// resolve the tenant context.
/// </param>
/// <param name="Email">Email of the invitee (informational data only).</param>
/// <param name="Role">Generic role the invite will grant on redemption.</param>
public sealed record InviteWorkspaceMemberRequest(string? OrganizationSlug, string? Email, string? Role);

/// <summary>
/// Response of creating a workspace invitation (CORE-WS-004). It is the ONLY
/// place the plaintext scoped token is ever returned: the inviter receives the
/// token exactly once, at creation, and it is never returned again on any read
/// and never logged (threats T6/T7 in docs/07_SECURITY_THREAT_MODEL.md). The
/// stored value is only the token's SHA-256 hash, which is never exposed.
///
/// The response is generic and product-neutral (docs/08_API_CONTRACTS.md DTO
/// rules): the invitation id, its tenant and workspace, the granted role, the
/// expiry, the lifecycle status, the server creation timestamp and the one-time
/// token. It carries no invited email back (personal data minimization), no
/// token hash and no internal authorization rationale.
/// </summary>
/// <param name="Id">Surrogate id of the invitation (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the invitation belongs to.</param>
/// <param name="WorkspaceId">Workspace the invite grants admission to.</param>
/// <param name="Role">Generic role the invite will grant on redemption.</param>
/// <param name="Status">Lifecycle status of the invitation (Pending at creation).</param>
/// <param name="ExpiresAt">When the scoped token expires (UTC).</param>
/// <param name="CreatedAt">When the invitation was created (UTC).</param>
/// <param name="Token">
/// The one-time plaintext scoped token. Returned exactly once here; never stored
/// in plaintext, never returned again and never logged.
/// </param>
public sealed record WorkspaceInvitationResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string Role,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string Token)
{
    /// <summary>
    /// Projects a created <see cref="WorkspaceInvitation"/> plus its one-time
    /// plaintext token into the creation response. The plaintext token is passed
    /// in (it lives only on the stack of the create call, never on the
    /// aggregate); the token hash and invited email are never copied out.
    /// </summary>
    public static WorkspaceInvitationResponse From(WorkspaceInvitation invitation, string plaintextToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            throw new ArgumentException("A one-time token value is required.", nameof(plaintextToken));
        }

        return new WorkspaceInvitationResponse(
            invitation.Id,
            invitation.OrganizationId,
            invitation.WorkspaceId,
            invitation.Role.ToString(),
            invitation.Status.ToString(),
            invitation.ExpiresAt,
            invitation.CreatedAt,
            plaintextToken);
    }
}

/// <summary>
/// PII-safe response projection of a PENDING workspace invitation (CORE-WS-008,
/// <c>GET /api/v1/workspaces/{workspaceId}/invitations</c>). It is the read DTO
/// of the manage-members pending-invitations list, returned to an Owner/Admin so
/// they can see a workspace's outstanding invites.
///
/// Data minimization and the token-at-rest model (docs/08_API_CONTRACTS.md DTO
/// rules; threats T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>The <see cref="InvitedEmail"/> is the ONLY personal datum and is
///   included by design — an admin needs to see who was invited — but it remains
///   DATA, never a credential (docs/adr/0005).</item>
///   <item>The token hash is NEVER projected (there is no field for it), and the
///   one-time plaintext token does not exist on a stored invitation, so it can
///   never be returned on a read. The creation response
///   (<see cref="WorkspaceInvitationResponse"/>) is the only place a token is ever
///   returned, exactly once.</item>
///   <item>No internal authorization rationale is carried (threat T7).</item>
/// </list>
/// </summary>
/// <param name="Id">Surrogate id of the invitation (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the invitation belongs to.</param>
/// <param name="WorkspaceId">Workspace the invite grants admission to.</param>
/// <param name="InvitedEmail">Email of the invitee (the only personal datum; data, not a credential).</param>
/// <param name="Role">Generic role the invite will grant on redemption.</param>
/// <param name="Status">Lifecycle status of the invitation (always Pending for this list).</param>
/// <param name="ExpiresAt">When the scoped token expires (UTC).</param>
/// <param name="CreatedAt">When the invitation was created (UTC).</param>
public sealed record PendingWorkspaceInvitationResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string InvitedEmail,
    string Role,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Projects a <see cref="WorkspaceInvitation"/> aggregate into its PII-safe
    /// list DTO. Only the generic fields plus the single allowed personal datum
    /// (the invited email) are copied; the token hash is DELIBERATELY never copied
    /// out (threats T6/T7). The role and status are emitted by their stable names.
    /// </summary>
    public static PendingWorkspaceInvitationResponse From(WorkspaceInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        return new PendingWorkspaceInvitationResponse(
            invitation.Id,
            invitation.OrganizationId,
            invitation.WorkspaceId,
            invitation.InvitedEmail,
            invitation.Role.ToString(),
            invitation.Status.ToString(),
            invitation.ExpiresAt,
            invitation.CreatedAt);
    }
}

/// <summary>
/// Request body for redeeming a workspace invitation (CORE-WS-006,
/// <c>POST /api/v1/workspaces/{workspaceId}/invitations/accept</c>).
///
/// The scoped invite token is a BEARER grant (the DECIDED model, threat T6): whoever
/// presents a valid token becomes the member, so the authenticated caller's OIDC subject
/// — never the invited email — is the one granted membership. The plaintext token is
/// carried in the request BODY, never in the URL path or a query string, because a token
/// in a URL leaks into access logs, proxies and browser history (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The server hashes the presented token and resolves
/// the invitation by its hash WITHIN the route's workspace and the resolved tenant, so a
/// token minted for another workspace or tenant resolves to nothing (a hidden 404).
///
/// The target organization is supplied as <see cref="OrganizationSlug"/> (the route
/// carries no organization in its path), matched against the caller's token organization
/// claim and a persisted organization membership by the tenant context resolver
/// (CORE-ID-005), exactly like the other workspace by-id routes. The workspace is taken
/// from the route. The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md).
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve the
/// tenant context.
/// </param>
/// <param name="Token">
/// The one-time plaintext scoped invite token the inviter handed to the invitee. Carried
/// in the body only; the server stores and matches only its SHA-256 hash, never the
/// plaintext (threats T6/T7).
/// </param>
public sealed record AcceptWorkspaceInvitationRequest(string? OrganizationSlug, string? Token);

/// <summary>
/// Response projection of a workspace membership (CORE-WS-006). Returned when an
/// invitation is redeemed into a new <see cref="WorkspaceMember"/>. Generic and
/// product-neutral (docs/04_PRODUCT_BOUNDARIES.md, docs/08_API_CONTRACTS.md): identifiers,
/// the granted generic role and server timestamps only. It carries no invited email, no
/// token and no internal authorization rationale (data minimization; threats T6/T7).
/// </summary>
/// <param name="Id">Surrogate id of the membership (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the membership belongs to.</param>
/// <param name="WorkspaceId">Workspace the membership grants standing in.</param>
/// <param name="UserProfileId">Subject (the caller who redeemed the token) the membership is for.</param>
/// <param name="Role">Generic role the membership grants.</param>
/// <param name="CreatedAt">When the membership was created (UTC).</param>
/// <param name="UpdatedAt">When the membership was last updated (UTC).</param>
public sealed record WorkspaceMemberResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid UserProfileId,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="WorkspaceMember"/> aggregate into its response DTO. Only the
    /// generic, non-sensitive fields are copied; the role is emitted by its stable name.
    /// </summary>
    public static WorkspaceMemberResponse From(WorkspaceMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new WorkspaceMemberResponse(
            member.Id,
            member.OrganizationId,
            member.WorkspaceId,
            member.UserProfileId,
            member.Role.ToString(),
            member.CreatedAt,
            member.UpdatedAt);
    }
}
