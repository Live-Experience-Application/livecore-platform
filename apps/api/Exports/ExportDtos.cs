// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// Request body for requesting an async WORKSPACE export (CORE-EXP-003,
/// <c>POST /api/v1/workspaces/{workspaceId}/exports</c>, csv/api_routes.csv "Request an async workspace
/// export", roles Owner/Admin/Host).
///
/// The target workspace is taken from the route path and the export <see cref="ExportScope"/> is FIXED to
/// <see cref="ExportScope.Workspace"/> by the route (the user-data export is its own scope/route), so the body
/// carries only the tenant slug — exactly like the scene/entity create requests resolve their tenant. The
/// slug is matched against the caller's token organization claim AND a persisted organization membership by
/// the tenant context resolver, and the request is then authorized by the caller's role in the route's
/// workspace (threat T5). The export job's surrogate id is assigned SERVER-SIDE (UUIDv7) by the aggregate; the
/// client never supplies an id or a scope.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve the tenant context (the
/// route carries no organization in its path).
/// </param>
public sealed record CreateExportRequest(string? OrganizationSlug);
