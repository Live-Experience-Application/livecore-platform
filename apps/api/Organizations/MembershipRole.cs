// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Generic, product-neutral role a subject holds inside one organization
/// (CORE-ID-004). The role set is taken verbatim from the Core authorization
/// matrix (docs/06_AUTHORIZATION_MATRIX.md, csv/authorization_matrix.csv) and
/// the generic role list in docs/01_PRODUCT_VISION_AND_SCOPE.md. Verticals may
/// rename these in their UI, but Core stores and enforces only these generic
/// roles (docs/03_DOMAIN_LANGUAGE.md, ADR 0003); no vertical-specific role
/// label ever appears here (see csv/forbidden_core_terms.csv).
///
/// The roles do NOT form a single linear privilege ladder. The authorization
/// matrix is non-linear: for example an <see cref="Auditor"/> may view the
/// audit log and workspace metadata that a <see cref="Participant"/> may not,
/// while a <see cref="Participant"/> may view its own visible feed that an
/// <see cref="Owner"/> may not. The integer values below are only stable
/// storage discriminators in the matrix's column order; they carry no "greater
/// than means more privileged" meaning and must not be compared with
/// &gt;/&lt;. The membership aggregate persists the role by name
/// (<see cref="OrganizationMemberConfiguration"/>), and authority is decided by
/// the explicit per-action policies that consume this role — those are a later
/// story (the workspace authorization policy stories); they are deliberately
/// not built here.
/// </summary>
public enum MembershipRole
{
    /// <summary>
    /// Owner standing: the top administrative role of the organization, with
    /// every administrative capability in the authorization matrix
    /// (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Owner = 1,

    /// <summary>
    /// Administrative standing: may manage members and organization settings
    /// (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Admin = 2,

    /// <summary>
    /// Host standing: full session and content control with limited member
    /// management (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Host = 3,

    /// <summary>
    /// Co-host standing: may run sessions and reveals but cannot manage
    /// members or organization settings (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    CoHost = 4,

    /// <summary>
    /// Participant audience standing: sees only its own visible feed and holds
    /// no host or management capability (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Participant = 5,

    /// <summary>
    /// Observer audience standing: may follow an observer feed but holds no
    /// host or management capability (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Observer = 6,

    /// <summary>
    /// Audit standing: may view workspace metadata and the audit log, but is
    /// not granted the audience or host capabilities of the other roles
    /// (docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    Auditor = 7,
}
