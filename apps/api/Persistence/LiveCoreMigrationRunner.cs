// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Applies the checked-in EF Core migrations to the target PostgreSQL database under a SESSION-LEVEL
/// advisory lock on a fixed application key (CORE-OPS-012). This is the lock the run-to-completion
/// migration step (<c>apps/api/Migrations.Dockerfile</c>, invoked as the <c>migrate</c> command —
/// <see cref="LiveCore.Api.Hosting.MigrationCommand"/>) wraps the apply in.
///
/// <para>
/// The migrate-before-API gate (CORE-OPS-001) stops API <em>replicas</em> from racing to migrate, but
/// nothing stops two migration-RUNNER invocations racing: a retried Helm pre-upgrade hook, an overlapping
/// redeploy, or two operator runs. Two concurrent runners could otherwise both attempt the same
/// <c>Up()</c> — and the retrying execution strategy (<see cref="LiveCoreNpgsqlOptions"/>) could re-issue a
/// half-applied migration — corrupting the schema or the <c>__EFMigrationsHistory</c> table. Serialising the
/// runners on one advisory lock makes the step safe under those races: exactly one runner applies, and any
/// other blocks until it finishes and then finds the schema current and applies nothing (idempotent).
/// </para>
///
/// <para>
/// The lock is taken on its OWN dedicated connection held open for the whole apply. PostgreSQL session
/// advisory locks live exactly as long as the holding session: closing the connection — here on completion,
/// or when the process exits abruptly — releases the lock, so a crashed runner never wedges it. The apply runs
/// on a separate context connection; the lock is a pure mutex around it, independent of (and additional to)
/// EF Core's own per-apply migration lock.
/// </para>
///
/// <para>
/// It holds no secret: the connection string is supplied by the caller from configuration only (threat T7 in
/// <c>docs/07_SECURITY_THREAT_MODEL.md</c>), exactly as the design-time/migrations factory
/// (<see cref="LiveCoreDbContextFactory"/>) resolves it. The lock key and the vocabulary are product-neutral
/// (AGENTS.md).
/// </para>
/// </summary>
internal static class LiveCoreMigrationRunner
{
    /// <summary>
    /// The fixed application advisory-lock key the migration step serialises on. It is the ASCII bytes of
    /// <c>"LCMIGRAT"</c> packed into a 64-bit integer — a stable, deliberately-unusual constant so it cannot
    /// collide with an unrelated <c>pg_advisory_lock</c> a deployment might take, and it stays positive in
    /// <see cref="long"/> (the high byte <c>0x4C</c> is below <c>0x80</c>). It is the SAME key for every runner
    /// against a given database, which is what makes a second runner block on the first.
    /// </summary>
    internal const long AdvisoryLockKey = 0x4C43_4D49_4752_4154L;

    /// <summary>
    /// Acquires the fixed-key session advisory lock, applies every pending migration, then releases the lock.
    /// A concurrent runner cannot reach the apply until this one releases; it then finds the schema current and
    /// applies nothing. Completing without throwing is the success signal (the <c>migrate</c> command maps it to
    /// process exit code 0).
    /// </summary>
    /// <param name="connectionString">The target PostgreSQL connection string (supplied from configuration).</param>
    /// <param name="cancellationToken">Cancels waiting for the lock and the apply.</param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    public static async Task RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // A dedicated connection holds the advisory lock for the whole apply. The lock is SESSION-scoped, so it
        // lives exactly as long as this connection stays open; disposing it (here, or on an abrupt process exit)
        // releases the lock, so a crashed runner never leaves the lock held.
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await AcquireAdvisoryLockAsync(lockConnection, cancellationToken).ConfigureAwait(false);
        try
        {
            // Build the migration context exactly as the design-time/migrations factory does
            // (LiveCoreNpgsqlOptions.Configure: the same retrying execution strategy and command timeout), then
            // apply every pending migration. Holding the lock makes the retrying strategy safe here too: only one
            // runner is ever inside this apply, so a retry can never collide with another runner's half-applied
            // migration.
            var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
                .UseNpgsql(connectionString, LiveCoreNpgsqlOptions.Configure)
                .Options;
            await using var context = new LiveCoreDbContext(options);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(lockConnection).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocks until the fixed-key session advisory lock is held by this connection. <c>pg_advisory_lock</c>
    /// waits for the lock rather than failing, so a second runner queues behind the first.
    /// </summary>
    private static async Task AcquireAdvisoryLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(@key);";
        command.Parameters.AddWithValue("key", AdvisoryLockKey);

        // No command timeout: pg_advisory_lock BLOCKS until the lock is free, and a queued runner must be able to
        // wait out the first runner's WHOLE apply. The default per-command timeout would otherwise surface a long
        // wait as a spurious error instead of the intended block. 0 means wait indefinitely; cancellation (a
        // terminated deploy) still aborts the wait through the token.
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the fixed-key session advisory lock so the next queued runner proceeds immediately. Disposing the
    /// connection would release it anyway (session-scoped); this just does it promptly and explicitly.
    /// </summary>
    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection)
    {
        // Released with no cancellation token so the unlock still issues even when the apply was cancelled.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@key);";
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
