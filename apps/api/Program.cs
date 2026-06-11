using LiveCore.Api;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;

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

// Persistence (CORE-ID-002): PostgreSQL via EF Core per docs/02_ARCHITECTURE.md
// and docs/10_DATABASE_SCHEMA.md. The connection string comes only from
// configuration (ConnectionStrings:Database, e.g. the environment variable
// ConnectionStrings__Database); no credentials live in this repository.
// Without a configured connection string the host runs without persistence
// (no database-backed feature exists yet) and the database readiness check
// is not registered, so local runs and tests need no database server.
var databaseConnectionString = builder.Configuration.GetConnectionString("Database");
if (!string.IsNullOrWhiteSpace(databaseConnectionString))
{
    builder.Services.AddDbContext<LiveCoreDbContext>(options => options.UseNpgsql(databaseConnectionString));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IUserProfileRepository, UserProfileRepository>();
    builder.Services.AddScoped<UserProfileReferenceService>();
    builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
    builder.Services.AddScoped<IOrganizationMemberRepository, OrganizationMemberRepository>();

    // Workspace persistence (CORE-WS-001): the Workspaces module owns the
    // tenant-scoped workspaces table (docs/05_MODULE_CONTRACTS.md). Registered
    // here, inside the persistence conditional, exactly like the organization
    // repositories above; the repository's lookups are tenant-scoped by
    // organization id (threat T5). HTTP endpoints are a later story (CORE-WS-003).
    builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();

    // Workspace membership persistence (CORE-WS-002): the Workspaces module
    // owns the workspace-scoped, tenant-scoped workspace_members table and the
    // generic workspace-level roles (docs/05_MODULE_CONTRACTS.md). Registered
    // here, inside the persistence conditional, exactly like the workspace and
    // organization repositories above; the repository's lookups are scoped by
    // organization id and workspace id (the organization boundary is checked
    // before the workspace boundary; threat T5). HTTP endpoints and per-action
    // authorization policies are later stories (CORE-WS-003, CORE-WS-005).
    builder.Services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();

    // Tenant context resolver (CORE-ID-005): turns an authenticated principal
    // plus a target organization into a trusted TenantContext or a fail-closed
    // denial. Registered here because it depends on the organization, user
    // profile and membership repositories above, which exist only when a
    // database connection string is configured (docs/02_ARCHITECTURE.md request
    // flow; docs/05_MODULE_CONTRACTS.md: Organizations provides organization
    // context and tenant isolation checks; threat T5).
    builder.Services.AddScoped<TenantContextResolver>();

    // Gate readiness on database connectivity. The health response stays
    // status-only (see HealthEndpoints), so a failing check never leaks
    // connection details to the unauthenticated readiness endpoint.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<LiveCoreDbContext>("database", tags: [HealthEndpoints.ReadinessTag]);
}

var app = builder.Build();

if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
    app.Logger.LogWarning(
        "No database connection string configured (ConnectionStrings:Database); persistence and the database readiness check are disabled.");
}

// Health endpoints only (CORE-FND-004). Domain modules and their endpoints
// arrive with their own backlog stories.
app.MapLiveCoreHealthEndpoints();

app.Run();

/// <summary>
/// Marker for in-memory smoke tests (WebApplicationFactory).
/// </summary>
public partial class Program;
