// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Recaps;

/// <summary>
/// FULL, host/metadata-facing projection of a <see cref="Recap"/> (CORE-AUD-004). It is the complete,
/// product-neutral, server-side view of a session recap returned to the host-content / metadata-entitled
/// workspace roles — the FULL half of the role-based recap projection, which honors the epic acceptance
/// criterion "Audit/export/recap are generic and authorized" and the host-vs-audience separation for the
/// host-only recap body (threat T2 visibility leak in docs/07_SECURITY_THREAT_MODEL.md; the "View host-only
/// content" row of docs/06_AUTHORIZATION_MATRIX.md).
///
/// This is one of the TWO distinct recap view types (docs/08_API_CONTRACTS.md DTO design rule "Host DTOs and
/// Participant DTOs are different"): the full view — the ONLY view that carries the recap
/// <see cref="Summary"/> body — while <see cref="RecapSummaryView"/> is the host-only-field-stripped,
/// audience-safe view. <see cref="RecapProjection"/> selects between them from the caller's workspace role;
/// this type is NEVER produced for an audience (Participant/Observer) role, because the recap body is host
/// content that the audience may not see until a separate reveal (docs/09_EVENT_CATALOG.md
/// <c>RecapGenerated</c>: "Participant-visible only after separate reveal").
///
/// The view is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): identifiers, the tenant,
/// workspace and session boundaries, the producer, the recap format version, the produced timestamp and the
/// generic recap body. It carries NO internal authorization rationale (docs/08).
/// </summary>
/// <param name="Id">Surrogate id of the recap (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the recap belongs to.</param>
/// <param name="WorkspaceId">Workspace the recap belongs to.</param>
/// <param name="SessionId">The session the recap summarizes.</param>
/// <param name="GeneratedByUserProfileId">The user that produced the recap, or <see langword="null"/> for a system/anonymized recap.</param>
/// <param name="RecapVersion">The recap format version.</param>
/// <param name="Summary">The generic recap body (host content).</param>
/// <param name="GeneratedAt">When the recap was produced (UTC).</param>
public sealed record RecapView(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid? GeneratedByUserProfileId,
    int RecapVersion,
    string Summary,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// Projects a <see cref="Recap"/> aggregate into its full view. Only the generic fields are copied (every
    /// field of the aggregate except the materialization plumbing); the recap body is included because the
    /// full view is host-only.
    /// </summary>
    public static RecapView From(Recap recap)
    {
        ArgumentNullException.ThrowIfNull(recap);

        return new RecapView(
            recap.Id,
            recap.OrganizationId,
            recap.WorkspaceId,
            recap.SessionId,
            recap.GeneratedByUserProfileId,
            recap.RecapVersion,
            recap.Summary,
            recap.GeneratedAt);
    }
}

/// <summary>
/// AUDIENCE-safe, host-only-field-STRIPPED projection of a <see cref="Recap"/> (CORE-AUD-004). It is the
/// second of the TWO recap view types: the shape produced for the audience workspace roles instead of the
/// full <see cref="RecapView"/>. docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs are
/// different" and "Participant DTOs must not contain hidden content fields", so it is a SEPARATE record type,
/// not a reused/optional-field variant of the full view.
///
/// EXACT field set and the justification for each OMISSION (a deliberately minimal, audience-safe set that
/// honors the host-only-content boundary; the recap body is "Participant-visible only after separate reveal"
/// per docs/09_EVENT_CATALOG.md):
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle; it never authorizes
///   access on its own (every lookup is tenant+workspace scoped, threat T1/T5).</item>
///   <item><c>SessionId</c> — KEPT: which session this recap belongs to is the audience-relevant identity —
///   a participant already legitimately holds its own session id (it connects to the session by that id), so
///   echoing it leaks nothing. It is the recap's audience-facing "what this recaps", the coarse analogue of
///   the export summary's <c>Scope</c>.</item>
///   <item><c>Summary</c> — OMITTED: the recap BODY is host content. This is the crucial omission — the
///   audience never receives the body until a separate reveal (docs/09_EVENT_CATALOG.md; threat T2 visibility
///   leak).</item>
///   <item><c>GeneratedByUserProfileId</c> — OMITTED: WHO produced the recap is host/audit metadata, not
///   audience-relevant; revealing another user's identity to the audience is stripped (threats T7).</item>
///   <item><c>OrganizationId</c> / <c>WorkspaceId</c> — OMITTED: the internal tenant/workspace boundary
///   identifiers, stripped because docs/08 forbids hidden/internal fields in participant DTOs and they are
///   isolation boundaries (threat T5).</item>
///   <item><c>RecapVersion</c> / <c>GeneratedAt</c> — OMITTED: host/operator metadata, kept out of the
///   audience shape exactly as the audience scene shape omits the host preparation timestamps.</item>
/// </list>
///
/// It carries NO host-only fields, NO recap body and NO internal authorization rationale (docs/08; threats
/// T2/T7). Adding any host-only field — most importantly the <c>Summary</c> body — here would be caught by the
/// projection tests that assert its EXACT property set.
/// </summary>
/// <param name="Id">Surrogate id of the recap (UUIDv7); a non-sensitive handle.</param>
/// <param name="SessionId">The session the recap belongs to; the audience-relevant identity.</param>
public sealed record RecapSummaryView(Guid Id, Guid SessionId)
{
    /// <summary>
    /// Projects a <see cref="Recap"/> aggregate into its audience-safe summary view. Only the minimal
    /// audience-relevant fields (id, session) are copied; the recap body, the producer, the tenant/workspace
    /// boundary ids, the format version and the produced timestamp are deliberately NOT copied (see the type
    /// summary).
    /// </summary>
    public static RecapSummaryView From(Recap recap)
    {
        ArgumentNullException.ThrowIfNull(recap);
        return new RecapSummaryView(recap.Id, recap.SessionId);
    }
}

/// <summary>
/// Role-based recap PROJECTOR (CORE-AUD-004) — the pure mapping that honors the epic acceptance criterion
/// "Audit/export/recap are generic and authorized" and keeps the host-only recap body away from the audience
/// (threat T2 visibility leak in docs/07_SECURITY_THREAT_MODEL.md; docs/09_EVENT_CATALOG.md
/// <c>RecapGenerated</c>: "Participant-visible only after separate reveal"). It selects WHICH of the two
/// distinct recap view shapes (<see cref="RecapView"/> full vs <see cref="RecapSummaryView"/> audience-safe) a
/// caller receives, from the caller's WORKSPACE <see cref="MembershipRole"/>.
///
/// This projector decides the SHAPE a viewer receives, NOT whether a viewer may access recaps at all: the HTTP
/// route that returns a recap and its server-side access authorization (and the separate participant reveal of
/// a recap) are later stories, exactly as CORE-AUD-002's <c>ExportJobProjection</c> and CORE-AUD-003's
/// <c>ExportManifestProjection</c> left the export endpoint to a later story. This story models the recap and
/// its role-based view shapes; the projector is the reusable, fail-closed core those later endpoints sit on.
///
/// Role -> shape mapping, decided STRICTLY from the host-content / metadata roles of
/// docs/06_AUTHORIZATION_MATRIX.md — the SAME crisp partition <c>ExportManifestProjection</c> /
/// <c>ExportJobProjection</c> use ("View workspace metadata" = yes), so the view-shape decision is not
/// reinvented: Owner/Admin/Host/CoHost (host content) and Auditor (audit-only access to host content) get the
/// full view WITH the body; Participant/Observer get the stripped summary view WITHOUT the body.
/// <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage discriminators only and must
/// never be compared with &gt;/&lt;), so the classification is an EXACT set-membership check, never an
/// ordering comparison. An undefined enum value fails closed to the stripped summary shape (deny-by-default:
/// an unrecognized role is never granted the broader full view that carries the host-only body).
/// </summary>
internal static class RecapProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full <see cref="RecapView"/> (with the recap body)
    /// rather than the stripped <see cref="RecapSummaryView"/>. EXACT set membership over the host-content and
    /// metadata roles of docs/06: Owner, Admin, Host, CoHost and Auditor. Any other role (the audience roles
    /// Participant/Observer, and any undefined value) is denied the full view and falls closed to the summary
    /// view — so the host-only recap body never reaches them. Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ReceivesFullView(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Host
            or MembershipRole.CoHost
            or MembershipRole.Auditor;

    /// <summary>
    /// Projects the given recap into the role-appropriate view, boxed as <see cref="object"/> so a later
    /// endpoint can return either the full (<see cref="RecapView"/>) or the summary
    /// (<see cref="RecapSummaryView"/>) shape as the response body.
    /// </summary>
    public static object Project(Recap recap, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(recap);

        return ReceivesFullView(role)
            ? RecapView.From(recap)
            : RecapSummaryView.From(recap);
    }
}
