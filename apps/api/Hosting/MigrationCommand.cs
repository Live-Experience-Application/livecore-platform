// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;

namespace LiveCore.Api.Hosting;

/// <summary>
/// The one-shot migration runner command (CORE-OPS-012). When the API assembly is launched with the
/// <see cref="CommandName"/> verb (the <c>apps/api/Migrations.Dockerfile</c> image's entrypoint, run as a
/// Kubernetes pre-install/pre-upgrade Job or a Compose <c>migrate</c> service), this process is the migration
/// RUNNER, not the API host: it applies every pending EF Core migration through
/// <see cref="LiveCoreMigrationRunner"/> — under a session-level PostgreSQL advisory lock on a fixed app key, so
/// two runner invocations racing serialise rather than corrupting the schema — and then exits. It never builds
/// the web host, starts Kestrel or serves traffic, so the API host's "never migrate on startup" guarantee
/// (CORE-OPS-001) is untouched: the migrate verb is a distinct invocation, not a startup hook.
///
/// <para>
/// The exit code IS the success signal a deploy gates on: <c>0</c> when the migrations applied (or the schema
/// was already current — idempotent), non-zero when no database is configured or the apply failed. No
/// credential lives in source: the connection string is read from the same <c>ConnectionStrings:Database</c>
/// configuration the API runtime uses, or from an explicit <c>--connection</c> argument (threat T7).
/// </para>
/// </summary>
internal static class MigrationCommand
{
    /// <summary>
    /// The command verb that selects the migration runner instead of the API host. Matched as the first
    /// argument in <c>Program.cs</c> before the web host is built.
    /// </summary>
    public const string CommandName = "migrate";

    /// <summary>
    /// Optional argument that supplies the target connection string explicitly, overriding the environment.
    /// Mirrors the override the EF migrations bundle this runner replaces accepted (documented in docs/13).
    /// </summary>
    private const string _connectionArgument = "--connection";

    /// <summary>
    /// Runs the migration apply and returns the process exit code (<c>0</c> on success/idempotent no-op,
    /// non-zero on a missing connection string or a failed apply). Blocks until the advisory lock is acquired
    /// and the apply completes, so a queued runner waits here rather than the caller racing ahead.
    /// </summary>
    /// <param name="args">The process arguments, including the <see cref="CommandName"/> verb.</param>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var connectionString = ResolveConnectionString(args);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                $"No database connection string configured: set {LiveCoreDbContextFactory.ConnectionStringEnvironmentVariable} " +
                $"or pass {_connectionArgument} \"<value>\".");
            return 1;
        }

        try
        {
            // A one-shot console command: block on the async apply and surface its outcome as the exit code.
            LiveCoreMigrationRunner.RunAsync(connectionString).GetAwaiter().GetResult();
            Console.WriteLine("Database migrations applied (or already up to date).");
            return 0;
        }
        catch (Exception exception)
        {
            // Report the failure (without the connection string, which carries the credential) and fail the
            // step, so a deploy that gates on this runner aborts rather than rolling the API onto a broken schema.
            Console.Error.WriteLine($"Database migration failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Resolves the target connection string: an explicit <c>--connection &lt;value&gt;</c> argument wins,
    /// otherwise the <c>ConnectionStrings:Database</c> environment variable. Returns <see langword="null"/> when
    /// neither is set, so the caller fails closed with a clear message rather than connecting to a placeholder.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    internal static string? ResolveConnectionString(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], _connectionArgument, StringComparison.Ordinal))
            {
                var value = args[index + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        var configured = Environment.GetEnvironmentVariable(
            LiveCoreDbContextFactory.ConnectionStringEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }
}
