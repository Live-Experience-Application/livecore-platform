var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Intentionally no endpoints yet. The repository skeleton (CORE-FND-001) ships
// no product features; health endpoints arrive with CORE-FND-004 and domain
// modules with their own backlog stories.

app.Run();

/// <summary>
/// Marker for in-memory smoke tests (WebApplicationFactory).
/// </summary>
public partial class Program;
