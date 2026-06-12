namespace LiveCore.Api.Recaps;

/// <summary>
/// Persistence contract for the recap aggregate (CORE-AUD-004). The Recaps module owns the <c>recaps</c>
/// table; other modules access recaps only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations; docs/05_MODULE_CONTRACTS.md: the Recaps
/// module owns "session recaps").
///
/// Every lookup is explicitly scoped by BOTH tenant boundaries: the caller passes the organization id and the
/// workspace id, and a recap is only ever returned when it belongs to exactly that (organization, workspace)
/// pair. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a recap is never returned through a foreign
/// organization's id even when the workspace and ids are correct, and never through a foreign workspace's id
/// even when the organization and ids are correct. There is deliberately no lookup of a recap by id alone and
/// no list-everything read method, so one workspace's recap can never be read through another workspace's id
/// and a recap in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// A recap is write-once (the produced output of a session), so there is no update or delete method — only an
/// append and tenant-scoped reads (mirrors the append-only audit log and the write-once export manifest).
/// </summary>
public interface IRecapRepository
{
    /// <summary>
    /// Appends a new recap. A recap is write-once, so there is no update path; the surrogate id is generated
    /// non-empty by the aggregate, so an insert simply persists the row. Foreign-key violations (a
    /// non-existent session, workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task AppendAsync(Recap recap, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the recap with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such recap exists there. The organization and workspace both scope the
    /// lookup, so a recap that exists under another organization's or workspace's id is never returned, even
    /// when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or recap id is empty. An empty id can never address a stored recap,
    /// so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<Recap?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the recaps produced for exactly the given session WITHIN the given organization and workspace, in
    /// produced order (the time-ordered surrogate id). A session may have produced more than one recap over
    /// time, so this returns all of them (possibly empty). The lookup is tenant- and workspace-scoped, so a
    /// session's recaps under another organization's or workspace's id are never returned even when the
    /// session id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// The organization id, workspace id or session id is empty.
    /// </exception>
    Task<IReadOnlyList<Recap>> ListBySessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        CancellationToken cancellationToken);
}
