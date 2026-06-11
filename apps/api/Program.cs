using LiveCore.Api;

var builder = WebApplication.CreateBuilder(args);

// Structured logging baseline (CORE-FND-004): one JSON object per log entry
// on stdout, UTC timestamps, scopes included. Uses the JSON console formatter
// built into Microsoft.Extensions.Logging; no external logging dependency.
// Log identifiers and metadata, never sensitive content (threat T7 in
// docs/07_SECURITY_THREAT_MODEL.md).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

// Do not advertise the server technology in response headers; the
// unauthenticated health endpoints must not aid fingerprinting
// (docs/07_SECURITY_THREAT_MODEL.md).
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Health endpoints only (CORE-FND-004). Domain modules and their endpoints
// arrive with their own backlog stories.
app.MapLiveCoreHealthEndpoints();

app.Run();

/// <summary>
/// Marker for in-memory smoke tests (WebApplicationFactory).
/// </summary>
public partial class Program;
