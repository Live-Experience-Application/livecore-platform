// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Templates;

/// <summary>
/// Outcome of the template create command (CORE-TMPL-001, the "Vertical Authoring and Read API Completeness"
/// epic), returned by <see cref="TemplateCreationService.CreateAsync"/>.
///
/// A template has a per-scope natural key (<see cref="Template.TemplateKey"/> + <see cref="Template.Version"/>),
/// so a creation is gated by per-scope key+version uniqueness the database enforces with the filtered unique
/// indexes in <see cref="TemplateConfiguration"/>. So there are exactly two outcomes — the key+version was free
/// in the caller's organization and a new template was created and audited (<see cref="Status"/> is
/// <see cref="TemplateCreationStatus.Created"/>, carrying the created <see cref="Template"/>), or the
/// organization already holds a template with that key and version
/// (<see cref="TemplateCreationStatus.Duplicate"/>, so nothing is created and no audit fact is written; the
/// existing template is left unchanged). The endpoint maps <see cref="TemplateCreationStatus.Duplicate"/> to a
/// <c>409 Conflict</c> (the organization already holds the key+version; the same key+version stays available in
/// another organization or in the global pool, threat T5) and <see cref="TemplateCreationStatus.Created"/> to a
/// <c>201 Created</c>. This mirrors <c>EntityTypeCreationResult</c>.
/// </summary>
internal readonly record struct TemplateCreationResult
{
    private TemplateCreationResult(TemplateCreationStatus status, Template? template)
    {
        Status = status;
        Template = template;
    }

    /// <summary>The outcome kind.</summary>
    public TemplateCreationStatus Status { get; }

    /// <summary>
    /// The created template when <see cref="Status"/> is <see cref="TemplateCreationStatus.Created"/>;
    /// otherwise <see langword="null"/> (no template was created).
    /// </summary>
    public Template? Template { get; }

    /// <summary>The key+version was free in the organization, so the template was created and audited.</summary>
    public static TemplateCreationResult Created(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new TemplateCreationResult(TemplateCreationStatus.Created, template);
    }

    /// <summary>
    /// A template with the given key and version already exists in the resolved organization, so nothing was
    /// created (the per-scope unique natural key).
    /// </summary>
    public static TemplateCreationResult Duplicate { get; } =
        new(TemplateCreationStatus.Duplicate, null);
}

/// <summary>The kind of <see cref="TemplateCreationResult"/>.</summary>
internal enum TemplateCreationStatus
{
    /// <summary>A new template was created, persisted and appended to the append-only audit log.</summary>
    Created = 1,

    /// <summary>
    /// The organization already holds a template with the requested key and version, so no template was created
    /// (the per-scope unique (<c>organization_id</c>, <c>template_key</c>, <c>version</c>) natural key).
    /// </summary>
    Duplicate = 2,
}
