// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Persistence contract for the user profile reference (CORE-ID-002). The
/// IdentityAccess module owns the <c>users</c> table; other modules access
/// profiles only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations).
///
/// Lookups address a profile only by its full OIDC identity pair
/// (issuer, subject). There is deliberately no lookup by subject alone:
/// subjects are unique only per issuer, so such a lookup could return a
/// foreign user's profile (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public interface IUserProfileRepository
{
    /// <summary>
    /// Finds the profile with exactly the given OIDC identity pair, or
    /// <see langword="null"/> when no such profile exists. Matching is
    /// exact (ordinal and case-sensitive) on both values.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The issuer or subject violates the corresponding identity invariants.
    /// Invalid values can never address a stored profile, so the lookup is
    /// rejected instead of silently returning nothing.
    /// </exception>
    Task<UserProfile?> FindByOidcIdentityAsync(string issuer, string subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the profile with exactly the given surrogate id, or <see langword="null"/> when no such profile
    /// exists (CORE-PRIV-001). The <c>users</c> table is GLOBAL scope (it references identities, not tenant
    /// data, so it carries no <c>organization_id</c>), so this lookup is by the profile's own surrogate key and
    /// is not tenant-scoped: a data subject is one identity across the whole deployment. It backs the
    /// data-subject erasure command, which has already resolved the subject's profile id from a tenant-scoped,
    /// authorized membership lookup before reaching this. The id is the row's own non-empty key, so it never
    /// addresses a foreign tenant's data (the table has no tenant); an empty id can never address a stored
    /// profile and is rejected.
    /// </summary>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    Task<UserProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new profile. Returns
    /// <see cref="UserProfileAddResult.DuplicateIdentity"/> when a profile
    /// with the same OIDC identity pair already exists (enforced by the
    /// unique database index, so concurrent first-time callers can never
    /// create two profiles for one identity).
    /// </summary>
    Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a profile previously loaded through this
    /// repository. The identity pair of a profile is immutable; only
    /// display metadata and the update timestamp change.
    /// </summary>
    Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently ERASES (hard-deletes) the given profile row — the data-subject erasure the schema's
    /// <c>ON DELETE SET NULL</c> / <c>ON DELETE CASCADE</c> foreign keys assume (CORE-PRIV-001, GDPR Art.17).
    /// This is deliberately a HARD delete, not a soft delete: the profile holds the data subject's PII (OIDC
    /// subject, email, display name), so erasure removes the row outright. Removing it lets the database honor
    /// the foreign keys other modules already declared against <c>users(id)</c>: <c>participants.user_id</c>,
    /// <c>assets.created_by</c> and <c>export_jobs.requested_by</c> SET NULL (the dependent records SURVIVE
    /// anonymized), while <c>organization_members.user_id</c> and <c>workspace_members.user_id</c> CASCADE (the
    /// access grants are revoked). The append-only <c>audit_logs</c> rows are NOT foreign keys (recorded facts),
    /// so the PII-free audit trail and its hash chain survive intact, which is what makes erasure reconcilable
    /// with the immutable audit log. The caller (the erasure command) is responsible for first anonymizing the
    /// PII the foreign keys do NOT reach (<c>participants.display_name</c>, <c>workspace_invitations.invited_email</c>)
    /// and for running this inside the erasure transaction so the whole erasure commits or rolls back atomically.
    /// </summary>
    /// <exception cref="ArgumentNullException">The profile is null.</exception>
    Task EraseAsync(UserProfile profile, CancellationToken cancellationToken);
}
