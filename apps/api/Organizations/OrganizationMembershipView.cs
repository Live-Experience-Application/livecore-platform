// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Read projection pairing one <see cref="Organization"/> with the generic
/// <see cref="MembershipRole"/> the subject holds in it (CORE-API-002). It is the
/// shape <see cref="IOrganizationRepository.ListMembershipsByMemberAsync"/>
/// returns so a caller can read a subject's tenants together with the subject's
/// role in each in a single query — the principal-context read backing
/// <c>GET /api/v1/me</c> (csv/api_routes.csv: "Returns principal context only").
///
/// It is the role-bearing sibling of <see cref="IOrganizationRepository.ListByMemberAsync"/>
/// (which returns the organizations without the role). It is a plain read model,
/// not an aggregate: it owns no behavior and is never persisted. The same
/// deny-by-default, tenant-isolation rule applies — it only ever carries an
/// organization the subject is genuinely a member of, and the caller still
/// intersects the result with the principal's organization claims so the
/// principal context never exposes a tenant the token does not assert (threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Organization">The tenant the subject is a member of.</param>
/// <param name="Role">The generic role the subject holds in that organization.</param>
public sealed record OrganizationMembershipView(Organization Organization, MembershipRole Role);
