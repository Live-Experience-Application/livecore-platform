// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Templates;

/// <summary>
/// Request body for creating a template (CORE-TMPL-001,
/// <c>POST /api/v1/organizations/{organizationSlug}/templates</c>, csv/api_routes.csv "Create template", roles
/// Owner/Admin).
///
/// The target organization is taken from the route PATH (<c>{organizationSlug}</c>), not the body, exactly like
/// the template-deletion route (CORE-LIFE-008): the slug is matched against the caller's token organization
/// claim AND a persisted organization membership by the tenant context resolver, and the create is then
/// authorized by the caller's Owner/Admin role in that organization (threat T5). So this body carries ONLY the
/// template content, never the tenant.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md, AGENTS.md): a template is reusable
/// vertical scaffolding stored as DATA — a stable dotted-slug <see cref="TemplateKey"/>, a positive
/// <see cref="Version"/> and the <see cref="Definition"/> JSON document (its template key plus the entity types
/// it defines) — all template-/host-supplied DATA, never inspected for vocabulary and never branched on (the
/// template boundary; no <c>if templateKey == ...</c> in Core source). The template's surrogate id is assigned
/// SERVER-SIDE (UUIDv7) by the aggregate; the client never supplies an id. The created template is ALWAYS
/// organization-scoped (never global). The (<see cref="TemplateKey"/>, <see cref="Version"/>) pair is unique
/// WITHIN the organization; a key+version the organization already holds is a <c>409 Conflict</c>.
/// </summary>
/// <param name="TemplateKey">
/// The template's stable, canonical natural key (canonicalized to a lower-case dotted-slug; a near-match or
/// re-cased value canonicalizes to the same key). Unique within the organization together with
/// <see cref="Version"/>.
/// </param>
/// <param name="Version">
/// The positive integer template version (part of the per-scope natural key). Optional: when omitted or null the
/// template is created at version <see cref="Template.MinVersion"/> (1); when provided it must be a positive
/// integer.
/// </param>
/// <param name="Definition">
/// The template content as a JSON document (template-/host-supplied data): a top-level <c>templateKey</c> plus a
/// non-empty <c>entityTypes</c> array whose entries each carry a valid key, display name and attribute schema. It
/// must be well-formed, structurally valid JSON within the size bound.
/// </param>
public sealed record CreateTemplateRequest(
    string? TemplateKey,
    int? Version,
    string? Definition);

/// <summary>
/// Response projection of a template (CORE-TMPL-001). It is the FULL, product-neutral, server-side view of a
/// generic template DEFINITION returned to the Owner/Admin roles on the create, list and by-id read routes.
///
/// There is exactly ONE template shape: a template is an authoring/registry artifact (a vertical's reusable
/// domain-to-Core scaffolding, the template boundary), not audience content, so all three routes are authorized
/// to Owner/Admin only and there is no host-vs-participant split — no audience role ever receives a template.
/// The shape is generic: the template's <see cref="TemplateKey"/> and <see cref="Definition"/> are stored DATA,
/// never inspected for vocabulary.
///
/// The DTO carries identifiers, the tenant boundary, the natural key, the version, the definition document and
/// the server timestamps. It carries NO internal authorization rationale — it never echoes why the caller was
/// allowed or how the tenant was resolved (docs/08; threat T7). Server timestamps are included per docs/08;
/// there is no resource version/ETag field (these create/list/read operations have no concurrent update to guard
/// at this surface — the <see cref="Version"/> field is the template's natural-key version, not a concurrency
/// token).
///
/// The routes only ever return ORGANIZATION-scoped templates (a GLOBAL template is never reachable through the
/// org-scoped lookups), so <see cref="OrganizationId"/> is always the owning tenant — <see cref="From"/>
/// rejects a global template defensively so the "org-scoped route never returns a global template" guarantee
/// holds even at the projection layer (threat T5).
/// </summary>
/// <param name="Id">Surrogate id of the template (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the template belongs to (always set; the routes never return a global template).</param>
/// <param name="TemplateKey">The template's canonical natural key (unique within the organization with the version).</param>
/// <param name="Version">The template's positive integer version.</param>
/// <param name="Definition">The template content (a JSON document).</param>
/// <param name="CreatedAt">When the template was created (UTC).</param>
/// <param name="UpdatedAt">When the template was last updated (UTC).</param>
public sealed record TemplateResponse(
    Guid Id,
    Guid OrganizationId,
    string TemplateKey,
    int Version,
    string Definition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="Template"/> aggregate into its response DTO. Only the generic, server-side fields
    /// are copied. The template MUST be organization-scoped (the routes guarantee this); a global template is
    /// rejected defensively so a global template can never be projected through the org-scoped route (threat
    /// T5).
    /// </summary>
    /// <exception cref="ArgumentException">The template is global (has no owning organization).</exception>
    public static TemplateResponse From(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.OrganizationId is not { } organizationId)
        {
            // Defensive: the org-scoped routes never load a global template, so this never happens in practice;
            // failing here keeps the "org-scoped route never returns a global template" guarantee structural.
            throw new ArgumentException(
                "A global template cannot be projected through the organization-scoped route.",
                nameof(template));
        }

        return new TemplateResponse(
            template.Id,
            organizationId,
            template.TemplateKey,
            template.Version,
            template.Definition,
            template.CreatedAt,
            template.UpdatedAt);
    }
}
