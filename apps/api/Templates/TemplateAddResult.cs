// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Templates;

/// <summary>
/// Outcome of persisting a new <see cref="Template"/> (CORE-ENT-004).
///
/// Mirrors <c>EntityTypeAddResult</c>/<c>WorkspaceAddResult</c>: a template has a per-scope natural
/// key (<see cref="Template.TemplateKey"/> + <see cref="Template.Version"/>), so an insert can be
/// rejected as a DUPLICATE when the same scope already holds a template with the same key and
/// version. For a GLOBAL template the scope is "all global templates" (unique by template_key +
/// version where organization_id IS NULL); for an ORGANIZATION-scoped template the scope is that one
/// organization (unique by organization_id + template_key + version). The uniqueness is enforced by
/// the partial/filtered unique indexes in <c>TemplateConfiguration</c>, so a second writer can never
/// overwrite an existing template with its own row.
/// </summary>
public enum TemplateAddResult
{
    /// <summary>The template was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A template with the same key and version already exists in the same scope (the same
    /// organization, or the global pool). The existing template was not modified; the per-scope
    /// (template_key, version) pair is the natural key, so a second writer can never overwrite an
    /// existing template with its own row (enforced by the unique database index). The same
    /// key+version is still available in a different scope (a different organization, or
    /// global-vs-org; threat T5).
    /// </summary>
    Duplicate = 2,
}
