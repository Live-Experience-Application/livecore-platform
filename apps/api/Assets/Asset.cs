namespace LiveCore.Api.Assets;

/// <summary>
/// Asset aggregate of the Assets module (CORE-AST-001, the first story of the "Asset Storage and
/// Authorization" epic).
///
/// An <see cref="Asset"/> is the Core-owned, product-neutral "stored file or media object"
/// (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md: the Assets module owns "asset metadata",
/// the "storage adapter", "upload/download authorization" and "signed URL creation";
/// csv/database_tables.csv: table <c>assets</c>, module Assets, scope <c>workspace</c>, "Metadata
/// only"). It stores no vertical product language — it is a generic asset only
/// (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv). This row is METADATA ONLY: it never
/// holds the binary content, which lives in S3-compatible object storage (docs/12_STORAGE_ASSETS.md;
/// docs/02_ARCHITECTURE.md "S3-compatible storage abstraction"; ADR 0006), so "storing large binary
/// files in PostgreSQL" is avoided (docs/12_STORAGE_ASSETS.md "Avoid").
///
/// PRIVATE BY DEFAULT — the epic acceptance criterion ("Assets are private by default and accessed only
/// through authorized signed URLs"). The metadata model carries NO public-access affordance: there is
/// no public URL, no "is public" flag and no shareable token. The storage coordinates
/// (<see cref="StorageProvider"/>, <see cref="Bucket"/>, <see cref="ObjectKey"/>) are internal
/// addressing only — the bucket is private (docs/12_STORAGE_ASSETS.md "buckets private by default", "no
/// public object listing") and the object is reached only through a short-lived, authorized signed URL
/// after a server-side permission check (the signed download flow is CORE-AST-004; threat T4 "Asset
/// leak" in docs/07_SECURITY_THREAT_MODEL.md). The coordinates are never participant-facing and never
/// written to logs (threat T7; see <see cref="ToString"/>). An asset is therefore private in EVERY
/// status, and nothing on this aggregate can make it public.
///
/// Tenant boundary: csv/database_tables.csv scopes <c>assets</c> to <c>workspace</c>. So an asset is
/// workspace-scoped and tenant-scoped, carrying both <see cref="OrganizationId"/> (the tenant; checked
/// before the workspace boundary) and <see cref="WorkspaceId"/>, mirroring <c>Entity</c> and
/// <c>Session</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; the documented critical index is
/// <c>assets(workspace_id, id)</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). An asset belongs to
/// exactly one organization and one workspace for its whole lifetime; it never crosses to another tenant
/// or workspace. The surrogate <see cref="Id"/> alone never authorizes access: every lookup is scoped
/// by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's asset can never be
/// reached through another workspace's or another tenant's id (threat T5; T1 broken object-level
/// authorization).
///
/// Creator: <see cref="CreatedByUserProfileId"/> records the authenticated user that registered the
/// asset (docs/12_STORAGE_ASSETS.md metadata field <c>created_by</c>). An asset is always created by an
/// authenticated host (creating an upload intent requires authorization — a host role per
/// csv/api_routes.csv, CORE-AST-003), so it is required at creation. It is a NULLABLE foreign key into
/// <c>users</c> that is set null when the user is deleted (mirrors <c>Participant.UserProfileId</c>): an
/// asset must survive the later deletion of the user who created it (other content may link to it), so
/// deleting the user anonymizes the creator rather than cascade-deleting the asset.
///
/// Lifecycle invariant: an asset is created <see cref="AssetStatus.Pending"/> when its upload intent is
/// registered (the object may not yet exist; <see cref="SizeBytes"/> and <see cref="Checksum"/> are
/// null), and <see cref="MarkAvailable"/> records the confirmed upload (size + checksum) and moves it to
/// <see cref="AssetStatus.Available"/> — the lifecycle behind docs/12_STORAGE_ASSETS.md's "client
/// confirms upload -&gt; Core stores asset metadata". The transition is guarded: marking an
/// already-available (or otherwise non-pending) asset available is an
/// <see cref="InvalidAssetStateTransitionException"/>, NOT a no-op, so a confirm cannot silently
/// overwrite a different already-recorded size/checksum. The upload-intent and confirm COMMANDS that
/// drive this transition, and the soft-delete state of the cleanup job (CORE-AST-006), are later stories
/// and are deliberately not modeled here.
/// </summary>
public sealed class Asset
{
    /// <summary>
    /// Maximum accepted storage-provider length. The provider is a short identifier of the
    /// S3-compatible backend (for example <c>s3</c> or <c>rustfs</c>, docs/12_STORAGE_ASSETS.md);
    /// longer values are rejected as hostile or broken input.
    /// </summary>
    public const int MaxStorageProviderLength = 64;

    /// <summary>
    /// Maximum accepted bucket-name length. Generous enough for any S3-compatible provider's bucket
    /// naming while still rejecting hostile or broken input.
    /// </summary>
    public const int MaxBucketLength = 256;

    /// <summary>
    /// Maximum accepted object-key length. S3-compatible object keys are bounded at 1024 bytes; the
    /// model uses the same character bound.
    /// </summary>
    public const int MaxObjectKeyLength = 1024;

    /// <summary>
    /// Maximum accepted content-type length. A MIME type (for example <c>image/png</c>); longer values
    /// are rejected as hostile or broken input.
    /// </summary>
    public const int MaxContentTypeLength = 255;

    /// <summary>
    /// Maximum accepted checksum length. Comfortably fits a hex- or base64-encoded SHA-256/512 digest
    /// (docs/12_STORAGE_ASSETS.md metadata field <c>checksum</c>); longer values are rejected.
    /// </summary>
    public const int MaxChecksumLength = 128;

    private Asset(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid? createdByUserProfileId,
        string storageProvider,
        string bucket,
        string objectKey,
        string contentType,
        long? sizeBytes,
        string? checksum,
        AssetStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The creator link is a recorded user reference; when present it must be a real, non-empty
        // surrogate id. A null value means the creating user was later deleted and the record was
        // anonymized (the column sets null on user deletion), so null is accepted on materialization but
        // an explicit empty id is neither a real user nor a deliberate anonymization and is rejected.
        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A created-by user profile id must not be empty; pass null only for an anonymized record.",
                nameof(createdByUserProfileId));
        }

        if (!IsValidStorageProvider(storageProvider))
        {
            throw new ArgumentException("Storage provider violates the storage provider invariants.", nameof(storageProvider));
        }

        if (!IsValidBucket(bucket))
        {
            throw new ArgumentException("Bucket violates the bucket invariants.", nameof(bucket));
        }

        if (!IsValidObjectKey(objectKey))
        {
            throw new ArgumentException("Object key violates the object key invariants.", nameof(objectKey));
        }

        if (!IsValidContentType(contentType))
        {
            throw new ArgumentException("Content type violates the content type invariants.", nameof(contentType));
        }

        if (sizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Size in bytes must not be negative.");
        }

        if (checksum is not null && !IsValidChecksum(checksum))
        {
            throw new ArgumentException("Checksum violates the checksum invariants.", nameof(checksum));
        }

        if (!IsValidStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined asset status.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An asset cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        CreatedByUserProfileId = createdByUserProfileId;
        StorageProvider = storageProvider;
        Bucket = bucket;
        ObjectKey = objectKey;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Checksum = checksum;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Asset()
    {
        StorageProvider = null!;
        Bucket = null!;
        ObjectKey = null!;
        ContentType = null!;
    }

    /// <summary>
    /// Surrogate key of the asset row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md). On
    /// its own it never authorizes access: lookups are always scoped by <see cref="OrganizationId"/> and
    /// <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the asset: the id of the organization that owns the workspace this asset
    /// belongs to (the <c>organization_id</c> foreign key to the <c>organizations</c> table). Immutable;
    /// an asset never crosses to another organization. The organization boundary is checked before the
    /// workspace boundary, so this column lets isolation be enforced at the row level (threat T5;
    /// docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the asset: the id of the workspace this asset belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; an asset never moves
    /// to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// The authenticated user that registered the asset (docs/12_STORAGE_ASSETS.md metadata field
    /// <c>created_by</c>; the <c>created_by</c> foreign key to the <c>users</c> table). Required at
    /// creation (an asset is always created by an authenticated host) but a NULLABLE column: it is set
    /// null when the user is deleted, so the asset survives and is anonymized rather than cascade-deleted
    /// (mirrors <c>Participant.UserProfileId</c>). Immutable on the aggregate.
    /// </summary>
    public Guid? CreatedByUserProfileId { get; }

    /// <summary>
    /// The S3-compatible storage backend the object lives in (for example <c>s3</c> or <c>rustfs</c>,
    /// docs/12_STORAGE_ASSETS.md). Internal addressing only: never participant-facing, never an
    /// authorization input, never written to logs (threat T7; see <see cref="ToString"/>).
    /// </summary>
    public string StorageProvider { get; }

    /// <summary>
    /// The private storage bucket the object lives in (docs/12_STORAGE_ASSETS.md: "buckets private by
    /// default"). Internal addressing only: never participant-facing, never a public URL, never written
    /// to logs (threat T4/T7).
    /// </summary>
    public string Bucket { get; }

    /// <summary>
    /// The object key (path) of the stored object within its bucket (docs/12_STORAGE_ASSETS.md
    /// <c>object_key</c>). Internal addressing only — reached solely through an authorized, short-lived
    /// signed URL (CORE-AST-004), never a static or public URL (threat T4); never written to logs
    /// (threat T7).
    /// </summary>
    public string ObjectKey { get; }

    /// <summary>
    /// The MIME content type of the stored object (docs/12_STORAGE_ASSETS.md <c>content_type</c>).
    /// Informational metadata supplied at upload-intent time.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// The size of the stored object in bytes (docs/12_STORAGE_ASSETS.md <c>size_bytes</c>). It is the
    /// client-DECLARED object size recorded at upload-intent when the deployment enforces the
    /// <c>asset.storage.bytes.max</c> storage quota (CORE-MON-006) — the bytes reserved against the
    /// workspace's storage allowance, which the host-initiated deletion releases — and otherwise
    /// <see langword="null"/> while the asset is still <see cref="AssetStatus.Pending"/> (no size was declared
    /// and the upload is not yet confirmed). It is stamped (or refined to the confirmed size) by
    /// <see cref="MarkAvailable"/>. Never negative.
    /// </summary>
    public long? SizeBytes { get; private set; }

    /// <summary>
    /// The checksum of the stored object (docs/12_STORAGE_ASSETS.md <c>checksum</c>; for example a
    /// hex SHA-256 digest), or <see langword="null"/> while the asset is still
    /// <see cref="AssetStatus.Pending"/>. Recorded by <see cref="MarkAvailable"/> and used to verify
    /// upload integrity. Informational metadata, not an authorization input.
    /// </summary>
    public string? Checksum { get; private set; }

    /// <summary>
    /// Lifecycle status of the asset. An asset is created <see cref="AssetStatus.Pending"/> and moves to
    /// <see cref="AssetStatus.Available"/> via the guarded <see cref="MarkAvailable"/> transition. Both
    /// states are private (see the type summary).
    /// </summary>
    public AssetStatus Status { get; private set; }

    /// <summary>When this asset was first registered (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this asset was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Whether the asset's upload has been confirmed and it is usable (<see cref="AssetStatus.Available"/>).
    /// An available asset is still private: it is reachable only through an authorized signed URL.
    /// </summary>
    public bool IsAvailable => Status == AssetStatus.Available;

    /// <summary>
    /// Whether the asset may be marked available right now: only a still-pending asset may be confirmed.
    /// Lets the application layer branch without catching
    /// <see cref="InvalidAssetStateTransitionException"/>.
    /// </summary>
    public bool CanMarkAvailable => Status == AssetStatus.Pending;

    /// <summary>
    /// Registers a new PENDING asset in the given workspace (owned by the given organization), created by
    /// the given authenticated user. The asset starts <see cref="AssetStatus.Pending"/> with no confirmed
    /// upload yet (the state behind docs/12_STORAGE_ASSETS.md's "Create upload intent"); the
    /// <see cref="Checksum"/> is <see langword="null"/> until the upload is confirmed
    /// (<see cref="MarkAvailable"/>). The optional <paramref name="sizeBytes"/> is the client-DECLARED object
    /// size the upload-intent reserves against the workspace's <c>asset.storage.bytes.max</c> storage quota
    /// (CORE-MON-006); omit it (the default <see langword="null"/>) when no size is declared. The asset is
    /// PRIVATE — nothing here makes it publicly reachable; access is only ever through an authorized signed
    /// URL (CORE-AST-004). The caller (the upload-intent endpoint, CORE-AST-003) supplies the
    /// already-resolved tenant, workspace and creator and the server-assigned storage coordinates and the
    /// client-declared content type.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or creator id is empty, or a storage coordinate or the content
    /// type violates an invariant.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The declared size is negative.</exception>
    public static Asset Create(
        Guid organizationId,
        Guid workspaceId,
        Guid createdByUserProfileId,
        string storageProvider,
        string bucket,
        string objectKey,
        string contentType,
        DateTimeOffset createdAt,
        long? sizeBytes = null)
    {
        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Created-by user profile id must not be empty.", nameof(createdByUserProfileId));
        }

        return new Asset(
            // UUIDv7 id derived from the creation time so ordering by id (ListByWorkspace,
            // ListExpiredPending) is chronological even for rows created in the same wall-clock ms.
            Guid.CreateVersion7(createdAt),
            organizationId,
            workspaceId,
            createdByUserProfileId,
            storageProvider?.Trim() ?? string.Empty,
            bucket?.Trim() ?? string.Empty,
            // The object key is significant whitespace-free but may legitimately contain '/'
            // path separators; it is trimmed of surrounding whitespace only.
            objectKey?.Trim() ?? string.Empty,
            contentType?.Trim() ?? string.Empty,
            sizeBytes,
            checksum: null,
            AssetStatus.Pending,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this asset belongs to exactly the given organization. Empty ids match nothing. This is the
    /// tenant-boundary check, checked before the workspace boundary: an asset belongs only to the
    /// organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this asset belongs to exactly the given workspace. Empty ids match nothing. This is the
    /// workspace-boundary check: an asset belongs only to the workspace it names, never to any other
    /// (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this asset is the one identified by the given id WITHIN the given organization and
    /// workspace. All three must match; an empty id matches nothing. An asset with the same id under a
    /// different tenant or workspace (which cannot happen for a single row but guards the caller's intent)
    /// returns <see langword="false"/>: the surrogate id is only ever honored inside its tenant and
    /// workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Records the confirmed upload and moves the asset <see cref="AssetStatus.Pending"/> -&gt;
    /// <see cref="AssetStatus.Available"/>, stamping the now-known <see cref="SizeBytes"/> and
    /// <see cref="Checksum"/> (the state behind docs/12_STORAGE_ASSETS.md's "client confirms upload -&gt;
    /// Core stores asset metadata"). The organization, workspace, id, creator and storage coordinates are
    /// immutable, so confirming never moves the asset and never makes it public (threat T4/T5). Confirming
    /// a non-pending asset is an invalid transition, NOT a no-op: a confirm must never silently overwrite a
    /// different already-recorded size/checksum. Callers that want to avoid the exception can pre-check
    /// <see cref="CanMarkAvailable"/>.
    /// </summary>
    /// <param name="sizeBytes">The confirmed object size in bytes (non-negative).</param>
    /// <param name="checksum">The confirmed object checksum (non-blank, bounded).</param>
    /// <param name="updatedAt">When the upload was confirmed.</param>
    /// <exception cref="InvalidAssetStateTransitionException">The asset is not <see cref="AssetStatus.Pending"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size is negative.</exception>
    /// <exception cref="ArgumentException">The checksum violates an invariant.</exception>
    public void MarkAvailable(long sizeBytes, string checksum, DateTimeOffset updatedAt)
    {
        if (!CanMarkAvailable)
        {
            throw new InvalidAssetStateTransitionException(Id, Status, AssetStatus.Available);
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Size in bytes must not be negative.");
        }

        var trimmedChecksum = checksum?.Trim() ?? string.Empty;
        if (!IsValidChecksum(trimmedChecksum))
        {
            throw new ArgumentException("Checksum violates the checksum invariants.", nameof(checksum));
        }

        SizeBytes = sizeBytes;
        Checksum = trimmedChecksum;
        Status = AssetStatus.Available;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid storage provider: non-blank, within the length bound and free
    /// of control characters and whitespace.
    /// </summary>
    public static bool IsValidStorageProvider(string? value)
        => IsValidToken(value, MaxStorageProviderLength);

    /// <summary>
    /// Whether the given value is a valid bucket name: non-blank, within the length bound and free of
    /// control characters and whitespace.
    /// </summary>
    public static bool IsValidBucket(string? value)
        => IsValidToken(value, MaxBucketLength);

    /// <summary>
    /// Whether the given value is a valid object key: non-blank, within the length bound, free of control
    /// characters and free of surrounding/internal whitespace (object keys may contain '/' path
    /// separators but no spaces).
    /// </summary>
    public static bool IsValidObjectKey(string? value)
        => IsValidToken(value, MaxObjectKeyLength);

    /// <summary>
    /// Whether the given value is a valid content type: non-blank, within the length bound and free of
    /// control characters and whitespace (a MIME type carries no spaces).
    /// </summary>
    public static bool IsValidContentType(string? value)
        => IsValidToken(value, MaxContentTypeLength);

    /// <summary>
    /// Whether the given value is a valid checksum: non-blank, within the length bound and free of
    /// control characters and whitespace.
    /// </summary>
    public static bool IsValidChecksum(string? value)
        => IsValidToken(value, MaxChecksumLength);

    /// <summary>
    /// Whether the given value is a defined <see cref="AssetStatus"/>. Used to reject undefined enum
    /// values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidStatus(AssetStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Whether the given value is a valid single-token metadata value: non-null, non-blank, within the
    /// length bound and free of any whitespace or control characters. Storage coordinates, content types
    /// and checksums are all single tokens without internal whitespace, so a value carrying a space, tab
    /// or control character is rejected as malformed.
    /// </summary>
    private static bool IsValidToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && !value.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: asset id, organization id,
    /// workspace id, creator (or <c>anonymized</c>), status and content type. The storage coordinates
    /// (provider, bucket, object key) and the checksum are deliberately EXCLUDED so an asset can never be
    /// located or addressed from logs (threats T4 "Asset leak" and T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not the means to reach private content).
    /// </summary>
    public override string ToString()
        => $"Asset {Id} org={OrganizationId} ws={WorkspaceId} "
            + $"createdBy={(CreatedByUserProfileId is { } creator ? creator.ToString() : "anonymized")} "
            + $"status={Status} contentType={ContentType}";
}
