using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for the <see cref="Asset"/> invariants and its lifecycle state machine (CORE-AST-001): an
/// asset is the generic "stored file or media object" (docs/03_DOMAIN_LANGUAGE.md); it is workspace-
/// scoped and tenant-scoped and belongs only to the workspace and organization it names; it is METADATA
/// ONLY (csv/database_tables.csv) and is PRIVATE BY DEFAULT (the epic acceptance criterion) — nothing on
/// the aggregate makes it publicly reachable. Its status moves through the guarded Pending -&gt;
/// Available state machine (docs/12_STORAGE_ASSETS.md). The negative-transition cases assert NO mutation
/// happens on a rejected transition, and ToString stays log-safe (threats T4 "Asset leak" and T7 in
/// docs/07_SECURITY_THREAT_MODEL.md): it never carries the storage coordinates that would let private
/// content be located. The object-level boundary checks (BelongsToOrganization/Workspace, Identifies)
/// are the fail-closed isolation guards (threats T5/T1). No vertical-specific terms appear anywhere —
/// generic Asset vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class AssetTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _createdBy = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _confirmedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _storageProvider = "s3";
    private const string _bucket = "livecore-private-assets";
    private const string _objectKey = "org/ws/2026/06/12/asset-001.bin";
    private const string _contentType = "image/png";
    private const string _checksum = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static Asset CreatePending()
        => Asset.Create(
            _organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, _objectKey, _contentType, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_starts_pending_with_no_confirmed_upload()
    {
        var asset = CreatePending();

        Assert.NotEqual(Guid.Empty, asset.Id);
        Assert.Equal(_organizationId, asset.OrganizationId);
        Assert.Equal(_workspaceId, asset.WorkspaceId);
        Assert.Equal(_createdBy, asset.CreatedByUserProfileId);
        Assert.Equal(_storageProvider, asset.StorageProvider);
        Assert.Equal(_bucket, asset.Bucket);
        Assert.Equal(_objectKey, asset.ObjectKey);
        Assert.Equal(_contentType, asset.ContentType);
        // A pending asset has no confirmed upload yet: the size and checksum are unknown.
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Null(asset.SizeBytes);
        Assert.Null(asset.Checksum);
        Assert.False(asset.IsAvailable);
        Assert.True(asset.CanMarkAvailable);
        Assert.Equal(_createdAt, asset.CreatedAt);
        Assert.Equal(_createdAt, asset.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreatePending();
        var second = CreatePending();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_metadata_fields()
    {
        var asset = Asset.Create(
            _organizationId,
            _workspaceId,
            _createdBy,
            "  s3  ",
            "  bucket-name  ",
            "  org/ws/key  ",
            "  image/jpeg  ",
            _createdAt);

        Assert.Equal("s3", asset.StorageProvider);
        Assert.Equal("bucket-name", asset.Bucket);
        Assert.Equal("org/ws/key", asset.ObjectKey);
        Assert.Equal("image/jpeg", asset.ContentType);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var asset = Asset.Create(
            _organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, _objectKey, _contentType, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, asset.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), asset.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(Guid.Empty, _workspaceId, _createdBy, _storageProvider, _bucket, _objectKey, _contentType, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, Guid.Empty, _createdBy, _storageProvider, _bucket, _objectKey, _contentType, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_created_by_id()
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, Guid.Empty, _storageProvider, _bucket, _objectKey, _contentType, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_storage_provider(string? provider)
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, provider!, _bucket, _objectKey, _contentType, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_bucket(string? bucket)
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, bucket!, _objectKey, _contentType, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_object_key(string? objectKey)
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, objectKey!, _contentType, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_content_type(string? contentType)
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, _objectKey, contentType!, _createdAt));

    [Fact]
    public void Create_rejects_an_object_key_with_internal_whitespace()
        // Storage coordinates are single tokens; an object key carrying a space is malformed.
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, "org/ws/ bad key", _contentType, _createdAt));

    [Fact]
    public void Create_rejects_a_content_type_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, _objectKey, "image/\tpng", _createdAt));

    [Fact]
    public void Create_rejects_an_object_key_over_the_length_bound()
    {
        var tooLong = new string('a', Asset.MaxObjectKeyLength + 1);

        Assert.Throws<ArgumentException>(
            () => Asset.Create(_organizationId, _workspaceId, _createdBy, _storageProvider, _bucket, tooLong, _contentType, _createdAt));
    }

    // --- Lifecycle: confirm (mark available) -----------------------------------

    [Fact]
    public void MarkAvailable_records_the_confirmed_upload_and_moves_to_available()
    {
        var asset = CreatePending();

        asset.MarkAvailable(2048, _checksum, _confirmedAt);

        Assert.Equal(AssetStatus.Available, asset.Status);
        Assert.True(asset.IsAvailable);
        Assert.False(asset.CanMarkAvailable);
        Assert.Equal(2048, asset.SizeBytes);
        Assert.Equal(_checksum, asset.Checksum);
        Assert.Equal(_confirmedAt, asset.UpdatedAt);
    }

    [Fact]
    public void MarkAvailable_trims_the_checksum()
    {
        var asset = CreatePending();

        asset.MarkAvailable(10, "  abc123  ", _confirmedAt);

        Assert.Equal("abc123", asset.Checksum);
    }

    [Fact]
    public void MarkAvailable_accepts_a_zero_byte_object()
    {
        var asset = CreatePending();

        asset.MarkAvailable(0, _checksum, _confirmedAt);

        Assert.Equal(0, asset.SizeBytes);
        Assert.Equal(AssetStatus.Available, asset.Status);
    }

    [Fact]
    public void MarkAvailable_on_an_already_available_asset_is_an_invalid_transition_and_does_not_mutate()
    {
        // The confirm transition is valid only from Pending. Confirming an already-available asset must
        // NOT silently overwrite the already-recorded size/checksum: it is a genuine state error.
        var asset = CreatePending();
        asset.MarkAvailable(2048, _checksum, _confirmedAt);

        var laterAttempt = _confirmedAt.AddHours(1);
        var ex = Assert.Throws<InvalidAssetStateTransitionException>(
            () => asset.MarkAvailable(999, "different-checksum", laterAttempt));

        Assert.Equal(asset.Id, ex.AssetId);
        Assert.Equal(AssetStatus.Available, ex.From);
        Assert.Equal(AssetStatus.Available, ex.To);
        // No mutation happened: the originally confirmed size/checksum/updatedAt are intact.
        Assert.Equal(2048, asset.SizeBytes);
        Assert.Equal(_checksum, asset.Checksum);
        Assert.Equal(_confirmedAt, asset.UpdatedAt);
    }

    [Fact]
    public void MarkAvailable_rejects_a_negative_size_with_no_mutation()
    {
        var asset = CreatePending();

        Assert.Throws<ArgumentOutOfRangeException>(() => asset.MarkAvailable(-1, _checksum, _confirmedAt));

        // The rejected confirm left the asset pending and unconfirmed.
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Null(asset.SizeBytes);
        Assert.Null(asset.Checksum);
        Assert.Equal(_createdAt, asset.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MarkAvailable_rejects_a_blank_checksum_with_no_mutation(string? checksum)
    {
        var asset = CreatePending();

        Assert.Throws<ArgumentException>(() => asset.MarkAvailable(2048, checksum!, _confirmedAt));

        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Null(asset.SizeBytes);
        Assert.Null(asset.Checksum);
    }

    // --- Object-level boundary guards (threats T5/T1) --------------------------

    [Fact]
    public void BelongsToOrganization_matches_only_the_owning_tenant()
    {
        var asset = CreatePending();

        Assert.True(asset.BelongsToOrganization(_organizationId));
        Assert.False(asset.BelongsToOrganization(_foreignOrganizationId));
        // An empty id matches nothing — it can never address a tenant.
        Assert.False(asset.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void BelongsToWorkspace_matches_only_the_owning_workspace()
    {
        var asset = CreatePending();

        Assert.True(asset.BelongsToWorkspace(_workspaceId));
        Assert.False(asset.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(asset.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_owning_tenant_workspace_and_id_together()
    {
        var asset = CreatePending();

        Assert.True(asset.Identifies(_organizationId, _workspaceId, asset.Id));
        // A correct id under the wrong tenant or workspace is denied: the surrogate id is only ever
        // honored inside its own tenant and workspace (threats T1/T5).
        Assert.False(asset.Identifies(_foreignOrganizationId, _workspaceId, asset.Id));
        Assert.False(asset.Identifies(_organizationId, _foreignWorkspaceId, asset.Id));
        Assert.False(asset.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(asset.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Status / validation helpers -------------------------------------------

    [Fact]
    public void IsValidStatus_rejects_an_undefined_status()
    {
        Assert.True(Asset.IsValidStatus(AssetStatus.Pending));
        Assert.True(Asset.IsValidStatus(AssetStatus.Available));
        Assert.False(Asset.IsValidStatus((AssetStatus)999));
    }

    // --- Log safety (threats T4 / T7) ------------------------------------------

    [Fact]
    public void ToString_excludes_the_storage_coordinates_and_checksum()
    {
        var asset = CreatePending();
        asset.MarkAvailable(2048, _checksum, _confirmedAt);

        var rendered = asset.ToString();

        // The id, tenant, workspace, creator, status and content type are safe identifiers/metadata.
        Assert.Contains(asset.Id.ToString(), rendered);
        Assert.Contains(_organizationId.ToString(), rendered);
        Assert.Contains(_workspaceId.ToString(), rendered);
        Assert.Contains(_createdBy.ToString(), rendered);
        Assert.Contains(nameof(AssetStatus.Available), rendered);
        Assert.Contains(_contentType, rendered);
        // The storage coordinates and the checksum must NEVER appear: they are the means to LOCATE the
        // private object, so leaking them through logs would defeat "private by default" (threats T4/T7).
        Assert.DoesNotContain(_bucket, rendered);
        Assert.DoesNotContain(_objectKey, rendered);
        Assert.DoesNotContain(_checksum, rendered);
    }
}
