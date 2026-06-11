using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Design-time factory for the EF Core tooling (<c>dotnet ef migrations</c>).
///
/// Migration scaffolding only needs the model and the PostgreSQL provider;
/// it never opens a connection, so the connection string below is a
/// placeholder without credentials. At runtime the context is registered
/// only when <c>ConnectionStrings:Database</c> is configured (see
/// Program.cs); this factory is not part of the runtime path.
/// </summary>
internal sealed class LiveCoreDbContextFactory : IDesignTimeDbContextFactory<LiveCoreDbContext>
{
    public LiveCoreDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql("Host=localhost;Database=livecore-design-time")
            .Options;

        return new LiveCoreDbContext(options);
    }
}
