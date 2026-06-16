using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Reads the optimistic-concurrency token of a tracked aggregate (CORE-DX-002), so a
/// handler can surface it as a weak <c>ETag</c> on reads and honor <c>If-Match</c> on
/// mutations (<see cref="EntityTag"/>).
///
/// <para>
/// The mutable aggregates map the PostgreSQL system column <c>xmin</c> as a read-only
/// row-version concurrency token (<see cref="LiveCoreDbContext"/>, CORE-CONC-001/006).
/// It is a SHADOW property — the aggregates carry no CLR version field — so its value is
/// read from the change tracker entry of the SAME scoped <see cref="LiveCoreDbContext"/>
/// the repository loaded the entity through. The token is reused exactly as the backend
/// already paid for it; nothing new is persisted.
/// </para>
///
/// <para>
/// The mapping is applied ONLY on the Npgsql provider (<c>xmin</c> is PostgreSQL
/// specific), so on the SQLite test provider the property is absent and this returns
/// <see langword="null"/> — there is no token to surface, and an <c>If-Match</c> against
/// a tokenless provider is treated fail-closed by <see cref="EntityTag.EvaluateIfMatch"/>.
/// In production (PostgreSQL) every mutable row always carries the token.
/// </para>
/// </summary>
internal static class EntityConcurrencyToken
{
    /// <summary>The shadow property / column name of the row-version token (<see cref="LiveCoreDbContext"/>).</summary>
    public const string PropertyName = "xmin";

    /// <summary>
    /// Returns the entity's current concurrency token as an opaque string, or
    /// <see langword="null"/> when the provider maps no token (the SQLite test
    /// provider). After a write commits, EF refreshes the token to the new database
    /// value, so reading it AFTER <c>SaveChanges</c> yields the post-mutation version.
    /// </summary>
    /// <param name="dbContext">
    /// The scoped context the entity is tracked in (the same instance the repository used).
    /// </param>
    /// <param name="entity">The tracked aggregate whose token to read.</param>
    public static string? TryRead(DbContext dbContext, object entity)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(entity);

        var entry = dbContext.Entry(entity);

        // The SQLite test provider maps no xmin row version; FindProperty avoids the
        // InvalidOperationException that Entry(entity).Property("xmin") would throw there.
        if (entry.Metadata.FindProperty(PropertyName) is null)
        {
            return null;
        }

        var value = entry.Property(PropertyName).CurrentValue;
        return value is null
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
