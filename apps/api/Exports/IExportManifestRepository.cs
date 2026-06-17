// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// Persistence contract for the export manifest aggregate (CORE-AUD-003). The Exports module owns the
/// <c>export_manifests</c> table and its <c>export_manifest_entries</c> child rows; other modules access
/// manifests only through this contract or the module's application services (docs/02_ARCHITECTURE.md: no
/// direct table ownership violations; docs/05_MODULE_CONTRACTS.md: the Exports module owns "export
/// manifests").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and the
/// workspace id, and a manifest is only ever returned when it belongs to exactly that (organization,
/// workspace) pair. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a manifest is never returned through a
/// foreign organization's id even when the workspace and ids are correct, and never through a foreign
/// workspace's id even when the organization and ids are correct. There is deliberately no lookup of a
/// manifest by id alone and no list-everything read method, so one workspace's manifest can never be read
/// through another workspace's id and a manifest in one tenant can never be read through another tenant's id
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// A manifest is write-once (the produced output of a completed export), so there is no update or delete
/// method — only an append and tenant-scoped reads. Reads return the manifest with its full per-kind
/// inventory loaded.
/// </summary>
public interface IExportManifestRepository
{
    /// <summary>
    /// Finds the manifest with exactly the given id WITHIN the given organization and workspace, with its
    /// inventory entries loaded, or <see langword="null"/> when no such manifest exists there. The
    /// organization and workspace both scope the lookup, so a manifest that exists under another
    /// organization's or workspace's id is never returned, even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or manifest id is empty. An empty id can never address a stored
    /// manifest, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<ExportManifest?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the manifest produced for exactly the given export job WITHIN the given organization and
    /// workspace, with its inventory entries loaded, or <see langword="null"/> when the job has no manifest
    /// there. Exactly one manifest exists per job (the unique <c>export_manifests(export_job_id)</c> index).
    /// The lookup is tenant- and workspace-scoped, so a manifest under another organization's or workspace's
    /// id is never returned even when the job id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or export job id is empty.
    /// </exception>
    Task<ExportManifest?> FindByExportJobIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid exportJobId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new export manifest and its inventory entries. Exactly one manifest exists per job, so a
    /// second add for a job that already has a manifest returns
    /// <see cref="ExportManifestAddResult.DuplicateExportJob"/> (the unique
    /// <c>export_manifests(export_job_id)</c> index rejected it) and creates nothing. Foreign-key violations
    /// (a non-existent job, workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<ExportManifestAddResult> AddAsync(ExportManifest manifest, CancellationToken cancellationToken);
}
