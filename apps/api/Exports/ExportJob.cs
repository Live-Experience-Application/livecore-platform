namespace LiveCore.Api.Exports;

/// <summary>
/// Export job aggregate of the Exports module (CORE-AUD-002, the first story of the "Audit, Export and
/// Recap" epic).
///
/// An <see cref="ExportJob"/> is the Core-owned, product-neutral record of one ASYNCHRONOUS export request
/// (docs/05_MODULE_CONTRACTS.md: the Exports module owns "export jobs", "user data export" and "workspace
/// export"; csv/database_tables.csv: table <c>export_jobs</c>, module Exports, scope <c>workspace</c>,
/// "Async exports"; docs/02_ARCHITECTURE.md: <c>apps/worker</c> owns "background jobs, exports, cleanup,
/// async processing"). It stores no vertical product language — a generic workspace or user-data export
/// only (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv).
///
/// GENERIC AND AUTHORIZED — the epic acceptance criterion ("Audit/export/recap are generic and
/// authorized"). The job is authorized along two structural axes that are immutable for its lifetime:
/// <list type="bullet">
///   <item>TENANT/WORKSPACE SCOPE: the job is workspace-scoped and tenant-scoped (csv/database_tables.csv
///   scopes <c>export_jobs</c> to <c>workspace</c>), carrying both <see cref="OrganizationId"/> (the
///   tenant; checked before the workspace boundary) and <see cref="WorkspaceId"/>, mirroring
///   <c>Asset</c>. The surrogate <see cref="Id"/> alone never authorizes access: every lookup is scoped
///   by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's export job can never
///   be reached through another workspace's or another tenant's id (threat T5 tenant isolation; threat T1
///   broken object-level authorization in docs/07_SECURITY_THREAT_MODEL.md).</item>
///   <item>EXPLICIT EXPORT SCOPE: the job carries the explicit <see cref="ExportScope"/> it was authorized
///   for — the "explicit host/admin export scopes" control for threat T8 ("Export leak"). The scope bounds
///   what the export may ever include (a workspace-wide export versus a single user's own data); it is
///   decided at creation by the requester's authorization and can never be widened afterwards, so a
///   user-data export is never silently promoted into hidden workspace content.</item>
/// </list>
///
/// REQUESTER. <see cref="RequestedByUserProfileId"/> records the authenticated user that requested the
/// export — an export is always requested by an authenticated caller, so it is required at creation. It is
/// a NULLABLE foreign key into <c>users</c> that is SET NULL when the user is deleted (mirrors
/// <c>Asset.CreatedByUserProfileId</c> and <c>Participant.UserProfileId</c>): the audit trail of who
/// requested an export must survive the later deletion of that user, so deleting the user anonymizes the
/// requester rather than cascade-deleting the job. Immutable on the aggregate.
///
/// LIFECYCLE. A job is created <see cref="ExportJobStatus.Pending"/> (queued), a worker
/// <see cref="Start"/>s it (<see cref="ExportJobStatus.Pending"/> -&gt; <see cref="ExportJobStatus.Running"/>),
/// and it settles into exactly one TERMINAL state — <see cref="Complete"/>
/// (<see cref="ExportJobStatus.Completed"/>) or <see cref="Fail"/> (<see cref="ExportJobStatus.Failed"/>,
/// with a generic reason). Every transition is GUARDED: an out-of-order transition is an
/// <see cref="InvalidExportJobStateTransitionException"/>, NOT a no-op, so a finished export can never be
/// silently re-run or have its outcome overwritten. The WORKER that drives these transitions and the
/// produced export MANIFEST (CORE-AUD-003) are later stories and are deliberately not modeled here, exactly
/// as CORE-AST-001 modeled the asset metadata and its lifecycle without the upload flow.
///
/// NO SENSITIVE CONTENT (threats T7/T8). The aggregate holds identifiers, enums and a generic failure
/// reason only — never the exported data and never hidden content. The optional
/// <see cref="FailureReason"/> is a short, single-line diagnostic, and <see cref="ToString"/> excludes it,
/// so a job can never carry exported content into logs.
/// </summary>
public sealed class ExportJob
{
    /// <summary>
    /// Maximum stored length of the generic <see cref="FailureReason"/>. Generous enough for a short
    /// single-line diagnostic message while still rejecting hostile or broken input; the reason is never
    /// exported content (threats T7/T8).
    /// </summary>
    public const int MaxFailureReasonLength = 512;

    /// <summary>Maximum stored length of the produced artifact's storage bucket name (mirrors an asset bucket).</summary>
    public const int MaxArtifactBucketLength = 256;

    /// <summary>Maximum stored length of the produced artifact's storage object key (mirrors an asset object key).</summary>
    public const int MaxArtifactObjectKeyLength = 1024;

    private ExportJob(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid? requestedByUserProfileId,
        ExportScope scope,
        ExportJobStatus status,
        string? failureReason,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Export job id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The requester link is a recorded user reference; when present it must be a real, non-empty
        // surrogate id. A null value means the requesting user was later deleted and the record was
        // anonymized (the column sets null on user deletion), so null is accepted on materialization but
        // an explicit empty id is neither a real user nor a deliberate anonymization and is rejected.
        if (requestedByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A requested-by user profile id must not be empty; pass null only for an anonymized record.",
                nameof(requestedByUserProfileId));
        }

        if (!IsValidScope(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Scope is not a defined export scope.");
        }

        if (!IsValidStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined export job status.");
        }

        // A failure reason is recorded only with the Failed status, and a Failed job always has one. A
        // present reason must be a meaningful, log-safe single line; see IsValidFailureReason.
        var reasonRequired = status == ExportJobStatus.Failed;
        if (reasonRequired && failureReason is null)
        {
            throw new ArgumentException("A failed export job must carry a failure reason.", nameof(failureReason));
        }

        if (!reasonRequired && failureReason is not null)
        {
            throw new ArgumentException(
                "A failure reason may only be recorded for a failed export job.",
                nameof(failureReason));
        }

        if (failureReason is not null && !IsValidFailureReason(failureReason))
        {
            throw new ArgumentException("Failure reason violates the failure reason invariants.", nameof(failureReason));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An export job cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        RequestedByUserProfileId = requestedByUserProfileId;
        Scope = scope;
        Status = status;
        FailureReason = failureReason;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private ExportJob()
    {
    }

    /// <summary>
    /// Surrogate key of the export job row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).
    /// On its own it never authorizes access: lookups are always scoped by <see cref="OrganizationId"/>
    /// and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the export job: the id of the organization that owns the workspace this job
    /// belongs to (the <c>organization_id</c> foreign key to the <c>organizations</c> table). Immutable;
    /// a job never crosses to another organization. The organization boundary is checked before the
    /// workspace boundary, so this column lets isolation be enforced at the row level (threat T5;
    /// docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the export job: the id of the workspace this job belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; a job never moves to
    /// another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// The authenticated user that requested the export (the <c>requested_by</c> foreign key to the
    /// <c>users</c> table). Required at creation (an export is always requested by an authenticated
    /// caller) but a NULLABLE column: it is set null when the user is deleted, so the job survives and is
    /// anonymized rather than cascade-deleted (mirrors <c>Asset.CreatedByUserProfileId</c>). Immutable on
    /// the aggregate.
    /// </summary>
    public Guid? RequestedByUserProfileId { get; }

    /// <summary>
    /// The explicit, generic <see cref="ExportScope"/> this job was authorized for — what data set the
    /// export covers (a workspace-wide export or a single user's own data). Immutable; the scope a job was
    /// authorized for can never be widened afterwards (threat T8 "explicit host/admin export scopes").
    /// </summary>
    public ExportScope Scope { get; }

    /// <summary>
    /// Lifecycle status of the export job. A job is created <see cref="ExportJobStatus.Pending"/> and moves
    /// through the guarded <see cref="Start"/> / <see cref="Complete"/> / <see cref="Fail"/> transitions to
    /// a terminal state (see the type summary).
    /// </summary>
    public ExportJobStatus Status { get; private set; }

    /// <summary>
    /// The generic reason the export failed, set only when <see cref="Status"/> is
    /// <see cref="ExportJobStatus.Failed"/> and otherwise <see langword="null"/>. A short, single-line
    /// diagnostic — never the exported data and never hidden content (threats T7/T8).
    /// </summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// How many times the background worker has ATTEMPTED to process this job and failed (CORE-RES-002).
    /// Starts at zero on creation and is incremented by <see cref="RecordAttempt"/> once per failed processing
    /// attempt; a successful run leaves it untouched (a job that completes on its first try keeps a count of
    /// zero). It is the poison-pill bound: when it reaches the worker's configured maximum the job is
    /// DEAD-LETTERED — moved to the terminal <see cref="ExportJobStatus.Failed"/> state via <see cref="Fail"/>
    /// instead of being retried forever — so a permanently-failing job stops re-consuming a batch slot every
    /// sweep and newer work progresses past it. The count is a plain non-negative integer (no identifier or
    /// content), safe for logs (threat T7).
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>When this export job was first requested (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this export job's status last changed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The background worker REPLICA that currently holds this job's processing LEASE (CORE-RES-003), or
    /// <see langword="null"/> when the job is unleased. Paired with <see cref="LeasedUntil"/>: both are set
    /// together when a replica CLAIMS the job and both cleared together when the lease is released. It is an
    /// opaque per-replica worker identifier (a generated <see cref="Guid"/>) — never a tenant, a user or any
    /// content (threat T7), and never an authorization key (a job is always re-scoped by
    /// <see cref="OrganizationId"/>/<see cref="WorkspaceId"/>; threat T5). Its purpose is purely operational: it
    /// lets exactly one replica process a job at a time and lets a crashed replica's expired lease be reclaimed
    /// (the claim is a compare-and-swap on this owner; see <see cref="ExportJobRepository"/>).
    /// </summary>
    public Guid? LeaseOwner { get; private set; }

    /// <summary>
    /// When the current processing LEASE EXPIRES (UTC), or <see langword="null"/> when the job is unleased
    /// (CORE-RES-003). While the lease is unexpired (<see cref="LeasedUntil"/> is in the future) the holding
    /// replica owns the job and no other replica processes it; once it has lapsed the job is RECLAIMABLE, so a
    /// replica that crashed mid-job cannot strand the job forever — the next sweep reclaims the expired lease and
    /// finishes the work. The lease therefore makes correctness rest on the claim rather than solely on the
    /// downstream unique <c>export_manifests(export_job_id)</c> index (which remains a backstop). It carries no
    /// content (threat T7).
    /// </summary>
    public DateTimeOffset? LeasedUntil { get; private set; }

    /// <summary>
    /// The private object-storage BUCKET that holds the completed export's downloadable ARTIFACT (its produced
    /// blob), or <see langword="null"/> when the export has no object-storage artifact. Paired with
    /// <see cref="ArtifactObjectKey"/>: both are set together by <see cref="RecordArtifact"/> or both null.
    ///
    /// <para>
    /// WHY IT IS OPTIONAL. In the Core model a completed workspace export's produced artifact is its
    /// <see cref="ExportManifest"/> — a durable DATABASE row of per-kind counts (CORE-EXP-001) — so Core's own
    /// manifest-only export pipeline writes NO object-storage blob and leaves these coordinates null. The columns
    /// exist so a deployment whose export pipeline DOES produce a downloadable blob can record WHERE that blob
    /// lives, which is what lets the data-retention sweep (CORE-PRIV-003) purge the object together with the row
    /// when the export is past its retention window (the "completed export artifacts (DB row + the S3 object)"
    /// acceptance criterion). The coordinate is storage NAMING only (a private bucket + key, never a credential
    /// and never a public URL; threat T4/T7) — the endpoint and credentials stay inside the deployment-supplied
    /// <see cref="LiveCore.Api.Assets.IAssetStorage"/> adapter.
    /// </para>
    /// </summary>
    public string? ArtifactBucket { get; private set; }

    /// <summary>
    /// The private object-storage OBJECT KEY of the completed export's downloadable artifact within
    /// <see cref="ArtifactBucket"/>, or <see langword="null"/> when the export has no object-storage artifact.
    /// Paired with <see cref="ArtifactBucket"/> (see that property for why the artifact is optional). It is a
    /// storage coordinate only — never exported content and never a means to reach private content without an
    /// authorized signed URL (threats T4/T7).
    /// </summary>
    public string? ArtifactObjectKey { get; private set; }

    /// <summary>
    /// Whether this export records an object-storage artifact (both <see cref="ArtifactBucket"/> and
    /// <see cref="ArtifactObjectKey"/> are set). When <see langword="true"/> the data-retention sweep deletes
    /// that object before it removes the export row (object-first-then-row), so a purged export never leaves an
    /// orphaned object behind (mirrors the asset cleanup invariant).
    /// </summary>
    public bool HasArtifact => ArtifactBucket is not null && ArtifactObjectKey is not null;

    /// <summary>
    /// Whether the job has reached a terminal state (<see cref="ExportJobStatus.Completed"/> or
    /// <see cref="ExportJobStatus.Failed"/>). A terminal job never transitions again.
    /// </summary>
    public bool IsTerminal => Status is ExportJobStatus.Completed or ExportJobStatus.Failed;

    /// <summary>
    /// Whether the job may be started right now: only a still-pending job may be started. Lets the
    /// application layer branch without catching <see cref="InvalidExportJobStateTransitionException"/>.
    /// </summary>
    public bool CanStart => Status == ExportJobStatus.Pending;

    /// <summary>
    /// Whether the job may be completed right now: only a running job may be completed.
    /// </summary>
    public bool CanComplete => Status == ExportJobStatus.Running;

    /// <summary>
    /// Whether the job may be failed right now: only a non-terminal (pending or running) job may be failed.
    /// </summary>
    public bool CanFail => !IsTerminal;

    /// <summary>
    /// Registers a new PENDING export job in the given workspace (owned by the given organization),
    /// requested by the given authenticated user, for the given explicit <see cref="ExportScope"/>. The
    /// job starts <see cref="ExportJobStatus.Pending"/> with no failure reason; a worker later
    /// <see cref="Start"/>s and <see cref="Complete"/>s or <see cref="Fail"/>s it (a later story). The
    /// caller (a later export-request endpoint) supplies the already-resolved tenant, workspace, the
    /// authenticated requester and the scope the requester is authorized for.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id, workspace id or requester id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The scope is not a defined <see cref="ExportScope"/>.</exception>
    public static ExportJob Create(
        Guid organizationId,
        Guid workspaceId,
        Guid requestedByUserProfileId,
        ExportScope scope,
        DateTimeOffset createdAt)
    {
        if (requestedByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Requested-by user profile id must not be empty.", nameof(requestedByUserProfileId));
        }

        return new ExportJob(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            requestedByUserProfileId,
            scope,
            ExportJobStatus.Pending,
            failureReason: null,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this export job belongs to exactly the given organization. Empty ids match nothing. This is
    /// the tenant-boundary check, checked before the workspace boundary: a job belongs only to the
    /// organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this export job belongs to exactly the given workspace. Empty ids match nothing. This is
    /// the workspace-boundary check: a job belongs only to the workspace it names, never to any other
    /// (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this export job is the one identified by the given id WITHIN the given organization and
    /// workspace. All three must match; an empty id matches nothing. A job with the same id under a
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
    /// Moves the job <see cref="ExportJobStatus.Pending"/> -&gt; <see cref="ExportJobStatus.Running"/> when
    /// a worker begins producing the export. The tenant, workspace, requester and scope are immutable, so
    /// starting never moves the job and never widens its scope (threats T5/T8). Starting a non-pending job
    /// is an invalid transition, NOT a no-op: a job must never be processed twice. Callers that want to
    /// avoid the exception can pre-check <see cref="CanStart"/>.
    /// </summary>
    /// <param name="updatedAt">When processing started.</param>
    /// <exception cref="InvalidExportJobStateTransitionException">The job is not <see cref="ExportJobStatus.Pending"/>.</exception>
    public void Start(DateTimeOffset updatedAt)
    {
        if (!CanStart)
        {
            throw new InvalidExportJobStateTransitionException(Id, Status, ExportJobStatus.Running);
        }

        Status = ExportJobStatus.Running;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Moves the job <see cref="ExportJobStatus.Running"/> -&gt; <see cref="ExportJobStatus.Completed"/>
    /// when the export finishes successfully. Completing a non-running job is an invalid transition, NOT a
    /// no-op: a finished export can never be silently re-run or overwritten. Callers that want to avoid the
    /// exception can pre-check <see cref="CanComplete"/>.
    /// </summary>
    /// <param name="updatedAt">When the export completed.</param>
    /// <exception cref="InvalidExportJobStateTransitionException">The job is not <see cref="ExportJobStatus.Running"/>.</exception>
    public void Complete(DateTimeOffset updatedAt)
    {
        if (!CanComplete)
        {
            throw new InvalidExportJobStateTransitionException(Id, Status, ExportJobStatus.Completed);
        }

        Status = ExportJobStatus.Completed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Moves the job to <see cref="ExportJobStatus.Failed"/> from a non-terminal (pending or running)
    /// state and records a generic <paramref name="reason"/>. Failing a terminal job is an invalid
    /// transition, NOT a no-op: a completed or already-failed export can never be silently re-judged.
    /// Callers that want to avoid the exception can pre-check <see cref="CanFail"/>. The reason is a short,
    /// single-line diagnostic and is never exported content (threats T7/T8).
    /// </summary>
    /// <param name="reason">The generic failure reason (non-blank, single-line, bounded).</param>
    /// <param name="updatedAt">When the export failed.</param>
    /// <exception cref="InvalidExportJobStateTransitionException">The job is already terminal.</exception>
    /// <exception cref="ArgumentException">The reason violates an invariant.</exception>
    public void Fail(string reason, DateTimeOffset updatedAt)
    {
        if (!CanFail)
        {
            throw new InvalidExportJobStateTransitionException(Id, Status, ExportJobStatus.Failed);
        }

        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (!IsValidFailureReason(trimmedReason))
        {
            throw new ArgumentException("Failure reason violates the failure reason invariants.", nameof(reason));
        }

        Status = ExportJobStatus.Failed;
        FailureReason = trimmedReason;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Records that the background worker has just ATTEMPTED to process this job and the attempt failed
    /// (CORE-RES-002): increments <see cref="AttemptCount"/> by one and stamps <see cref="UpdatedAt"/>. The
    /// worker calls this — in its OWN unit of work, separate from the rolled-back processing transaction — so a
    /// failed attempt is recorded durably even though the work itself did not commit; once the count reaches
    /// the worker's configured maximum the worker DEAD-LETTERS the job via <see cref="Fail"/>. An attempt is
    /// only meaningful on a job that is still being worked, so recording one on a terminal
    /// (<see cref="ExportJobStatus.Completed"/> or <see cref="ExportJobStatus.Failed"/>) job is an
    /// <see cref="InvalidOperationException"/>, NOT a silent no-op: a finished job is never re-attempted. The
    /// transition tenant, workspace, requester and scope are immutable, so recording an attempt never moves the
    /// job or widens its scope (threats T5/T8), and the counter carries no content (threat T7).
    /// </summary>
    /// <param name="updatedAt">When the failed attempt was recorded.</param>
    /// <exception cref="InvalidOperationException">The job is already terminal.</exception>
    public void RecordAttempt(DateTimeOffset updatedAt)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException(
                "A processing attempt cannot be recorded for a terminal export job.");
        }

        AttemptCount++;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether a worker replica may CLAIM (lease) this job right now (CORE-RES-003): only a NON-TERMINAL job
    /// whose lease is absent or has already EXPIRED at <paramref name="now"/> is claimable. A live, unexpired
    /// lease means another replica is processing the job, so it is left alone; an expired lease (a crashed
    /// replica) is reclaimable. A terminal (<see cref="ExportJobStatus.Completed"/>/<see cref="ExportJobStatus.Failed"/>)
    /// job is never claimable — a finished export is never re-processed.
    /// </summary>
    /// <param name="now">The current UTC time the lease expiry is judged against.</param>
    public bool CanAcquireLease(DateTimeOffset now)
        => !IsTerminal && (LeasedUntil is null || LeasedUntil.Value <= now);

    /// <summary>
    /// CLAIMS this job for the given worker replica until <paramref name="leasedUntil"/> (CORE-RES-003): records
    /// the <paramref name="leaseOwner"/> and the lease expiry so exactly one replica processes the job at a time.
    /// The tenant, workspace, requester, scope and status are untouched — a claim never moves the job or widens
    /// its scope (threats T5/T8), it only records operational lease metadata.
    ///
    /// <para>
    /// The atomic, cross-replica claim is performed set-based by
    /// <see cref="ExportJobRepository.ClaimAsync"/> (a compare-and-swap on <see cref="LeaseOwner"/> so two
    /// replicas can never both claim the same row); this in-memory method models the SAME state transition on a
    /// loaded aggregate (for example a single-process worker) and GUARDS it: a job that is not currently
    /// <see cref="CanAcquireLease"/> throws rather than silently stealing a live lease.
    /// </para>
    /// </summary>
    /// <param name="leaseOwner">The non-empty worker-replica identifier taking the lease.</param>
    /// <param name="leasedUntil">When the lease expires (must be strictly after <paramref name="now"/>).</param>
    /// <param name="now">The current UTC time the lease is taken at.</param>
    /// <exception cref="ArgumentException">The lease owner is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lease expiry is not after <paramref name="now"/>.</exception>
    /// <exception cref="InvalidOperationException">The job is terminal or already holds an unexpired lease.</exception>
    public void AcquireLease(Guid leaseOwner, DateTimeOffset leasedUntil, DateTimeOffset now)
    {
        if (leaseOwner == Guid.Empty)
        {
            throw new ArgumentException("Lease owner must not be empty.", nameof(leaseOwner));
        }

        if (leasedUntil <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leasedUntil), leasedUntil, "The lease expiry must be after the current time.");
        }

        if (!CanAcquireLease(now))
        {
            throw new InvalidOperationException(
                "The export job is terminal or already holds an unexpired lease and cannot be claimed.");
        }

        LeaseOwner = leaseOwner;
        LeasedUntil = leasedUntil.ToUniversalTime();
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>
    /// RELEASES any lease this job holds (CORE-RES-003): clears <see cref="LeaseOwner"/> and
    /// <see cref="LeasedUntil"/> so the job is immediately reclaimable. The worker calls this after a FAILED
    /// processing attempt (in the same unit of work that records the attempt) so the job does not have to wait
    /// out its lease before the next sweep retries it. Releasing an already-unleased job is an idempotent no-op,
    /// not an error. The tenant, workspace, requester and scope are untouched (threats T5/T8).
    /// </summary>
    /// <param name="updatedAt">When the lease was released (UTC).</param>
    public void ReleaseLease(DateTimeOffset updatedAt)
    {
        LeaseOwner = null;
        LeasedUntil = null;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Records the object-storage coordinates of the completed export's downloadable ARTIFACT (its produced
    /// blob) so the artifact object can be purged with the row when the export reaches its retention window
    /// (CORE-PRIV-003). The bucket and key are set TOGETHER (an artifact is a bucket + key pair); both are
    /// validated as single storage tokens (non-blank, bounded, no whitespace or control characters), exactly
    /// like an asset's storage coordinates, so a broken coordinate can never reach the row. The tenant,
    /// workspace, requester, scope and status are unchanged — recording the artifact never moves the job or
    /// widens its scope (threats T5/T8). The coordinate is storage NAMING only, never exported content and
    /// never a credential (threats T4/T7).
    /// </summary>
    /// <param name="bucket">The private bucket the artifact object lives in.</param>
    /// <param name="objectKey">The artifact object's key within the bucket.</param>
    /// <param name="updatedAt">When the artifact was recorded.</param>
    /// <exception cref="ArgumentException">The bucket or object key violates the storage-token invariants.</exception>
    public void RecordArtifact(string bucket, string objectKey, DateTimeOffset updatedAt)
    {
        var trimmedBucket = bucket?.Trim() ?? string.Empty;
        var trimmedObjectKey = objectKey?.Trim() ?? string.Empty;

        if (!IsValidArtifactBucket(trimmedBucket))
        {
            throw new ArgumentException("Artifact bucket violates the storage-token invariants.", nameof(bucket));
        }

        if (!IsValidArtifactObjectKey(trimmedObjectKey))
        {
            throw new ArgumentException("Artifact object key violates the storage-token invariants.", nameof(objectKey));
        }

        ArtifactBucket = trimmedBucket;
        ArtifactObjectKey = trimmedObjectKey;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid artifact bucket: a non-blank, bounded single storage token free of
    /// whitespace and control characters (mirrors the asset bucket invariants).
    /// </summary>
    public static bool IsValidArtifactBucket(string? value) => IsStorageToken(value, MaxArtifactBucketLength);

    /// <summary>
    /// Whether the given value is a valid artifact object key: a non-blank, bounded single storage token free
    /// of whitespace and control characters (an object key may carry '/' path separators but no spaces).
    /// </summary>
    public static bool IsValidArtifactObjectKey(string? value) => IsStorageToken(value, MaxArtifactObjectKeyLength);

    /// <summary>
    /// Whether the given value is a valid single storage token: non-blank, within the length bound and free of
    /// any whitespace or control characters (storage coordinates carry no internal whitespace).
    /// </summary>
    private static bool IsStorageToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && !value.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Whether the given value is a defined <see cref="ExportScope"/>. Used to reject undefined enum values
    /// that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidScope(ExportScope scope) => Enum.IsDefined(scope);

    /// <summary>
    /// Whether the given value is a defined <see cref="ExportJobStatus"/>. Used to reject undefined enum
    /// values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidStatus(ExportJobStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Whether the given value is a valid failure reason: non-blank, within the length bound and free of
    /// control characters (so it stays a single, log-safe line — threat T7). A reason may legitimately
    /// contain spaces, so unlike storage tokens it is not whitespace-free, only control-character-free.
    /// </summary>
    public static bool IsValidFailureReason(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxFailureReasonLength
            && !value.Any(static c => char.IsControl(c));

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: job id, organization id, workspace
    /// id, requester (or <c>anonymized</c>), scope and status. The <see cref="FailureReason"/> is
    /// deliberately EXCLUDED so no free-form diagnostic text ever reaches logs through the aggregate
    /// (threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not content).
    /// </summary>
    public override string ToString()
        => $"ExportJob {Id} org={OrganizationId} ws={WorkspaceId} "
            + $"requestedBy={(RequestedByUserProfileId is { } requester ? requester.ToString() : "anonymized")} "
            + $"scope={Scope} status={Status} attempts={AttemptCount}";
}
