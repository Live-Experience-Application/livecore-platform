// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// Lifecycle status of an <see cref="Asset"/> (CORE-AST-001, the first story of the "Asset Storage and
/// Authorization" epic). An asset is the Core-owned "stored file or media object"
/// (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md: the Assets module owns "asset metadata",
/// the "storage adapter", "upload/download authorization" and "signed URL creation";
/// csv/database_tables.csv: table <c>assets</c>, module Assets, scope <c>workspace</c>, "Metadata
/// only"). This story models only the metadata + its persistence; the storage adapter (CORE-AST-002),
/// the upload intent flow (CORE-AST-003), the signed download URL flow (CORE-AST-004), linking
/// (CORE-AST-005) and the cleanup job (CORE-AST-006) are later stories.
///
/// The two states are the lifecycle behind the asset lifecycle in docs/12_STORAGE_ASSETS.md
/// ("Create upload intent -&gt; client uploads to storage -&gt; client confirms upload -&gt; Core
/// stores asset metadata"): an asset is registered <see cref="Pending"/> when its upload intent is
/// created (the object may not yet exist in storage), then becomes <see cref="Available"/> once the
/// upload is confirmed and its size and checksum are known. Both states are PRIVATE: an asset is never
/// publicly reachable in any state — it is reached only through an authorized, short-lived signed URL
/// after a permission check (docs/12_STORAGE_ASSETS.md "Security rules"; threat T4 in
/// docs/07_SECURITY_THREAT_MODEL.md). The <see cref="Pending"/>/<see cref="Available"/> distinction is
/// about whether the stored object is confirmed usable, NOT about public exposure.
///
/// The status is persisted by its stable name (not its numeric value), so the integers below are only
/// in-memory storage discriminators and carry no ordering meaning; they must not be compared with
/// &gt;/&lt;. The legal transitions are expressed by the <see cref="Asset"/> state machine
/// (<see cref="Asset.MarkAvailable"/>), not by integer order. A soft-delete / quarantine state for the
/// later cleanup job (CORE-AST-006) is deliberately not modeled here.
/// </summary>
public enum AssetStatus
{
    /// <summary>
    /// The asset's upload intent has been registered but the upload has not yet been confirmed: the
    /// object may not yet exist in storage, and its size and checksum are not yet known. Private and
    /// not yet downloadable. This is the only state from which the asset may be marked available.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The asset's upload has been confirmed: the object exists in storage and its size and checksum
    /// are recorded. Still PRIVATE — reachable only through an authorized, short-lived signed URL after
    /// a permission check (the signed download flow is CORE-AST-004), never through a public or static
    /// URL (threat T4).
    /// </summary>
    Available = 2,
}
