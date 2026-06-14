namespace LiveCore.Worker;

/// <summary>
/// Entry point of the background worker host. Declared as an explicit, namespaced type (rather than
/// top-level statements) so the worker assembly defines no global <c>Program</c> type that would collide
/// with the referenced API assembly's <c>Program</c> under the repository-wide warnings-as-errors policy.
/// </summary>
internal static class WorkerEntryPoint
{
    private static void Main(string[] args)
    {
        var app = WorkerHostFactory.Create(args);
        app.Run();
    }
}
