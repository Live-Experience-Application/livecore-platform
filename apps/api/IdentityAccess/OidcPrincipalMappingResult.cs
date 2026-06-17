// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics.CodeAnalysis;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Outcome of mapping token claims to an <see cref="OidcPrincipal"/>
/// (CORE-ID-001): either a fully valid principal or a typed error, never a
/// partially trusted principal.
/// </summary>
public sealed class OidcPrincipalMappingResult
{
    private OidcPrincipalMappingResult(OidcPrincipal? principal, OidcPrincipalMappingError error)
    {
        Principal = principal;
        Error = error;
    }

    /// <summary>
    /// The mapped principal, or <see langword="null"/> when mapping failed.
    /// </summary>
    public OidcPrincipal? Principal { get; }

    /// <summary>
    /// Why mapping failed; <see cref="OidcPrincipalMappingError.None"/> on
    /// success.
    /// </summary>
    public OidcPrincipalMappingError Error { get; }

    /// <summary>Whether a valid principal was produced.</summary>
    [MemberNotNullWhen(true, nameof(Principal))]
    public bool Succeeded => Error == OidcPrincipalMappingError.None;

    internal static OidcPrincipalMappingResult Success(OidcPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new OidcPrincipalMappingResult(principal, OidcPrincipalMappingError.None);
    }

    internal static OidcPrincipalMappingResult Failure(OidcPrincipalMappingError error)
    {
        if (error == OidcPrincipalMappingError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error), error, "A failure result requires a specific error.");
        }

        return new OidcPrincipalMappingResult(null, error);
    }
}
