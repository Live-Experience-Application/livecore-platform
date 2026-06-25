// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics.CodeAnalysis;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Outcome of the CLAIM-ONLY tenant resolution path (CORE-INV-003): either the resolved
/// <see cref="Organization"/> the caller's token asserts a claim for, or a typed fail-closed denial.
///
/// This is the DISTINCT, narrower counterpart of <see cref="TenantContextResolutionResult"/>. The full
/// <see cref="TenantContextResolver.ResolveAsync"/> requires a persisted organization membership and produces a
/// trusted <see cref="TenantContext"/> (organization + subject + role); it returns
/// <see cref="TenantContextResolutionError.NoMembership"/> for a brand-new or cross-org invitee who has no
/// membership yet, which would make a workspace invitation un-acceptable. The invitation-accept route therefore
/// resolves the tenant from the token org claim ALONE (plus, separately, the valid invitation bearer token),
/// without requiring a pre-existing membership, so a genuinely new invitee can onboard in one step (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md is still honoured: the token org claim is the identity provider's assertion
/// that the caller's token is scoped to the tenant, and the accept route additionally gates on a valid
/// invitation token matched by hash — never on an email match).
///
/// Because no membership is consulted, this result carries NO role and is never a trusted
/// <see cref="TenantContext"/>: it names only the resolved organization, and the accept route still proves the
/// caller's entitlement to act through the invitation token it then redeems. It is never produced for a foreign,
/// unclaimed or non-existent tenant (fail-closed), mirroring <see cref="TenantContextResolutionResult"/>.
/// </summary>
public sealed class ClaimScopedTenantResolutionResult
{
    private ClaimScopedTenantResolutionResult(Organization? organization, TenantContextResolutionError error)
    {
        Organization = organization;
        Error = error;
    }

    /// <summary>
    /// The resolved organization, or <see langword="null"/> when resolution was denied. It carries no role: the
    /// claim-only path proves the token is scoped to the tenant, not the caller's standing in it.
    /// </summary>
    public Organization? Organization { get; }

    /// <summary>
    /// Why resolution was denied; <see cref="TenantContextResolutionError.None"/> on success. The reusable
    /// resolution-error vocabulary is shared with the full resolver; the claim-only path only ever reports
    /// <see cref="TenantContextResolutionError.NotAUser"/>,
    /// <see cref="TenantContextResolutionError.ClaimMismatch"/> or
    /// <see cref="TenantContextResolutionError.OrganizationNotFound"/> (it never reaches the subject/membership
    /// checks).
    /// </summary>
    public TenantContextResolutionError Error { get; }

    /// <summary>Whether the organization was resolved from the token claim.</summary>
    [MemberNotNullWhen(true, nameof(Organization))]
    public bool Succeeded => Error == TenantContextResolutionError.None;

    internal static ClaimScopedTenantResolutionResult Success(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return new ClaimScopedTenantResolutionResult(organization, TenantContextResolutionError.None);
    }

    internal static ClaimScopedTenantResolutionResult Denied(TenantContextResolutionError error)
    {
        if (error == TenantContextResolutionError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error), error, "A denial requires a specific error.");
        }

        return new ClaimScopedTenantResolutionResult(null, error);
    }
}
