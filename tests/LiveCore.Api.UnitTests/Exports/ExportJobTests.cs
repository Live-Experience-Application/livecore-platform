using LiveCore.Api.Exports;

namespace LiveCore.Api.UnitTests.Exports;

/// <summary>
/// Unit tests for the <see cref="ExportJob"/> invariants and its lifecycle state machine (CORE-AUD-002):
/// an export job is the generic record of one asynchronous export request (docs/03_DOMAIN_LANGUAGE.md,
/// docs/05_MODULE_CONTRACTS.md); it is workspace-scoped and tenant-scoped and belongs only to the
/// workspace and organization it names; it carries an EXPLICIT, immutable <see cref="ExportScope"/> (the
/// "explicit host/admin export scopes" control for threat T8 in docs/07_SECURITY_THREAT_MODEL.md). Its
/// status moves through the guarded Pending -&gt; Running -&gt; Completed/Failed state machine
/// (csv/database_tables.csv: "Async exports"). The negative-transition cases assert NO mutation happens on
/// a rejected transition, and <see cref="ExportJob.ToString"/> stays log-safe (threats T7/T8): it never
/// carries the free-form failure reason. The object-level boundary checks
/// (BelongsToOrganization/Workspace, Identifies) are the fail-closed isolation guards (threats T5/T1). No
/// vertical-specific terms appear anywhere — generic Export vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class ExportJobTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _requestedBy = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _startedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _finishedAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    private static ExportJob CreatePending(ExportScope scope = ExportScope.Workspace)
        => ExportJob.Create(_organizationId, _workspaceId, _requestedBy, scope, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_starts_pending_with_no_failure_reason()
    {
        var job = CreatePending(ExportScope.UserData);

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(_organizationId, job.OrganizationId);
        Assert.Equal(_workspaceId, job.WorkspaceId);
        Assert.Equal(_requestedBy, job.RequestedByUserProfileId);
        Assert.Equal(ExportScope.UserData, job.Scope);
        // A new job is queued: pending, no failure, no terminal state.
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Null(job.FailureReason);
        Assert.False(job.IsTerminal);
        Assert.True(job.CanStart);
        Assert.False(job.CanComplete);
        Assert.True(job.CanFail);
        Assert.Equal(_createdAt, job.CreatedAt);
        Assert.Equal(_createdAt, job.UpdatedAt);
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
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var job = ExportJob.Create(_organizationId, _workspaceId, _requestedBy, ExportScope.Workspace, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, job.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), job.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => ExportJob.Create(Guid.Empty, _workspaceId, _requestedBy, ExportScope.Workspace, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => ExportJob.Create(_organizationId, Guid.Empty, _requestedBy, ExportScope.Workspace, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_requested_by_id()
        => Assert.Throws<ArgumentException>(
            () => ExportJob.Create(_organizationId, _workspaceId, Guid.Empty, ExportScope.Workspace, _createdAt));

    [Fact]
    public void Create_rejects_an_undefined_scope()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => ExportJob.Create(_organizationId, _workspaceId, _requestedBy, (ExportScope)999, _createdAt));

    // --- Lifecycle: the happy paths --------------------------------------------

    [Fact]
    public void Start_moves_pending_to_running()
    {
        var job = CreatePending();

        job.Start(_startedAt);

        Assert.Equal(ExportJobStatus.Running, job.Status);
        Assert.False(job.IsTerminal);
        Assert.False(job.CanStart);
        Assert.True(job.CanComplete);
        Assert.True(job.CanFail);
        Assert.Null(job.FailureReason);
        Assert.Equal(_startedAt, job.UpdatedAt);
    }

    [Fact]
    public void Complete_moves_running_to_completed()
    {
        var job = CreatePending();
        job.Start(_startedAt);

        job.Complete(_finishedAt);

        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.True(job.IsTerminal);
        Assert.False(job.CanStart);
        Assert.False(job.CanComplete);
        Assert.False(job.CanFail);
        Assert.Null(job.FailureReason);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void Fail_from_running_records_the_reason_and_is_terminal()
    {
        var job = CreatePending();
        job.Start(_startedAt);

        job.Fail("storage adapter unavailable", _finishedAt);

        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.True(job.IsTerminal);
        Assert.False(job.CanFail);
        Assert.Equal("storage adapter unavailable", job.FailureReason);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void Fail_directly_from_pending_is_allowed()
    {
        // A job may fail before processing ever starts (for example, rejected during validation).
        var job = CreatePending();

        job.Fail("rejected before processing", _startedAt);

        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal("rejected before processing", job.FailureReason);
    }

    [Fact]
    public void Fail_trims_the_reason()
    {
        var job = CreatePending();

        job.Fail("  bounded reason  ", _startedAt);

        Assert.Equal("bounded reason", job.FailureReason);
    }

    // --- Lifecycle: the guarded negative transitions ---------------------------

    [Fact]
    public void Start_on_a_running_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();
        job.Start(_startedAt);

        Assert.Throws<InvalidExportJobStateTransitionException>(() => job.Start(_finishedAt));

        // No mutation on a rejected transition: the status and timestamp are unchanged.
        Assert.Equal(ExportJobStatus.Running, job.Status);
        Assert.Equal(_startedAt, job.UpdatedAt);
    }

    [Fact]
    public void Complete_on_a_pending_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();

        Assert.Throws<InvalidExportJobStateTransitionException>(() => job.Complete(_finishedAt));

        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(_createdAt, job.UpdatedAt);
    }

    [Fact]
    public void Complete_on_a_completed_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();
        job.Start(_startedAt);
        job.Complete(_finishedAt);

        Assert.Throws<InvalidExportJobStateTransitionException>(
            () => job.Complete(_finishedAt.AddHours(1)));

        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void Fail_on_a_completed_job_throws_and_does_not_mutate()
    {
        // A terminal (completed) job can never be silently re-judged as failed.
        var job = CreatePending();
        job.Start(_startedAt);
        job.Complete(_finishedAt);

        Assert.Throws<InvalidExportJobStateTransitionException>(
            () => job.Fail("too late", _finishedAt.AddHours(1)));

        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Null(job.FailureReason);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void Fail_on_a_failed_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();
        job.Fail("first failure", _startedAt);

        Assert.Throws<InvalidExportJobStateTransitionException>(
            () => job.Fail("second failure", _finishedAt));

        // The first failure reason and timestamp survive the rejected re-fail.
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal("first failure", job.FailureReason);
        Assert.Equal(_startedAt, job.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\ncontrol")]
    public void Fail_rejects_an_invalid_reason_and_does_not_mutate(string reason)
    {
        var job = CreatePending();
        job.Start(_startedAt);

        Assert.Throws<ArgumentException>(() => job.Fail(reason, _finishedAt));

        // The job stays running and records no reason when the reason is invalid.
        Assert.Equal(ExportJobStatus.Running, job.Status);
        Assert.Null(job.FailureReason);
        Assert.Equal(_startedAt, job.UpdatedAt);
    }

    [Fact]
    public void Fail_rejects_an_overlong_reason()
    {
        var job = CreatePending();
        var tooLong = new string('x', ExportJob.MaxFailureReasonLength + 1);

        Assert.Throws<ArgumentException>(() => job.Fail(tooLong, _startedAt));
        Assert.Equal(ExportJobStatus.Pending, job.Status);
    }

    // --- Attempt counting + dead-letter bound (CORE-RES-002) -------------------

    [Fact]
    public void Create_starts_with_a_zero_attempt_count()
        => Assert.Equal(0, CreatePending().AttemptCount);

    [Fact]
    public void RecordAttempt_increments_the_counter_and_stamps_updated_at_without_changing_status()
    {
        var job = CreatePending();

        job.RecordAttempt(_startedAt);

        // A recorded attempt counts but does not move the lifecycle status (it is recorded after a rolled-back
        // processing attempt, leaving the job queued for the next sweep).
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.False(job.IsTerminal);
        Assert.Equal(_startedAt, job.UpdatedAt);

        job.RecordAttempt(_finishedAt);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void RecordAttempt_is_allowed_on_a_running_job()
    {
        var job = CreatePending();
        job.Start(_startedAt);

        job.RecordAttempt(_finishedAt);

        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(ExportJobStatus.Running, job.Status);
    }

    [Fact]
    public void RecordAttempt_then_dead_letter_via_fail_keeps_the_counter()
    {
        // The worker records the failed attempt then dead-letters the job via Fail once the bound is reached;
        // the attempt counter is preserved on the now-terminal job (observable to its requester).
        var job = CreatePending();
        job.RecordAttempt(_startedAt);
        job.RecordAttempt(_startedAt);
        job.RecordAttempt(_startedAt);

        job.Fail("dead-lettered after the maximum attempts", _finishedAt);

        Assert.Equal(3, job.AttemptCount);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.True(job.IsTerminal);
    }

    [Fact]
    public void RecordAttempt_on_a_completed_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();
        job.Start(_startedAt);
        job.Complete(_finishedAt);

        Assert.Throws<InvalidOperationException>(() => job.RecordAttempt(_finishedAt.AddHours(1)));

        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(_finishedAt, job.UpdatedAt);
    }

    [Fact]
    public void RecordAttempt_on_a_failed_job_throws_and_does_not_mutate()
    {
        var job = CreatePending();
        job.Fail("first failure", _startedAt);

        Assert.Throws<InvalidOperationException>(() => job.RecordAttempt(_finishedAt));

        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(_startedAt, job.UpdatedAt);
    }

    // --- Object-level boundary checks (fail-closed isolation) -------------------

    [Fact]
    public void BelongsToOrganization_matches_only_the_owning_tenant()
    {
        var job = CreatePending();

        Assert.True(job.BelongsToOrganization(_organizationId));
        Assert.False(job.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(job.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void BelongsToWorkspace_matches_only_the_owning_workspace()
    {
        var job = CreatePending();

        Assert.True(job.BelongsToWorkspace(_workspaceId));
        Assert.False(job.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(job.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_tenant_workspace_and_id_to_all_match()
    {
        var job = CreatePending();

        Assert.True(job.Identifies(_organizationId, _workspaceId, job.Id));
        // A foreign tenant, a foreign workspace, a wrong id or an empty id all fail closed.
        Assert.False(job.Identifies(_foreignOrganizationId, _workspaceId, job.Id));
        Assert.False(job.Identifies(_organizationId, _foreignWorkspaceId, job.Id));
        Assert.False(job.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(job.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Log safety (threats T7/T8) --------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_excludes_the_failure_reason()
    {
        var job = CreatePending(ExportScope.Workspace);
        job.RecordAttempt(_startedAt);
        job.Fail("an internal diagnostic that must never reach logs", _startedAt);

        var rendered = job.ToString();

        // Identifiers, scope, status and the attempt count are present; the free-form failure reason is NOT.
        Assert.Contains(job.Id.ToString(), rendered);
        Assert.Contains($"org={_organizationId}", rendered);
        Assert.Contains($"ws={_workspaceId}", rendered);
        Assert.Contains("scope=Workspace", rendered);
        Assert.Contains("status=Failed", rendered);
        Assert.Contains("attempts=1", rendered);
        Assert.DoesNotContain("an internal diagnostic that must never reach logs", rendered);
    }

    [Fact]
    public void ToString_renders_an_anonymized_requester()
    {
        // The materialization path sets requested_by null when the requesting user is deleted; the
        // log-safe rendering reflects that as "anonymized" rather than an empty id.
        var job = CreatePending();
        Assert.Contains($"requestedBy={_requestedBy}", job.ToString());
    }

    // --- Artifact coordinates (CORE-PRIV-003) ----------------------------------

    [Fact]
    public void A_new_job_has_no_artifact()
    {
        var job = CreatePending();
        Assert.False(job.HasArtifact);
        Assert.Null(job.ArtifactBucket);
        Assert.Null(job.ArtifactObjectKey);
    }

    [Fact]
    public void RecordArtifact_sets_both_coordinates_together_and_trims_them()
    {
        var job = CreatePending();
        job.RecordArtifact("  livecore-private-assets  ", $"  exports/{job.Id:N}  ", _finishedAt);

        Assert.True(job.HasArtifact);
        Assert.Equal("livecore-private-assets", job.ArtifactBucket);
        Assert.Equal($"exports/{job.Id:N}", job.ArtifactObjectKey);
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("   ", "key")]
    [InlineData("has space", "key")]
    [InlineData("bucket", "")]
    [InlineData("bucket", "has space")]
    public void RecordArtifact_rejects_a_broken_coordinate(string bucket, string objectKey)
        => Assert.Throws<ArgumentException>(() => CreatePending().RecordArtifact(bucket, objectKey, _finishedAt));

    // --- Lease (CORE-RES-003) --------------------------------------------------

    private static readonly DateTimeOffset _leaseUntil = _startedAt + TimeSpan.FromMinutes(5);

    [Fact]
    public void Create_starts_unleased()
    {
        var job = CreatePending();

        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeasedUntil);
        Assert.True(job.CanAcquireLease(_startedAt));
    }

    [Fact]
    public void AcquireLease_records_the_owner_and_expiry_and_stamps_updated_at()
    {
        var job = CreatePending();
        var owner = Guid.CreateVersion7();

        job.AcquireLease(owner, _leaseUntil, _startedAt);

        Assert.Equal(owner, job.LeaseOwner);
        Assert.Equal(_leaseUntil, job.LeasedUntil);
        Assert.Equal(_startedAt, job.UpdatedAt);
        // The lifecycle status is untouched — a claim only records lease metadata.
        Assert.Equal(ExportJobStatus.Pending, job.Status);
    }

    [Fact]
    public void CanAcquireLease_is_false_while_a_live_lease_is_held_and_true_once_it_expires()
    {
        var job = CreatePending();
        job.AcquireLease(Guid.CreateVersion7(), _leaseUntil, _startedAt);

        // While the lease is live the job cannot be re-claimed (another replica owns it)...
        Assert.False(job.CanAcquireLease(_startedAt + TimeSpan.FromMinutes(1)));
        // ...but once the lease has lapsed it is reclaimable (a crashed replica).
        Assert.True(job.CanAcquireLease(_leaseUntil));
        Assert.True(job.CanAcquireLease(_leaseUntil + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AcquireLease_rejects_stealing_a_live_lease()
    {
        var job = CreatePending();
        job.AcquireLease(Guid.CreateVersion7(), _leaseUntil, _startedAt);

        // A second acquire before the lease expires is an invalid operation, not a silent steal.
        Assert.Throws<InvalidOperationException>(
            () => job.AcquireLease(Guid.CreateVersion7(), _leaseUntil + TimeSpan.FromMinutes(5), _startedAt + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AcquireLease_is_rejected_on_a_terminal_job()
    {
        var job = CreatePending();
        job.Start(_startedAt);
        job.Complete(_finishedAt);

        Assert.False(job.CanAcquireLease(_finishedAt));
        Assert.Throws<InvalidOperationException>(
            () => job.AcquireLease(Guid.CreateVersion7(), _finishedAt + TimeSpan.FromMinutes(5), _finishedAt));
    }

    [Fact]
    public void AcquireLease_rejects_an_empty_owner_or_a_non_future_expiry()
    {
        Assert.Throws<ArgumentException>(
            () => CreatePending().AcquireLease(Guid.Empty, _leaseUntil, _startedAt));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreatePending().AcquireLease(Guid.CreateVersion7(), _startedAt, _startedAt));
    }

    [Fact]
    public void ReleaseLease_clears_the_lease_so_the_job_is_reclaimable()
    {
        var job = CreatePending();
        job.AcquireLease(Guid.CreateVersion7(), _leaseUntil, _startedAt);

        job.ReleaseLease(_finishedAt);

        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeasedUntil);
        Assert.Equal(_finishedAt, job.UpdatedAt);
        Assert.True(job.CanAcquireLease(_finishedAt));
    }
}
