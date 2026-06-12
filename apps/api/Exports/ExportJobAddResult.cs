namespace LiveCore.Api.Exports;

/// <summary>
/// Outcome of persisting a new export job (CORE-AUD-002).
///
/// An export job has no natural key in this story — it is identified only by its surrogate id — so there
/// is no uniqueness constraint that an insert could violate and therefore no "duplicate" outcome. This
/// enum mirrors <c>AssetAddResult</c>, <c>EntityAddResult</c> and <c>SessionAddResult</c>: it carries a
/// single success value so the Add contract has the same shape as the other modules' and can grow a
/// second outcome later without a breaking signature change. Foreign-key violations (a non-existent
/// workspace, tenant or requesting user) surface as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a
/// result value.
/// </summary>
public enum ExportJobAddResult
{
    /// <summary>The export job was persisted.</summary>
    Added = 1,
}
