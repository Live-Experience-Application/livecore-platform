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
/// <param name="CreatedAt">When the workspace was created (UTC).</param>
/// <param name="UpdatedAt">When the workspace was last updated (UTC).</param>
public sealed record WorkspaceResponse(
    Guid Id,
    Guid OrganizationId,
    string Slug,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="Workspace"/> aggregate into its response DTO. Only
    /// the generic, non-sensitive fields are copied.
    /// </summary>
    public static WorkspaceResponse From(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new WorkspaceResponse(
            workspace.Id,
            workspace.OrganizationId,
            workspace.Slug,
            workspace.Name,
            workspace.CreatedAt,
            workspace.UpdatedAt);
    }
}
