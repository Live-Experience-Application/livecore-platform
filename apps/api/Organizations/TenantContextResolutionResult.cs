using System.Diagnostics.CodeAnalysis;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Outcome of resolving an authenticated principal and a target organization
/// into a tenant context (CORE-ID-005): either a fully verified, trusted
/// <see cref="TenantContext"/> or a typed fail-closed denial, never a partially
/// trusted context (mirrors the fail-closed result of CORE-ID-001's
/// <c>OidcPrincipalMappingResult</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class TenantContextResolutionResult
{
    private TenantContextResolutionResult(TenantContext? context, TenantContextResolutionError error)
    {
        Context = context;
        Error = error;
    }

    /// <summary>
    /// The resolved tenant context, or <see langword="null"/> when resolution
    /// was denied.
    /// </summary>
    public TenantContext? Context { get; }

    /// <summary>
    /// Why resolution was denied; <see cref="TenantContextResolutionError.None"/>
    /// on success.
    /// </summary>
    public TenantContextResolutionError Error { get; }

    /// <summary>Whether a trusted tenant context was produced.</summary>
    [MemberNotNullWhen(true, nameof(Context))]
    public bool Succeeded => Error == TenantContextResolutionError.None;

    internal static TenantContextResolutionResult Success(TenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TenantContextResolutionResult(context, TenantContextResolutionError.None);
    }

    internal static TenantContextResolutionResult Denied(TenantContextResolutionError error)
    {
        if (error == TenantContextResolutionError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error), error, "A denial requires a specific error.");
        }

        return new TenantContextResolutionResult(null, error);
    }
}
