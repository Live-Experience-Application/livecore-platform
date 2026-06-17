// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Persistence contract for the subject entitlement assignment aggregate (CORE-ENTL-002). The Entitlements
/// module owns the <c>subject_entitlements</c> table; other modules access subject entitlements only through
/// this contract or the module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// Every read is scoped by the (<see cref="SubjectEntitlement.SubjectType"/>,
/// <see cref="SubjectEntitlement.SubjectId"/>) pair, so one subject's premium state can never be read through
/// another subject's id, and a user subject and a workspace subject that share a guid never collide (the
/// per-subject isolation that backs "User-visible premium state comes only from server entitlements"). There
/// is deliberately NO list-everything read: a subject entitlement is always addressed by its subject.
/// </summary>
public interface ISubjectEntitlementRepository
{
    /// <summary>
    /// Persists a new subject entitlement assignment. A subject holds each entitlement at most once, so an
    /// assignment that duplicates an existing (subject, entitlement) pair is reported as
    /// <see cref="SubjectEntitlementAddResult.DuplicateAssignment"/>; any other failure surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<SubjectEntitlementAddResult> AddAsync(SubjectEntitlement assignment, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an already-tracked assignment (a value re-grant, a revocation or a reinstatement).
    /// </summary>
    Task UpdateAsync(SubjectEntitlement assignment, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the subject's assignment of exactly the given entitlement definition, or <see langword="null"/>
    /// when the subject has none. The lookup is scoped by the subject pair then the entitlement, so it never
    /// returns another subject's assignment.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id or entitlement definition id is empty.</exception>
    Task<SubjectEntitlement?> FindBySubjectAndEntitlementAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid entitlementDefinitionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every assignment held by the given subject (active and revoked), in a deterministic order. Scoped
    /// by the subject pair, so it never returns another subject's assignments.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id is empty.</exception>
    Task<IReadOnlyList<SubjectEntitlement>> ListBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists only the ACTIVE assignments held by the given subject (revoked assignments are excluded), in a
    /// deterministic order. This is the premium-state read the resolver consumes: a revoked assignment resolves
    /// to not entitled (the fail-closed default). Scoped by the subject pair.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id is empty.</exception>
    Task<IReadOnlyList<SubjectEntitlement>> ListActiveBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);
}
