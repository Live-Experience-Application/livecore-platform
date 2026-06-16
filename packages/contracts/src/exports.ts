import type { ExportResourceKind, ExportScope } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Exports module contracts (CORE-EXP-001). A completed workspace export's produced
 * artifact is its manifest — the per-kind table of contents of what the export
 * covered (counts only, never any exported scene/content body; threats T7/T8).
 * The Core stores no separate export blob in object storage, so the artifact is
 * delivered as an authorized stream — the role-projected manifest in the
 * authenticated, authorized response body — never through a public/static URL
 * (docs/12_STORAGE_ASSETS.md; threats T4/T8). Access is authorized server-side
 * before the artifact is produced; a non-authoring role is denied.
 */

/** One inventory line of a full export manifest view: a count per resource kind. */
export interface ExportManifestEntryView {
  /** The generic kind of resource counted. */
  kind: ExportResourceKind;
  /** How many resources of `kind` the export covered (non-negative). */
  itemCount: number;
}

/**
 * The full, host/metadata-facing artifact of `GET /api/v1/exports/{exportId}` —
 * the role-projected view returned to an authorized downloader (the "Export
 * workspace" roles Owner/Admin/Host). It carries the per-kind inventory and the
 * tenant/workspace boundary identifiers, never any exported content.
 */
export interface ExportManifestView {
  /** Surrogate id of the manifest. */
  id: Uuid;
  /** The export job that produced this manifest. */
  exportJobId: Uuid;
  /** Tenant the manifest belongs to. */
  organizationId: Uuid;
  /** Workspace the manifest belongs to. */
  workspaceId: Uuid;
  /** The explicit export scope of the producing job (always `Workspace`). */
  scope: ExportScope;
  /** The manifest format version. */
  manifestVersion: number;
  /** When the manifest was produced (UTC). */
  generatedAt: IsoDateTimeString;
  /** The per-kind inventory, in ascending-kind order. */
  entries: ExportManifestEntryView[];
  /** The total number of resources the export covered across all kinds. */
  totalItemCount: number;
}

/**
 * The audience-safe, host-only-field-stripped artifact shape: the manifest's
 * non-sensitive identity and scope only — no inventory, no boundary identifiers.
 * The export read/download route authorizes the artifact to the "Export workspace"
 * roles, so an audience caller is denied (403) rather than handed this shape; it
 * is the contract the role-based projection falls closed to and is exported so a
 * consumer can model the projected union exhaustively.
 */
export interface ExportManifestSummaryView {
  /** Surrogate id of the manifest; a non-sensitive handle. */
  id: Uuid;
  /** The explicit export scope of the producing job. */
  scope: ExportScope;
}

/**
 * The role-projected artifact returned by `GET /api/v1/exports/{exportId}`: the
 * full host/metadata view for an authorized downloader, or the host-only-field
 * -stripped summary the projection falls closed to for any other role.
 */
export type ExportArtifactResponse =
  | ExportManifestView
  | ExportManifestSummaryView;
