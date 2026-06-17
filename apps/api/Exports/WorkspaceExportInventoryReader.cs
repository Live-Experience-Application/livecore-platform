// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Exports;

/// <summary>
/// EF Core implementation of <see cref="IWorkspaceExportInventoryReader"/> (CORE-JOB-002). It runs one bounded
/// <c>COUNT</c> per generic <see cref="ExportResourceKind"/> over that kind's workspace-scoped table, each
/// filtered to exactly the given (organization, workspace) pair, and returns the complete per-kind inventory.
/// Only row counts are read — never any column carrying content (threats T7/T8).
/// </summary>
internal sealed class WorkspaceExportInventoryReader : IWorkspaceExportInventoryReader
{
    private readonly LiveCoreDbContext _dbContext;

    public WorkspaceExportInventoryReader(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<ExportResourceKind, int>> CountWorkspaceResourcesAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's resources, so the inventory fails fast instead of
        // counting across an unscoped predicate.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // Each count's predicate leads with the tenant column and then matches the workspace, so every count is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's rows are never counted
        // even when they share the same kind (threat T5; the organization boundary is checked before the
        // workspace boundary). Only rows are counted — no title, body, object key or other content column is
        // read (threats T7/T8).
        var sessions = await _dbContext.Sessions
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        var scenes = await _dbContext.Scenes
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        var contentBlocks = await _dbContext.ContentBlocks
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        var entities = await _dbContext.Entities
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        var participants = await _dbContext.Participants
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);
        var assets = await _dbContext.Assets
            .CountAsync(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, cancellationToken)
            .ConfigureAwait(false);

        // Every defined kind is present (a kind with no rows maps to 0), so the produced manifest is a complete,
        // deterministic inventory rather than only the non-empty kinds.
        return new Dictionary<ExportResourceKind, int>
        {
            [ExportResourceKind.Session] = sessions,
            [ExportResourceKind.Scene] = scenes,
            [ExportResourceKind.ContentBlock] = contentBlocks,
            [ExportResourceKind.Entity] = entities,
            [ExportResourceKind.Participant] = participants,
            [ExportResourceKind.Asset] = assets,
        };
    }
}
