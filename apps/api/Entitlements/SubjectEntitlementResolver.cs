namespace LiveCore.Api.Entitlements;

/// <summary>
/// Server-side LOOKUP of a subject's effective entitlements (CORE-ENTL-002) — the read half of the subject
/// entitlement assignment and lookup story. It loads a subject's ACTIVE assignments and resolves them into the
/// subject's <see cref="EffectiveEntitlements"/>, the server-authoritative premium state.
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server entitlements". The
/// resolver is the single path that produces premium state, and it reads ONLY persisted subject entitlements:
/// there is no client input and no other source. A subject with no active assignment for an entitlement is not
/// entitled, and a revoked assignment is excluded by the repository's active-only read, so a refund /
/// cancellation / downgrade immediately removes the premium state (docs/21; fail-closed default). The resolved
/// set is the same whether it backs a later <c>GET /v1/me/entitlements</c> response or an internal feature
/// guard, so the two can never diverge.
///
/// The resolver is keyed purely by the (subject type, subject id) pair; it does not itself authorize the
/// caller. The endpoint that exposes a subject's entitlements resolves and authorizes the subject first (the
/// authenticated user for <c>/v1/me/entitlements</c>, or a tenant- and membership-checked workspace for the
/// workspace quota surface) and only then asks the resolver — exactly as the audit/export/recap projectors are
/// the reusable core their later endpoints sit on. The <c>/v1/me/entitlements</c> endpoint itself is a later
/// story (csv/mobile_store_api_routes.csv).
/// </summary>
public sealed class SubjectEntitlementResolver
{
    private readonly ISubjectEntitlementRepository _repository;

    public SubjectEntitlementResolver(ISubjectEntitlementRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>
    /// Resolves the given subject's effective entitlements from its active assignments. Returns
    /// <see cref="EffectiveEntitlements.Empty"/> when the subject holds no active entitlements (the fail-closed
    /// default — an unknown or unentitled subject is entitled to nothing).
    /// </summary>
    /// <param name="subjectType">The kind of subject to resolve.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public async Task<EffectiveEntitlements> ResolveAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var activeAssignments = await _repository
            .ListActiveBySubjectAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);

        return EffectiveEntitlements.FromActiveAssignments(activeAssignments);
    }
}
