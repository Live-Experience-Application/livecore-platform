namespace LiveCore.Worker;

/// <summary>
/// Builds the background worker host.
/// The repository skeleton (CORE-FND-001) registers no background jobs;
/// job registrations arrive with their own backlog stories.
/// </summary>
public static class WorkerHostFactory
{
    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        return builder;
    }
}
