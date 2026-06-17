// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json.Serialization;

namespace LiveCore.Api.Exports;

/// <summary>
/// The generic, product-neutral KIND of Core resource an <see cref="ExportManifest"/> inventories
/// (CORE-AUD-003, the workspace export manifest story of the "Audit, Export and Recap" epic). A workspace
/// export manifest is the table-of-contents of a produced workspace export, and each
/// <see cref="ExportManifestEntry"/> records how many resources of one of these kinds the export covered.
/// The names are the Core domain terms of docs/03_DOMAIN_LANGUAGE.md — never vertical product language
/// (AGENTS.md, csv/forbidden_core_terms.csv): a verticals app maps these to its own labels in its UI, but
/// the Core stores only the generic kind.
///
/// The catalog is deliberately the primary workspace-scoped CONTENT kinds a "workspace export" covers
/// (csv/database_tables.csv scopes each of these tables to <c>workspace</c>); it is NOT an exhaustive list
/// of every Core table. The manifest carries a COUNT per kind, never the content itself, so the manifest
/// reveals only the shape of an export, never any scene/content body (threats T7/T8 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// EXTENSIBLE WITHOUT A SCHEMA CHANGE: the kind is persisted as a real string column by its stable NAME, so
/// adding a member never reshapes the table (mirrors <c>AuditAction</c>, <c>ExportScope</c>). The integer
/// values are only in-memory storage discriminators (persisted by name, like every other enum in the
/// model — <c>ExportJobStatus</c>, <c>AssetStatus</c>, <c>SessionStatus</c>), carry no ordering meaning and
/// must not be compared with &gt;/&lt;.
///
/// Serialized over HTTP by its stable NAME too (the export read/download endpoint, CORE-EXP-001, surfaces it
/// directly on each <see cref="ExportManifestEntryView"/>), so the wire contract uses the name exactly as the
/// other Core enums do (docs/08_API_CONTRACTS.md "enums as stable string names"). The converter is attached
/// to the enum itself because it is surfaced directly (rather than pre-mapped to a string in its DTO),
/// mirroring <see cref="LiveCore.Api.Audit.AuditAction"/>; the EF string conversion is configured separately,
/// so this attribute affects JSON only.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportResourceKind
{
    /// <summary>
    /// A live/prepared run of the workspace (docs/03_DOMAIN_LANGUAGE.md "Session";
    /// csv/database_tables.csv: <c>sessions</c>, scope <c>workspace</c>).
    /// </summary>
    Session = 1,

    /// <summary>
    /// An ordered segment of a session (docs/03_DOMAIN_LANGUAGE.md "Scene"; csv/database_tables.csv:
    /// <c>scenes</c>, scope <c>workspace</c>).
    /// </summary>
    Scene = 2,

    /// <summary>
    /// A text/media/data unit shown or hidden by visibility rules (docs/03_DOMAIN_LANGUAGE.md
    /// "ContentBlock"; csv/database_tables.csv: <c>content_blocks</c>, scope <c>workspace</c>).
    /// </summary>
    ContentBlock = 3,

    /// <summary>
    /// A generic domain object (docs/03_DOMAIN_LANGUAGE.md "Entity"; csv/database_tables.csv:
    /// <c>entities</c>, scope <c>workspace</c>).
    /// </summary>
    Entity = 4,

    /// <summary>
    /// A session-facing participant of the workspace (docs/03_DOMAIN_LANGUAGE.md "Participant";
    /// csv/database_tables.csv: <c>participants</c>, scope <c>workspace</c>).
    /// </summary>
    Participant = 5,

    /// <summary>
    /// A stored file or media object's metadata (docs/03_DOMAIN_LANGUAGE.md "Asset";
    /// csv/database_tables.csv: <c>assets</c>, scope <c>workspace</c>). The manifest counts an asset's
    /// metadata record, never its private binary content (threat T4 "Asset leak").
    /// </summary>
    Asset = 6,
}
