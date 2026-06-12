using LiveCore.Api.Exports;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for the <see cref="ExportManifest"/> invariants and its <see cref="ExportManifest.ForWorkspaceExport"/>
/// factory (CORE-AUD-003): a workspace export manifest is the generic, product-neutral TABLE OF CONTENTS of a
/// produced workspace export (docs/03_DOMAIN_LANGUAGE.md, docs/05_MODULE_CONTRACTS.md). It is workspace- and
/// tenant-scoped and belongs only to the workspace and organization of the job that produced it; it carries
/// the explicit, immutable <see cref="ExportScope"/> of that job (the "explicit host/admin export scopes"
/// control for threat T8 in docs/07_SECURITY_THREAT_MODEL.md) and a per-kind inventory of COUNTS only — never
/// any exported content (threats T7/T8). The factory only ever produces a manifest for a COMPLETED,
/// workspace-scoped export job. The object-level boundary checks
/// (BelongsToOrganization/Workspace, Identifies) are the fail-closed isolation guards (threats T5/T1), and
/// <see cref="ExportManifest.ToString"/> stays log-safe. No vertical-specific terms appear anywhere — generic
/// Core vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class ExportManifestTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _requestedBy = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _completedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _generatedAt = new(2026, 6, 12, 9, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<ExportResourceKind, int> _inventory =
        new Dictionary<ExportResourceKind, int>
        {
            [ExportResourceKind.Scene] = 3,
            [ExportResourceKind.ContentBlock] = 7,
            [ExportResourceKind.Participant] = 2,
        };

    private static ExportJob CompletedWorkspaceJob()
    {
        var job = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.Workspace, _createdAt);
        job.Start(_createdAt);
        job.Complete(_completedAt);
        return job;
    }

    // --- Factory: the produced manifest ----------------------------------------

    [Fact]
    public void ForWorkspaceExport_builds_the_manifest_from_the_completed_job()
    {
        var job = CompletedWorkspaceJob();

        var manifest = ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);

        Assert.NotEqual(Guid.Empty, manifest.Id);
        Assert.Equal(7, manifest.Id.Version); // UUIDv7, time-ordered (docs/10_DATABASE_SCHEMA.md)
        Assert.Equal(job.Id, manifest.ExportJobId);
        Assert.Equal(_organizationId, manifest.OrganizationId);
        Assert.Equal(_workspaceId, manifest.WorkspaceId);
        Assert.Equal(ExportScope.Workspace, manifest.Scope);
        Assert.Equal(ExportManifest.CurrentManifestVersion, manifest.ManifestVersion);
        Assert.Equal(_generatedAt, manifest.GeneratedAt);
        // The inventory total is the sum of the per-kind counts.
        Assert.Equal(12, manifest.TotalItemCount);
        Assert.Equal(3, manifest.Entries.Count);
    }

    [Fact]
    public void ForWorkspaceExport_orders_entries_by_kind_deterministically()
    {
        var job = CompletedWorkspaceJob();

        var manifest = ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);

        // The entries are stored in ascending-kind order regardless of the inventory's enumeration order, so
        // the persisted manifest and every projection are stable and repeatable.
        var kinds = manifest.Entries.Select(entry => entry.Kind).ToArray();
        Assert.Equal(kinds.OrderBy(kind => kind).ToArray(), kinds);
        Assert.Equal(
            new[] { ExportResourceKind.Scene, ExportResourceKind.ContentBlock, ExportResourceKind.Participant }
                .OrderBy(kind => kind),
            kinds);
    }

    [Fact]
    public void ForWorkspaceExport_each_entry_points_back_at_the_manifest_with_its_count()
    {
        var job = CompletedWorkspaceJob();

        var manifest = ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);

        foreach (var entry in manifest.Entries)
        {
            Assert.Equal(manifest.Id, entry.ExportManifestId);
            Assert.Equal(_inventory[entry.Kind], entry.ItemCount);
            Assert.NotEqual(Guid.Empty, entry.Id);
        }
    }

    [Fact]
    public void ForWorkspaceExport_allows_an_empty_inventory()
    {
        // A workspace that holds nothing still produces a manifest — an empty table of contents with a zero
        // total.
        var job = CompletedWorkspaceJob();

        var manifest = ExportManifest.ForWorkspaceExport(
            job,
            new Dictionary<ExportResourceKind, int>(),
            _generatedAt);

        Assert.Empty(manifest.Entries);
        Assert.Equal(0, manifest.TotalItemCount);
    }

    [Fact]
    public void ForWorkspaceExport_allows_a_zero_count_entry()
    {
        var job = CompletedWorkspaceJob();

        var manifest = ExportManifest.ForWorkspaceExport(
            job,
            new Dictionary<ExportResourceKind, int> { [ExportResourceKind.Asset] = 0 },
            _generatedAt);

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(ExportResourceKind.Asset, entry.Kind);
        Assert.Equal(0, entry.ItemCount);
        Assert.Equal(0, manifest.TotalItemCount);
    }

    [Fact]
    public void Generated_at_is_normalized_to_utc()
    {
        var job = CompletedWorkspaceJob();
        var localGeneratedAt = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var manifest = ExportManifest.ForWorkspaceExport(job, _inventory, localGeneratedAt);

        Assert.Equal(TimeSpan.Zero, manifest.GeneratedAt.Offset);
        Assert.Equal(localGeneratedAt.ToUniversalTime(), manifest.GeneratedAt);
    }

    // --- Factory: the guarded preconditions ------------------------------------

    [Fact]
    public void ForWorkspaceExport_rejects_a_null_job()
        => Assert.Throws<ArgumentNullException>(
            () => ExportManifest.ForWorkspaceExport(null!, _inventory, _generatedAt));

    [Fact]
    public void ForWorkspaceExport_rejects_a_null_inventory()
    {
        var job = CompletedWorkspaceJob();

        Assert.Throws<ArgumentNullException>(
            () => ExportManifest.ForWorkspaceExport(job, null!, _generatedAt));
    }

    [Fact]
    public void ForWorkspaceExport_rejects_a_user_data_scoped_job()
    {
        // A WORKSPACE export manifest can only be produced for a workspace-scoped export job: a user-data
        // export's manifest is a separate, narrower artifact, so this factory never widens a user-data export
        // into a workspace one (threat T8 "explicit host/admin export scopes").
        var userDataJob = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.UserData, _createdAt);
        userDataJob.Start(_createdAt);
        userDataJob.Complete(_completedAt);

        Assert.Throws<ArgumentException>(
            () => ExportManifest.ForWorkspaceExport(userDataJob, _inventory, _generatedAt));
    }

    [Theory]
    [InlineData(ExportJobStatus.Pending)]
    [InlineData(ExportJobStatus.Running)]
    [InlineData(ExportJobStatus.Failed)]
    public void ForWorkspaceExport_rejects_a_non_completed_job(ExportJobStatus status)
    {
        // A manifest is the OUTPUT of a finished export: it is produced only once the export has completed
        // successfully, so a still-pending, running or failed job has no manifest to describe.
        var job = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.Workspace, _createdAt);
        switch (status)
        {
            case ExportJobStatus.Pending:
                break;
            case ExportJobStatus.Running:
                job.Start(_createdAt);
                break;
            case ExportJobStatus.Failed:
                job.Fail("an internal diagnostic", _createdAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        Assert.Equal(status, job.Status);
        Assert.Throws<ArgumentException>(
            () => ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt));
    }

    // --- Entry invariants ------------------------------------------------------

    [Fact]
    public void Entry_rejects_a_negative_count()
    {
        var job = CompletedWorkspaceJob();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExportManifest.ForWorkspaceExport(
                job,
                new Dictionary<ExportResourceKind, int> { [ExportResourceKind.Scene] = -1 },
                _generatedAt));
    }

    [Fact]
    public void Entry_rejects_an_undefined_kind()
    {
        var job = CompletedWorkspaceJob();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExportManifest.ForWorkspaceExport(
                job,
                new Dictionary<ExportResourceKind, int> { [(ExportResourceKind)999] = 1 },
                _generatedAt));
    }

    // --- Object-level boundary checks (fail-closed isolation) -------------------

    [Fact]
    public void BelongsToOrganization_matches_only_the_owning_tenant()
    {
        var manifest = ExportManifest.ForWorkspaceExport(CompletedWorkspaceJob(), _inventory, _generatedAt);

        Assert.True(manifest.BelongsToOrganization(_organizationId));
        Assert.False(manifest.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(manifest.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void BelongsToWorkspace_matches_only_the_owning_workspace()
    {
        var manifest = ExportManifest.ForWorkspaceExport(CompletedWorkspaceJob(), _inventory, _generatedAt);

        Assert.True(manifest.BelongsToWorkspace(_workspaceId));
        Assert.False(manifest.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(manifest.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_tenant_workspace_and_id_to_all_match()
    {
        var manifest = ExportManifest.ForWorkspaceExport(CompletedWorkspaceJob(), _inventory, _generatedAt);

        Assert.True(manifest.Identifies(_organizationId, _workspaceId, manifest.Id));
        // A foreign tenant, a foreign workspace, a wrong id or an empty id all fail closed.
        Assert.False(manifest.Identifies(_foreignOrganizationId, _workspaceId, manifest.Id));
        Assert.False(manifest.Identifies(_organizationId, _foreignWorkspaceId, manifest.Id));
        Assert.False(manifest.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(manifest.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Log safety (threats T7/T8) --------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_identifier_only()
    {
        var job = CompletedWorkspaceJob();
        var manifest = ExportManifest.ForWorkspaceExport(job, _inventory, _generatedAt);

        var rendered = manifest.ToString();

        Assert.Contains(manifest.Id.ToString(), rendered);
        Assert.Contains($"job={job.Id}", rendered);
        Assert.Contains($"org={_organizationId}", rendered);
        Assert.Contains($"ws={_workspaceId}", rendered);
        Assert.Contains("scope=Workspace", rendered);
        Assert.Contains("total=12", rendered);
    }
}
