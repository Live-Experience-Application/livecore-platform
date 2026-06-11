namespace LiveCore.Worker;

/// <summary>
/// Builds the background worker host.
/// No background jobs are registered yet; job registrations arrive with
/// their own backlog stories.
/// </summary>
public static class WorkerHostFactory
{
    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Structured logging baseline (CORE-FND-004): same shape as the API
        // host - one JSON object per log entry on stdout, UTC timestamps,
        // scopes included. Uses the JSON console formatter built into
        // Microsoft.Extensions.Logging; no external logging dependency.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });

        return builder;
    }
}
