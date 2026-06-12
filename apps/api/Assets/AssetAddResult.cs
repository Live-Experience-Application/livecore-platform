namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of persisting a new asset (CORE-AST-001).
///
/// An asset has no natural key in this story — it is identified only by its surrogate id — so there is
/// no uniqueness constraint that an insert could violate and therefore no "duplicate" outcome. This enum
/// mirrors <c>EntityAddResult</c> and <c>SessionAddResult</c>: it carries a single success value so the
/// Add contract has the same shape as the other modules' and can grow a second outcome later without a
/// breaking signature change. A storage-object-key uniqueness guarantee (so two metadata rows can never
/// alias the same stored object) belongs to the upload-intent flow that mints object keys (CORE-AST-003)
/// and would introduce a second outcome then; foreign-key violations (a non-existent workspace, tenant
/// or creating user) surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the
/// repository rather than as a result value.
/// </summary>
public enum AssetAddResult
{
    /// <summary>The asset was persisted.</summary>
    Added = 1,
}
