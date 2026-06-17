// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the asset-link command (<see cref="AssetLinkService.LinkAsync"/>, CORE-AST-005). The
/// command runs AFTER the endpoint has authorized the caller; this result tells the endpoint how to
/// respond without it re-implementing the verification logic.
/// </summary>
public enum AssetLinkOutcome
{
    /// <summary>The link was created (the endpoint returns 201 with the link).</summary>
    Linked = 1,

    /// <summary>
    /// The asset is already linked to this target (the per-workspace unique key rejected a duplicate);
    /// the endpoint returns 409. No second link was created.
    /// </summary>
    AlreadyLinked = 2,

    /// <summary>
    /// The target content block / entity does not exist in the asset's own workspace, so it cannot be
    /// linked; the endpoint hides it as 404 (the same workspace-scoped existence rule the rest of the API
    /// uses; threats T1/T5). No link was created.
    /// </summary>
    TargetNotFound = 3,
}

/// <summary>
/// The result of <see cref="AssetLinkService.LinkAsync"/>: the <see cref="Outcome"/> and, when a link was
/// actually created, the created <see cref="Link"/> (otherwise <see langword="null"/>).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Link">The created link when <see cref="Outcome"/> is <see cref="AssetLinkOutcome.Linked"/>; otherwise null.</param>
public readonly record struct AssetLinkResult(AssetLinkOutcome Outcome, AssetLink? Link)
{
    /// <summary>A successful link creation.</summary>
    public static AssetLinkResult Created(AssetLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return new AssetLinkResult(AssetLinkOutcome.Linked, link);
    }

    /// <summary>The asset was already linked to the target.</summary>
    public static AssetLinkResult AlreadyLinked() => new(AssetLinkOutcome.AlreadyLinked, Link: null);

    /// <summary>The target resource was not found in the asset's workspace.</summary>
    public static AssetLinkResult TargetNotFound() => new(AssetLinkOutcome.TargetNotFound, Link: null);
}
