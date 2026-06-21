// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// The assembled result of a data-subject access/portability export (<see cref="PersonalDataExportService"/>,
/// CORE-PRIV-004, GDPR Art.15 access / Art.20 portability). It is the in-memory, tenant-scoped gathering of the
/// personal data Core holds about ONE data subject within ONE tenant: the subject's global identity profile plus
/// the personal-data-bearing records that reference the subject inside the resolved organization — their
/// organization membership, their workspace memberships, their participant records and the invitations addressed
/// to their email (the documented set from the story's acceptance criteria).
///
/// SCOPE (threat T5). Every collection here was read TENANT-SCOPED (the export route resolves a single tenant and
/// the repositories lead their predicates with <c>organization_id</c>), so this result never carries a record
/// from another tenant — an Owner/Admin exporting on the subject's behalf cannot learn of the subject's activity
/// in a tenant they do not control, and the subject reaches their data in other tenants only through those
/// tenants' own export routes. The single GLOBAL datum is <see cref="Subject"/>, the subject's own user-profile
/// identity (one deployment-wide row, no tenant) — but it is the subject's OWN identity, returned to the subject
/// themselves or to the tenant's data controller acting for them, which is exactly what GDPR access discloses.
///
/// This result holds the domain aggregates; the HTTP endpoint projects it to the safe
/// <see cref="PersonalDataExportResponse"/> DTO, which carries the subject's personal data (the point of an
/// access/portability export) but never an invitation token hash or any other secret (threats T6/T7). The
/// aggregates are read-only here; the export reads the subject's data back and changes nothing.
/// </summary>
/// <param name="OrganizationId">The tenant the export was authorized in (the scope of every tenant-scoped collection below).</param>
/// <param name="Subject">The subject's global user-profile identity (the only cross-tenant datum — the subject's own identity).</param>
/// <param name="OrganizationMembership">The subject's membership in this tenant, or <see langword="null"/> if none (defensive; the export route resolved one).</param>
/// <param name="WorkspaceMemberships">The subject's workspace memberships within this tenant (possibly empty).</param>
/// <param name="Participants">The subject's participant records within this tenant (possibly empty).</param>
/// <param name="Invitations">The invitations addressed to the subject's email within this tenant (possibly empty).</param>
/// <param name="PushSubscriptions">
/// The subject's Web Push subscriptions (CORE-PUSH-001). Like <see cref="Subject"/>, these are GLOBAL,
/// per-principal personal data (no tenant), disclosed to the subject themselves or the data controller acting
/// for them; possibly empty.
/// </param>
internal sealed record PersonalDataExport(
    Guid OrganizationId,
    UserProfile Subject,
    OrganizationMember? OrganizationMembership,
    IReadOnlyList<WorkspaceMember> WorkspaceMemberships,
    IReadOnlyList<Participant> Participants,
    IReadOnlyList<WorkspaceInvitation> Invitations,
    IReadOnlyList<PushSubscription> PushSubscriptions);
