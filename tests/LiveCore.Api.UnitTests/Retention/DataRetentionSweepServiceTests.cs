using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Exports;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Recaps;
using LiveCore.Api.Retention;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Retention;

/// <summary>
/// Integration-style tests for the <see cref="DataRetentionSweepService"/> (CORE-PRIV-003/CORE-PRIV-006, the
/// "Privacy and Data Lifecycle" epic, GDPR Art.5(1)(e) "storage limitation"). They run against an in-memory
/// SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, the
/// SQL translation, the FK cascades, the audit hash chain and the transactional unit of work are all exercised on
/// every run without a database server — exactly like the asset/scene/entity deletion service tests.
///
/// Coverage (the story's required tests):
/// <list type="bullet">
///   <item>A record PAST its retention window is purged — for every family, including the export S3 object
///   (recorded by a fake <see cref="IAssetStorage"/>) and the session's cascade-removed events and recap.</item>
///   <item>A record WITHIN its window is RETAINED, and a NON-terminal record (a live session, a still-redeemable
///   invitation) is never purged even when it is old (fail-closed correctness).</item>
///   <item>DISABLING a window stops purging that family entirely.</item>
///   <item>The sweep is IDEMPOTENT (a second sweep is a no-op and never double-audits) and CONCURRENCY-SAFE (a
///   second overlapping sweep never double-deletes or errors).</item>
///   <item>Every tenant-scoped purge is AUDITED BY ID (a RecordRetentionPurged fact, system actor, PII-free), and
///   the export is KEPT when storage is unconfigured (fail-closed, no orphaned object).</item>
///   <item>The idempotency-key family (CORE-PRIV-006) is a COUNT-ONLY bulk purge by age: a past-window key is
///   removed and a within-window one retained, disabling stops it, overlapping sweeps never double-delete, and
///   no per-record audit fact is written (deletion is never by key value).</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class DataRetentionSweepServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";

    private static readonly DateTimeOffset _now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _window = TimeSpan.FromDays(30);
    private static readonly DateTimeOffset _pastWindow = _now - TimeSpan.FromDays(40);
    private static readonly DateTimeOffset _withinWindow = _now - TimeSpan.FromDays(1);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public DataRetentionSweepServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // ------------------------------------------------------------------------------------------------ Sessions

    [Fact]
    public async Task Past_window_terminal_session_is_purged_with_its_events_and_recap()
    {
        var (org, ws, _) = await SeedTenantAsync();
        var oldEnded = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Ended);
        await SeedSessionEventAsync(org, ws, oldEnded, _pastWindow);
        await SeedSystemRecapAsync(org, ws, oldEnded, _pastWindow);

        var recent = await SeedSessionAsync(org, ws, _withinWindow, terminal: SessionTerminalState.Ended);
        var live = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Live);

        var result = await RunSweepAsync(Options(sessions: true));

        Assert.Equal(1, result.Sessions.Purged);
        await using var verify = CreateContext();
        // The past-window terminal session and its cascade-removed event + recap are gone.
        Assert.False(await verify.Sessions.AnyAsync(s => s.Id == oldEnded));
        Assert.False(await verify.SessionEvents.AnyAsync(e => e.SessionId == oldEnded));
        Assert.False(await verify.Recaps.AnyAsync(r => r.SessionId == oldEnded));
        // The within-window terminal session and the old-but-still-live session both survive (fail-closed).
        Assert.True(await verify.Sessions.AnyAsync(s => s.Id == recent));
        Assert.True(await verify.Sessions.AnyAsync(s => s.Id == live));
    }

    [Fact]
    public async Task Cancelled_session_past_window_is_purged()
    {
        var (org, ws, _) = await SeedTenantAsync();
        var cancelled = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Cancelled);

        var result = await RunSweepAsync(Options(sessions: true));

        Assert.Equal(1, result.Sessions.Purged);
        await using var verify = CreateContext();
        Assert.False(await verify.Sessions.AnyAsync(s => s.Id == cancelled));
    }

    [Fact]
    public async Task Disabling_the_session_window_stops_purging()
    {
        var (org, ws, _) = await SeedTenantAsync();
        var oldEnded = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Ended);

        // Sessions disabled (the default): a past-window terminal session is NOT purged.
        var result = await RunSweepAsync(Options(sessions: false));

        Assert.Equal(0, result.Sessions.Examined);
        Assert.Equal(0, result.Sessions.Purged);
        await using var verify = CreateContext();
        Assert.True(await verify.Sessions.AnyAsync(s => s.Id == oldEnded));
    }

    // -------------------------------------------------------------------------------------------------- Recaps

    [Fact]
    public async Task Past_window_recap_is_purged_and_within_window_recap_is_retained()
    {
        var (org, ws, _) = await SeedTenantAsync();
        // A non-terminal session anchors both recaps; only the recap window is enabled, so the session survives.
        var session = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Live);
        var oldRecap = await SeedHostRecapAsync(org, ws, session, _pastWindow);
        var recentRecap = await SeedHostRecapAsync(org, ws, session, _withinWindow);

        var result = await RunSweepAsync(Options(recaps: true));

        Assert.Equal(1, result.Recaps.Purged);
        await using var verify = CreateContext();
        Assert.False(await verify.Recaps.AnyAsync(r => r.Id == oldRecap));
        Assert.True(await verify.Recaps.AnyAsync(r => r.Id == recentRecap));
        // The anchoring session is untouched (only the recap window ran).
        Assert.True(await verify.Sessions.AnyAsync(s => s.Id == session));
    }

    // ------------------------------------------------------------------------------------------------- Exports

    [Fact]
    public async Task Past_window_completed_export_is_purged_including_its_s3_object()
    {
        var (org, ws, user) = await SeedTenantAsync();
        var (export, bucket, key) = await SeedCompletedExportAsync(org, ws, user, _pastWindow, withArtifact: true);
        var recent = (await SeedCompletedExportAsync(org, ws, user, _withinWindow, withArtifact: true)).ExportId;

        var storage = new RecordingRetentionStorage();
        var result = await RunSweepAsync(Options(exports: true), storage);

        Assert.Equal(1, result.Exports.Purged);
        // The export S3 object was deleted by coordinate (the story's "including the export S3 object").
        Assert.Equal(new[] { (bucket, key) }, storage.DeletedObjects);

        await using var verify = CreateContext();
        Assert.False(await verify.ExportJobs.AnyAsync(j => j.Id == export));
        Assert.True(await verify.ExportJobs.AnyAsync(j => j.Id == recent));
    }

    [Fact]
    public async Task Export_is_kept_when_storage_is_unconfigured_so_its_object_is_never_orphaned()
    {
        var (org, ws, user) = await SeedTenantAsync();
        var (export, _, _) = await SeedCompletedExportAsync(org, ws, user, _pastWindow, withArtifact: true);

        // Fail-closed storage: the object cannot be deleted, so the export row is KEPT (never row-gone-object-remains).
        var result = await RunSweepAsync(Options(exports: true), new UnconfiguredAssetStorage());

        Assert.Equal(0, result.Exports.Purged);
        Assert.Equal(1, result.Exports.Failed);
        await using var verify = CreateContext();
        Assert.True(await verify.ExportJobs.AnyAsync(j => j.Id == export));
    }

    [Fact]
    public async Task Completed_export_with_no_artifact_is_purged_without_touching_storage()
    {
        var (org, ws, user) = await SeedTenantAsync();
        var (export, _, _) = await SeedCompletedExportAsync(org, ws, user, _pastWindow, withArtifact: false);

        var storage = new RecordingRetentionStorage();
        var result = await RunSweepAsync(Options(exports: true), storage);

        Assert.Equal(1, result.Exports.Purged);
        Assert.Empty(storage.DeletedObjects);
        await using var verify = CreateContext();
        Assert.False(await verify.ExportJobs.AnyAsync(j => j.Id == export));
    }

    // --------------------------------------------------------------------------------------------- Invitations

    [Theory]
    [InlineData(InvitationTerminalState.Accepted)]
    [InlineData(InvitationTerminalState.Revoked)]
    [InlineData(InvitationTerminalState.Expired)]
    public async Task Past_window_terminal_invitation_is_purged(InvitationTerminalState state)
    {
        var (org, ws, _) = await SeedTenantAsync();
        var terminal = await SeedInvitationAsync(org, ws, _pastWindow, state);

        var result = await RunSweepAsync(Options(invitations: true));

        Assert.Equal(1, result.Invitations.Purged);
        await using var verify = CreateContext();
        Assert.False(await verify.WorkspaceInvitations.AnyAsync(i => i.Id == terminal));
    }

    [Fact]
    public async Task Still_redeemable_pending_invitation_is_never_purged_even_when_old()
    {
        var (org, ws, _) = await SeedTenantAsync();
        // Created long ago but with a long validity, so it is still redeemable at "now" — NOT terminal.
        var pending = await SeedInvitationAsync(org, ws, _pastWindow, InvitationTerminalState.None);
        var within = await SeedInvitationAsync(org, ws, _withinWindow, InvitationTerminalState.Revoked);

        var result = await RunSweepAsync(Options(invitations: true));

        Assert.Equal(0, result.Invitations.Purged);
        await using var verify = CreateContext();
        // The still-redeemable old invitation AND the within-window terminal one both survive.
        Assert.True(await verify.WorkspaceInvitations.AnyAsync(i => i.Id == pending));
        Assert.True(await verify.WorkspaceInvitations.AnyAsync(i => i.Id == within));
    }

    // ----------------------------------------------------------------------------------------- Idempotency keys

    [Fact]
    public async Task Past_window_idempotency_key_is_purged_and_within_window_key_is_retained()
    {
        var expired = await SeedIdempotencyKeyAsync(_pastWindow);
        var recent = await SeedIdempotencyKeyAsync(_withinWindow);

        var result = await RunSweepAsync(Options(idempotencyKeys: true));

        Assert.Equal(1, result.IdempotencyKeys.Purged);
        await using var verify = CreateContext();
        // The past-window key is gone; the within-window key is retained (deletion is by age alone).
        Assert.False(await verify.IdempotencyKeys.AnyAsync(k => k.Id == expired));
        Assert.True(await verify.IdempotencyKeys.AnyAsync(k => k.Id == recent));
        // COUNT ONLY: the idempotency-key purge writes no per-record audit fact (never by key value).
        Assert.Equal(0, await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.RecordRetentionPurged));
    }

    [Fact]
    public async Task Disabling_the_idempotency_key_window_stops_purging()
    {
        var expired = await SeedIdempotencyKeyAsync(_pastWindow);

        // Idempotency keys disabled: a past-window key is NOT purged and nothing is examined.
        var result = await RunSweepAsync(Options(idempotencyKeys: false));

        Assert.Equal(0, result.IdempotencyKeys.Examined);
        Assert.Equal(0, result.IdempotencyKeys.Purged);
        await using var verify = CreateContext();
        Assert.True(await verify.IdempotencyKeys.AnyAsync(k => k.Id == expired));
    }

    [Fact]
    public async Task Overlapping_idempotency_key_sweep_does_not_double_delete_or_error()
    {
        var expired = await SeedIdempotencyKeyAsync(_pastWindow);

        // A second worker arriving just after the first: the bulk delete by age is idempotent, so the second
        // sweep matches nothing already gone — no double-delete, no error.
        await using var contextA = CreateContext();
        await using var contextB = CreateContext();
        var resultA = await CreateService(contextA, Options(idempotencyKeys: true), new RecordingRetentionStorage())
            .SweepAsync(CancellationToken.None);
        var resultB = await CreateService(contextB, Options(idempotencyKeys: true), new RecordingRetentionStorage())
            .SweepAsync(CancellationToken.None);

        Assert.Equal(1, resultA.IdempotencyKeys.Purged);
        Assert.Equal(0, resultB.IdempotencyKeys.Purged);
        Assert.Equal(0, resultB.IdempotencyKeys.Failed);
        await using var verify = CreateContext();
        Assert.False(await verify.IdempotencyKeys.AnyAsync(k => k.Id == expired));
    }

    // -------------------------------------------------------------------------------------------------- Audit

    [Fact]
    public async Task Each_purge_appends_one_retention_purge_audit_fact_by_id_with_no_actor()
    {
        var (org, ws, _) = await SeedTenantAsync();
        var session = await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Ended);

        await RunSweepAsync(Options(sessions: true));

        await using var verify = CreateContext();
        var entry = Assert.Single(await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.RecordRetentionPurged)
            .ToListAsync());
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(nameof(Session), entry.ResourceType);
        Assert.Equal(session, entry.ResourceId);
        // A SYSTEM purge: no user actor and no before/after state pair.
        Assert.Null(entry.ActorUserProfileId);
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
        // The fact is sealed into the tenant's hash chain (it survives the purge it records).
        Assert.NotNull(entry.EntryHash);
    }

    // ------------------------------------------------------------------------------ Idempotency / concurrency

    [Fact]
    public async Task Second_sweep_is_a_no_op_and_does_not_double_audit()
    {
        var (org, ws, _) = await SeedTenantAsync();
        await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Ended);

        var first = await RunSweepAsync(Options(sessions: true));
        var second = await RunSweepAsync(Options(sessions: true));

        Assert.Equal(1, first.Sessions.Purged);
        Assert.Equal(0, second.Sessions.Examined);
        Assert.Equal(0, second.Sessions.Purged);

        await using var verify = CreateContext();
        // Exactly ONE audit fact — the re-run never re-purged or re-audited.
        Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.RecordRetentionPurged));
    }

    [Fact]
    public async Task Overlapping_sweep_does_not_double_purge_or_error()
    {
        var (org, ws, _) = await SeedTenantAsync();
        await SeedSessionAsync(org, ws, _pastWindow, terminal: SessionTerminalState.Ended);

        // Two services over the same database, simulating a second worker arriving just after the first. The
        // second re-loads the row inside its transaction, finds it already gone and skips it — no double-delete,
        // no double-audit and no error.
        await using var contextA = CreateContext();
        await using var contextB = CreateContext();
        var resultA = await CreateService(contextA, Options(sessions: true), new RecordingRetentionStorage()).SweepAsync(CancellationToken.None);
        var resultB = await CreateService(contextB, Options(sessions: true), new RecordingRetentionStorage()).SweepAsync(CancellationToken.None);

        Assert.Equal(1, resultA.Sessions.Purged);
        Assert.Equal(0, resultB.Sessions.Purged);
        await using var verify = CreateContext();
        Assert.Equal(0, await verify.Sessions.CountAsync());
        Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.RecordRetentionPurged));
    }

    // -------------------------------------------------------------------------------------- service + options

    private async Task<DataRetentionSweepResult> RunSweepAsync(DataRetentionOptions options, IAssetStorage? storage = null)
    {
        await using var context = CreateContext();
        return await CreateService(context, options, storage ?? new RecordingRetentionStorage())
            .SweepAsync(CancellationToken.None);
    }

    private static DataRetentionSweepService CreateService(LiveCoreDbContext context, DataRetentionOptions options, IAssetStorage storage)
        => new(
            context,
            new TransactionalUnitOfWork(context),
            new SessionRepository(context),
            new RecapRepository(context),
            new ExportJobRepository(context),
            new WorkspaceInvitationRepository(context),
            new IdempotencyKeyStore(context),
            new AuditLogRepository(context),
            storage,
            options,
            new FixedTimeProvider(_now),
            NullLogger<DataRetentionSweepService>.Instance);

    // Builds options with exactly the named families enabled (all on the shared test window) and the rest off,
    // so each test exercises one family in isolation regardless of the production defaults.
    private static DataRetentionOptions Options(
        bool sessions = false,
        bool recaps = false,
        bool exports = false,
        bool invitations = false,
        bool idempotencyKeys = false)
        => new(
            DataRetentionOptions.DefaultSweepInterval,
            DataRetentionOptions.DefaultBatchSize,
            new RetentionWindowOptions(sessions, _window),
            new RetentionWindowOptions(recaps, _window),
            new RetentionWindowOptions(exports, _window),
            new RetentionWindowOptions(invitations, _window),
            new RetentionWindowOptions(idempotencyKeys, _window));

    // ------------------------------------------------------------------------------------------------ seeding

    private async Task<(Guid Organization, Guid Workspace, Guid User)> SeedTenantAsync()
    {
        await using var context = CreateContext();
        var user = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, _issuer, $"subject-{Guid.CreateVersion7():N}"),
            _pastWindow);
        context.UserProfiles.Add(user);
        var org = Organization.Create($"org-{Guid.CreateVersion7():N}", "Organization", _pastWindow);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();
        var workspace = Workspace.Create(org.Id, "workspace", "Workspace", _pastWindow);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        return (org.Id, workspace.Id, user.Id);
    }

    private async Task<Guid> SeedSessionAsync(Guid org, Guid ws, DateTimeOffset createdAt, SessionTerminalState terminal)
    {
        await using var context = CreateContext();
        var session = Session.Create(org, ws, "Session", createdAt);
        switch (terminal)
        {
            case SessionTerminalState.Ended:
                session.Start(createdAt);
                session.End(createdAt);
                break;
            case SessionTerminalState.Cancelled:
                session.Cancel(createdAt);
                break;
            case SessionTerminalState.Live:
                session.Start(createdAt);
                break;
        }

        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task SeedSessionEventAsync(Guid org, Guid ws, Guid sessionId, DateTimeOffset createdAt)
    {
        await using var context = CreateContext();
        var sessionEvent = SessionEvent.Create(org, ws, sessionId, "ContentRevealed", null, null, "{}", 1, createdAt);
        sessionEvent.AssignSequence(1);
        context.SessionEvents.Add(sessionEvent);
        await context.SaveChangesAsync();
    }

    private async Task SeedSystemRecapAsync(Guid org, Guid ws, Guid sessionId, DateTimeOffset generatedAt)
    {
        await using var context = CreateContext();
        context.Recaps.Add(Recap.GenerateBySystem(org, ws, sessionId, "A generic session summary.", generatedAt));
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedHostRecapAsync(Guid org, Guid ws, Guid sessionId, DateTimeOffset generatedAt)
    {
        await using var context = CreateContext();
        // A host recap needs a producing user; reuse a fresh user per recap to avoid the one-system-recap rule.
        var user = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, _issuer, $"recap-author-{Guid.CreateVersion7():N}"),
            generatedAt);
        context.UserProfiles.Add(user);
        await context.SaveChangesAsync();
        var recap = Recap.GenerateByHost(org, ws, sessionId, user.Id, "A generic session summary.", generatedAt);
        context.Recaps.Add(recap);
        await context.SaveChangesAsync();
        return recap.Id;
    }

    private async Task<(Guid ExportId, string Bucket, string Key)> SeedCompletedExportAsync(
        Guid org, Guid ws, Guid user, DateTimeOffset createdAt, bool withArtifact)
    {
        await using var context = CreateContext();
        var export = ExportJob.Create(org, ws, user, ExportScope.Workspace, createdAt);
        export.Start(createdAt);
        export.Complete(createdAt);

        var bucket = "livecore-private-assets";
        var key = $"exports/{export.Id:N}";
        if (withArtifact)
        {
            export.RecordArtifact(bucket, key, createdAt);
        }

        context.ExportJobs.Add(export);
        await context.SaveChangesAsync();
        return (export.Id, bucket, key);
    }

    private async Task<Guid> SeedInvitationAsync(Guid org, Guid ws, DateTimeOffset createdAt, InvitationTerminalState state)
    {
        await using var context = CreateContext();
        // A long validity for the still-redeemable case, a short (already-expired) one otherwise.
        var validity = state == InvitationTerminalState.None ? TimeSpan.FromDays(120) : TimeSpan.FromDays(7);
        var invitation = WorkspaceInvitation.Create(
            org, ws, "invitee@example.test", MembershipRole.Participant, createdAt, out _, validity);
        switch (state)
        {
            case InvitationTerminalState.Accepted:
                invitation.Redeem(createdAt + TimeSpan.FromDays(1));
                break;
            case InvitationTerminalState.Revoked:
                invitation.Revoke(createdAt + TimeSpan.FromDays(1));
                break;
                // Expired and None both stay Pending; Expired's short validity has lapsed by "now", None's has not.
        }

        context.WorkspaceInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return invitation.Id;
    }

    private async Task<Guid> SeedIdempotencyKeyAsync(DateTimeOffset createdAt)
    {
        await using var context = CreateContext();
        // Generic, neutral scope/key (AGENTS.md) — unique per row so the unique (scope, key) index is satisfied.
        var key = IdempotencyKey.Create(
            $"reveal:{Guid.CreateVersion7():N}", $"key-{Guid.CreateVersion7():N}", createdAt);
        context.IdempotencyKeys.Add(key);
        await context.SaveChangesAsync();
        return key.Id;
    }

    public enum SessionTerminalState
    {
        Ended,
        Cancelled,
        Live,
    }

    public enum InvitationTerminalState
    {
        None,
        Accepted,
        Revoked,
        Expired,
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// A recording fake <see cref="IAssetStorage"/> that captures the coordinate-addressed deletes the export
    /// retention purge issues (the story's "including the export S3 object"). The signing and asset-delete
    /// operations are never exercised by the sweep, so they throw.
    /// </summary>
    private sealed class RecordingRetentionStorage : IAssetStorage
    {
        public List<(string Bucket, string Key)> DeletedObjects { get; } = [];

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        {
            DeletedObjects.Add((bucket, objectKey));
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
